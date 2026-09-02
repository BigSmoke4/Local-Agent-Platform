using Microsoft.Build.Locator;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Web.Data;
using Platform.Web.Hubs;
using Platform.Web.Models;
using Platform.Web.Services;
using Platform.Web.Services.Tools;
using Platform.Web.Services.Verification;
using Platform.Web.Services.Telemetry;
using Platform.Web.Services.CodeIntelligence;
using Platform.Web.Services.Memory;
using Platform.Web.Services.Routing;
using Platform.Web.Services.Autonomy;
using Platform.Web.BackgroundServices;
using Serilog;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// MSBuildLocator MUST register before any Microsoft.Build/MSBuildWorkspace
// types are touched anywhere in the process — this is a real constraint of
// how MSBuildWorkspace resolves assemblies, not paranoia. Doing this lazily
// inside SemanticCodeGraphService would be too late if anything else in the
// dependency graph loaded those assemblies first.
if (!MSBuildLocator.IsRegistered)
{
    try
    {
        MSBuildLocator.RegisterDefaults();
    }
    catch (Exception ex)
    {
        // No .NET SDK/MSBuild found on this host. SemanticCodeGraphService
        // will report this honestly per-call rather than crash startup —
        // the rest of the platform doesn't depend on it.
        Console.Error.WriteLine($"MSBuildLocator.RegisterDefaults failed at startup: {ex.Message}");
    }
}

var builder = WebApplication.CreateBuilder(args);

// ---- LSP stdio mode ----
// Real alternate entry point: `dotnet run -- --lsp` runs the actual LSP
// server over stdin/stdout instead of the web host, so a generic LSP
// client (VS Code via a small client extension, Neovim's built-in LSP
// client, etc.) can launch this as a language server subprocess the same
// way it would launch any other language server.
if (args.Contains("--lsp"))
{
    var lspServices = new ServiceCollection();
    lspServices.AddLogging(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)); // stdout is reserved for LSP frames
    lspServices.AddSingleton<CommandPolicyEngine>();
    lspServices.AddSingleton<IConfiguration>(builder.Configuration);
    lspServices.AddScoped<TerminalTool>();
    lspServices.AddScoped<BuildTool>();
    lspServices.AddScoped<Platform.Web.Services.Lsp.LspServer>();

    using var lspProvider = lspServices.BuildServiceProvider();
    using var scope = lspProvider.CreateScope();
    var lspServer = scope.ServiceProvider.GetRequiredService<Platform.Web.Services.Lsp.LspServer>();

    await lspServer.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
    return;
}

// ---- Structured logging (Serilog) ----
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/platform-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();
builder.Host.UseSerilog();

// ---- Database ----
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

builder.Services.AddDbContext<PlatformDbContext>(options =>
    options.UseNpgsql(connectionString));

// ---- Identity (real local auth, no cloud dependency) ----
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<PlatformDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

// ---- Local model runtime (Ollama adapter behind IModelProvider) ----
var ollamaBaseUrl = builder.Configuration["ModelRuntime:OllamaBaseUrl"] ?? "http://localhost:11434/";
builder.Services.AddHttpClient<IModelProvider, OllamaModelProvider>(client =>
{
    client.BaseAddress = new Uri(ollamaBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(5);
});

// ---- Tools ----
builder.Services.AddSingleton<CalculatorTool>();
builder.Services.AddSingleton<CommandPolicyEngine>();
builder.Services.AddScoped<FileReadTool>();
builder.Services.AddScoped<TerminalTool>();
builder.Services.AddScoped<GitTool>();
builder.Services.AddScoped<BuildTool>();
builder.Services.AddScoped<TestTool>();
builder.Services.AddScoped<ProjectStructureTool>();
builder.Services.AddScoped<SearchSymbolTool>();
builder.Services.AddScoped<DependencyAnalysisTool>();
builder.Services.AddScoped<FileWriteTool>();
builder.Services.AddScoped<DiffTool>();
builder.Services.AddScoped<SafeFileEditService>();

// ---- Code intelligence ----
builder.Services.AddSingleton<RoslynSyntaxIndexer>();
builder.Services.AddScoped<RepositoryIndexService>();
builder.Services.AddSingleton<SemanticCodeGraphService>();
builder.Services.AddScoped<Platform.Web.Services.CodeIntelligence.SemanticRepairTargetResolver>();

// ---- Memory, routing, autonomy ----
builder.Services.AddScoped<MemoryService>();
builder.Services.AddScoped<Platform.Web.Services.Memory.PgVectorSimilarityService>();
builder.Services.AddScoped<ModelRouter>();
builder.Services.AddScoped<AutonomyService>();
builder.Services.AddSingleton<Platform.Web.Services.IdeIntegration.IIdeIntegrationProvider, Platform.Web.Services.IdeIntegration.GenericHttpIdeProvider>();
builder.Services.AddScoped<Platform.Web.Services.Planning.PlannerService>();
builder.Services.AddScoped<Platform.Web.Services.Planning.PlanExecutionService>();

// ---- Verification ----
builder.Services.AddScoped<VerificationEngine>();
builder.Services.AddScoped<ReviewerService>();

// ---- Telemetry & real-time ----
builder.Services.AddSingleton<HardwareTelemetryProvider>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IAgentEventBroadcaster, AgentEventBroadcaster>();
builder.Services.AddHostedService<HardwareTelemetryBackgroundService>();

// ---- Health checks ----
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgresql");

// ---- OpenTelemetry: real ASP.NET Core + HTTP client + Npgsql instrumentation.
// Exports to console always (visible immediately, no external collector
// needed); also exports via OTLP if Telemetry:OtlpEndpoint is configured —
// real distributed tracing, not just structured logs relabeled.
var otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("LocalAgentPlatform"))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation() // real spans for outbound calls to Ollama
            .AddNpgsql()                    // real spans for PostgreSQL queries
            .AddConsoleExporter();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracing.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        }
    });

// ---- MVC + API ----
builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready");
app.MapHub<AgentHub>("/hubs/agent");

app.Run();

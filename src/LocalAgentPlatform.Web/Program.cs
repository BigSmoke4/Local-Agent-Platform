using LocalAgentPlatform.Modules.Models.Application.Services;
using LocalAgentPlatform.Modules.Models.Infrastructure.Ollama;
using LocalAgentPlatform.Modules.Models.Infrastructure.Telemetry;
using LocalAgentPlatform.Modules.Agent.Application.Services;
using LocalAgentPlatform.Modules.Memory.Application.Services;
using LocalAgentPlatform.Modules.RepositoryAnalysis.Application.Services;
using LocalAgentPlatform.Modules.RepositoryAnalysis.Infrastructure;
using LocalAgentPlatform.Modules.Tools.Application.Services;
using LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;
using LocalAgentPlatform.Modules.Verification.Application.Services;
using LocalAgentPlatform.Modules.Verification.Infrastructure.Security;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Kernel.BackgroundWork;
using LocalAgentPlatform.Shared.Kernel.Models;
using LocalAgentPlatform.Shared.Kernel.Telemetry;
using LocalAgentPlatform.Shared.Kernel.Tools;
using LocalAgentPlatform.Web.BackgroundServices;
using LocalAgentPlatform.Web.Hubs;
using LocalAgentPlatform.Web.Infrastructure;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---- Structured logging (Serilog) ----
builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// ---- Configuration ----
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));

// ---- Data layer (PostgreSQL via EF Core) ----
var connectionString = builder.Configuration.GetConnectionString("PlatformDb")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:PlatformDb in configuration.");

builder.Services.AddDbContext<PlatformDbContext>(opts =>
    opts.UseNpgsql(connectionString, npg => npg.EnableRetryOnFailure()));

// ---- Model provider abstraction (IModelProvider -> Ollama adapter) ----
// This is the only place a concrete runtime is chosen. Swapping runtimes means
// registering a different IModelProvider implementation here — nothing else changes.
builder.Services.AddHttpClient<IModelProvider, OllamaModelProvider>();

// ---- Hardware telemetry ----
builder.Services.AddSingleton<IHardwareTelemetryProvider, ProcHardwareTelemetryProvider>();

// ---- Model registry (Phase 2) ----
builder.Services.AddScoped<IModelRegistryService, ModelRegistryService>();
builder.Services.AddScoped<ModelManagerAppService>();

// ---- Repository Analysis (Phase 3) ----
builder.Services.AddScoped<IRepositoryFileScanner, RepositoryFileScanner>();
builder.Services.AddScoped<ICodeSymbolExtractor, RoslynCSharpSymbolExtractor>();
builder.Services.AddScoped<IRepositoryIndexingService, RepositoryIndexingService>();

// ---- Background job queue (Section 40) ----
builder.Services.AddSingleton<ChannelBackgroundTaskQueue>();
builder.Services.AddSingleton<IBackgroundTaskQueue>(sp => sp.GetRequiredService<ChannelBackgroundTaskQueue>());
builder.Services.AddHostedService<QueuedHostedService>();

// ---- Tool Execution Engine (Phase 4, Sections 10/11) ----
// Register every concrete tool as ITool; ToolExecutionService discovers them via
// IEnumerable<ITool> injection, so adding a new tool never requires touching this
// service or the console UI — only this registration list.
builder.Services.AddScoped<ITool, FileReadTool>();
builder.Services.AddScoped<ITool, DirectoryListTool>();
builder.Services.AddScoped<ITool, FileWriteTool>();
builder.Services.AddScoped<ITool, FileEditTool>();
builder.Services.AddScoped<ITool, TerminalTool>();
builder.Services.AddScoped<ITool, GitTool>();
builder.Services.AddScoped<ITool, BuildTool>();
builder.Services.AddScoped<ITool, TestTool>();
builder.Services.AddScoped<CommandPermissionService>();
builder.Services.AddScoped<ToolExecutionService>();
builder.Services.AddScoped<ToolDefinitionSeeder>();

// ---- Agent Engine (Phase 5, Sections 8/9/47) ----
builder.Services.AddSingleton<AgentRunRegistry>();
builder.Services.AddScoped<AgentPlanningService>();
builder.Services.AddScoped<AgentOrchestratorService>();

// ---- Verification Engine + Self-Critic Reviewer (Phase 6, Sections 15/16/21) ----
builder.Services.AddScoped<ISecurityPatternScanner, RegexSecurityPatternScanner>();
builder.Services.AddScoped<VerificationPipelineService>();
builder.Services.AddScoped<ReviewerService>();

// ---- Memory (Phase 7, Section 14) ----
builder.Services.AddScoped<MemoryRetrievalService>();
builder.Services.AddScoped<MemoryWriteService>();

// ---- MVC ----
builder.Services.AddControllersWithViews(options =>
{
    // Every controller requires an authenticated user by default (spec Section 38);
    // [AllowAnonymous] on AccountController is the only opt-out.
    var policy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.Authorization.AuthorizeFilter(policy));
});

// ---- Authentication: cookie for the MVC UI, API key for /api/* ----
builder.Services.AddScoped<ApiKeyService>();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
    })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.SchemeName, _ => { });
builder.Services.AddAuthorization();

// ---- SignalR (Phase 8, Section 18/20) ----
builder.Services.AddSignalR();
builder.Services.AddSingleton<IAgentEventBroadcaster, SignalRAgentEventBroadcaster>();
builder.Services.AddHostedService<HardwareTelemetryBroadcastService>();

// ---- OpenAPI / Swagger (Phase 10, Section 18) ----
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Local Agent Platform API", Version = "v1" });
    c.AddSecurityDefinition(ApiKeyAuthenticationOptions.SchemeName, new()
    {
        Name = ApiKeyAuthenticationOptions.HeaderName,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "API key created from the /ApiKeys page. Example: lap_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
    });
    c.AddSecurityRequirement(new()
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = ApiKeyAuthenticationOptions.SchemeName
                }
            },
            Array.Empty<string>()
        }
    });
});

// ---- API rate limiting (Phase 11, Section 38) ----
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("api", opt =>
    {
        opt.PermitLimit = 60;
        opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ---- Health checks ----
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgresql")
    .AddCheck<OllamaHealthCheck>("ollama");

// ---- OpenTelemetry (tracing) ----
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

var app = builder.Build();

// Apply pending EF Core migrations automatically at startup. Real, idempotent
// (Migrate() is a no-op if the schema is current) — this is what lets
// docker-compose bring up a working instance without a manual `dotnet ef
// database update` step; local dev can still run migrations manually too.
using (var migrationScope = app.Services.CreateScope())
{
    var dbContext = migrationScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    await dbContext.Database.MigrateAsync();
}

// Seed ToolDefinition rows from the real registered ITool instances (Section 21) —
// runs once at startup so the DB reflects whatever tools are actually wired up.
using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<ToolDefinitionSeeder>();
    await seeder.SeedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Local Agent Platform API v1"));

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");
app.MapControllers(); // attribute-routed API controllers under /api/*

app.MapHub<AgentTelemetryHub>("/hubs/agent-telemetry");

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false // liveness: process is up, no dependency checks
});
app.MapHealthChecks("/health/ready"); // readiness: runs all registered checks (DB, Ollama)

app.Run();

/// <summary>Real health check that calls the configured IModelProvider's health endpoint.</summary>
public sealed class OllamaHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    private readonly IModelProvider _modelProvider;
    public OllamaHealthCheck(IModelProvider modelProvider) => _modelProvider = modelProvider;

    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var health = await _modelProvider.CheckHealthAsync(cancellationToken);
        return health.IsHealthy
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("Ollama reachable")
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(health.Detail ?? "Ollama unreachable");
    }
}

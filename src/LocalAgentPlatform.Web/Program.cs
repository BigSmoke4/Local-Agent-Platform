using OpenTelemetry.Trace;
using System.Net;
using System.Threading.RateLimiting;
using LocalAgentPlatform.Modules.Models.Application.Services;
using LocalAgentPlatform.Modules.Models.Infrastructure.Ollama;
using LocalAgentPlatform.Modules.Models.Infrastructure.Telemetry;
using LocalAgentPlatform.Modules.Agent.Application.Services;
using LocalAgentPlatform.Modules.Memory.Application.Services;
using LocalAgentPlatform.Modules.IdeIntegration.Domain;
using LocalAgentPlatform.Modules.RepositoryAnalysis.Application.Services;
using LocalAgentPlatform.Modules.RepositoryAnalysis.Infrastructure;
using LocalAgentPlatform.Modules.Tools.Application.Services;
using LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;
using LocalAgentPlatform.Modules.Verification.Application.Services;
using LocalAgentPlatform.Modules.Verification.Infrastructure.Security;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Kernel.BackgroundWork;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Shared.Kernel.Models;
using LocalAgentPlatform.Shared.Kernel.Security;
using LocalAgentPlatform.Shared.Kernel.Telemetry;
using LocalAgentPlatform.Shared.Kernel.Tools;
using LocalAgentPlatform.Web.BackgroundServices;
using LocalAgentPlatform.Web.Hubs;
using LocalAgentPlatform.Web.Infrastructure;
using LocalAgentPlatform.Web.Ide;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
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
var isDevelopment = builder.Environment.IsDevelopment();
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection(OllamaOptions.SectionName));
var allowedWorkspaceRoots = builder.Configuration.GetSection("Repositories:AllowedRoots").Get<string[]>() ?? Array.Empty<string>();
var configurationErrors = ProductionConfigurationPolicy.Validate(
    isDevelopment,
    builder.Configuration["AllowedHosts"],
    builder.Configuration["DataProtection:KeyRingPath"],
    allowedWorkspaceRoots);
if (configurationErrors.Count != 0)
    throw new InvalidOperationException($"Unsafe production configuration: {string.Join(' ', configurationErrors)}");
var workspaceRootPolicy = new WorkspaceRootPolicy(allowedWorkspaceRoots);
builder.Services.AddSingleton<IWorkspaceRootPolicy>(workspaceRootPolicy);

// ---- Data layer (PostgreSQL via EF Core) ----
var connectionString = builder.Configuration.GetConnectionString("PlatformDb");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Missing ConnectionStrings:PlatformDb. Supply it through a deployment secret or environment variable; no database credential is bundled in appsettings.json.");

builder.Services.AddDbContext<PlatformDbContext>(opts =>
    opts.UseNpgsql(connectionString, npg => npg.EnableRetryOnFailure()));

// ---- Model provider abstraction (IModelProvider -> Ollama adapter) ----
// This is the only place a concrete runtime is chosen. Swapping runtimes means
// registering a different IModelProvider implementation here — nothing else changes.
builder.Services.AddHttpClient<IModelProvider, OllamaModelProvider>();
builder.Services.AddHttpClient<IEmbeddingProvider, OllamaEmbeddingProvider>();

// ---- Hardware telemetry ----
builder.Services.AddSingleton<IHardwareTelemetryProvider, ProcHardwareTelemetryProvider>();

// ---- Model registry (Phase 2) ----
builder.Services.AddScoped<IModelRegistryService, ModelRegistryService>();
builder.Services.AddScoped<ModelManagerAppService>();

// ---- Repository Analysis (Phase 3) ----
builder.Services.AddScoped<IRepositoryFileScanner, RepositoryFileScanner>();
builder.Services.AddScoped<ICodeSymbolExtractor, RoslynCSharpSymbolExtractor>();
builder.Services.AddScoped<IRepositoryIndexingService, RepositoryIndexingService>();
builder.Services.AddScoped<IRepositoryContextEngine, RepositoryContextEngine>();

// ---- Background job queue (Section 40) ----
builder.Services.AddSingleton<ChannelBackgroundTaskQueue>();
builder.Services.AddSingleton<IBackgroundTaskQueue>(sp => sp.GetRequiredService<ChannelBackgroundTaskQueue>());
builder.Services.AddHostedService<QueuedHostedService>();
builder.Services.AddHostedService<PendingAgentSessionRecoveryService>();

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

// ---- IDE integration ----
builder.Services.AddSingleton<IIdeIntegrationProvider, VsCodeIdeIntegrationProvider>();

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
builder.Services.AddSingleton<TotpService>();
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("LocalAgentPlatform");
var dataProtectionKeyPath = builder.Configuration["DataProtection:KeyRingPath"];
if (!isDevelopment)
{
    var writeProbe = Path.Combine(dataProtectionKeyPath!, $".lap-write-probe-{Guid.NewGuid():N}");
    try
    {
        using (new FileStream(writeProbe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        File.Delete(writeProbe);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
    {
        try { if (File.Exists(writeProbe)) File.Delete(writeProbe); } catch { /* Keep the config failure as primary. */ }
        throw new InvalidOperationException("Production DataProtection:KeyRingPath must be writable by the web process.", ex);
    }
}
if (!string.IsNullOrWhiteSpace(dataProtectionKeyPath))
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath));
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = isDevelopment ? "LocalAgentPlatform.Auth" : "__Host-LocalAgentPlatform.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = isDevelopment
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
    })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
});

// Trust forwarded client/protocol headers only from explicitly configured reverse
// proxies. This keeps IP rate limits and HTTPS cookie behavior meaningful in production
// without accepting spoofed X-Forwarded-* headers from arbitrary clients.
var forwardedHeadersEnabled = builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled");
var knownProxyAddresses = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>();
var parsedKnownProxies = new List<IPAddress>();
if (!isDevelopment && !forwardedHeadersEnabled)
    throw new InvalidOperationException("Production requires TLS termination at a trusted reverse proxy with ForwardedHeaders:Enabled=true.");
foreach (var proxy in knownProxyAddresses)
{
    if (!IPAddress.TryParse(proxy, out var address))
        throw new InvalidOperationException($"ForwardedHeaders:KnownProxies contains an invalid IP address: '{proxy}'.");
    parsedKnownProxies.Add(address);
}
if (forwardedHeadersEnabled && parsedKnownProxies.Count == 0)
    throw new InvalidOperationException("ForwardedHeaders:Enabled requires at least one exact ForwardedHeaders:KnownProxies IP.");
if (forwardedHeadersEnabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();
        foreach (var proxy in parsedKnownProxies) options.KnownProxies.Add(proxy);
    });
}

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

// ---- API and credential-endpoint rate limiting ----
// Partition by remote IP so one client cannot consume a process-wide bucket and starve
// unrelated local users. Authentication endpoints have a deliberately tighter limit.
builder.Services.AddRateLimiter(options =>
{
    static string ClientPartition(HttpContext context) =>
        context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown-client";

    options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartition(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartition(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ---- Health checks ----
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgresql")
    .AddCheck<OllamaHealthCheck>("ollama");

// ---- OpenTelemetry (tracing) ----
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();
        // Console tracing is intentionally development-only; production deployments
        // should configure an approved exporter and retention policy explicitly.
        if (isDevelopment) tracing.AddConsoleExporter();
    });

var app = builder.Build();

// Serialize migrations and startup seeding across replicas. A PostgreSQL session-level
// advisory lock prevents two new web instances from racing the same DDL/unique inserts.
using (var startupScope = app.Services.CreateScope())
{
    var dbContext = startupScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    var seeder = startupScope.ServiceProvider.GetRequiredService<ToolDefinitionSeeder>();
    await PostgresStartupLock.ExecuteAsync(dbContext, async startupCt =>
    {
        var migrations = dbContext.Database.GetMigrations();
        if (migrations.Any())
        {
            await dbContext.Database.MigrateAsync(startupCt);
        }
        else if (app.Environment.IsDevelopment())
        {
            await dbContext.Database.EnsureCreatedAsync(startupCt);
        }
        else
        {
            throw new InvalidOperationException(
                "No EF Core migrations were found. Refusing to initialize a non-development database with EnsureCreatedAsync.");
        }

        await seeder.SeedAsync(startupCt);
    }, app.Lifetime.ApplicationStopping);
}

if (forwardedHeadersEnabled) app.UseForwardedHeaders();

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Local Agent Platform API v1"));
}

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

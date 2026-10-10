using LocalAgentPlatform.Modules.Agent.Application.Services;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Kernel.BackgroundWork;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.BackgroundServices;

/// <summary>
/// Re-enqueues durable sessions that were saved as Created but never claimed before a
/// previous process stopped. The orchestrator's conditional database claim makes this
/// safe when more than one application instance performs the same recovery scan.
/// Active sessions are deliberately not replayed: tool side effects are not generally
/// exactly-once and require a separate lease/recovery protocol.
/// </summary>
public sealed class PendingAgentSessionRecoveryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBackgroundTaskQueue _queue;
    private readonly ILogger<PendingAgentSessionRecoveryService> _logger;

    public PendingAgentSessionRecoveryService(
        IServiceScopeFactory scopeFactory,
        IBackgroundTaskQueue queue,
        ILogger<PendingAgentSessionRecoveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Guid[] pendingSessionIds;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                pendingSessionIds = await db.AgentSessions
                    .Where(session => session.State == "Created")
                    .OrderBy(session => session.CreatedAtUtc)
                    .Select(session => session.Id)
                    .ToArrayAsync(stoppingToken);
            }

            foreach (var sessionId in pendingSessionIds)
            {
                stoppingToken.ThrowIfCancellationRequested();
                _queue.QueueBackgroundWorkItem(async workToken =>
                {
                    using var workScope = _scopeFactory.CreateScope();
                    var orchestrator = workScope.ServiceProvider.GetRequiredService<AgentOrchestratorService>();
                    await orchestrator.RunAsync(sessionId, workToken);
                });
            }

            if (pendingSessionIds.Length > 0)
                _logger.LogInformation("Re-enqueued {SessionCount} unclaimed agent sessions from the database.", pendingSessionIds.Length);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception ex)
        {
            // Startup recovery is best-effort; a later restart can retry. Do not prevent
            // the web host from starting because a recoverable queued session exists.
            _logger.LogError(ex, "Could not recover unclaimed agent sessions at startup.");
        }
    }
}

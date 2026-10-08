using LocalAgentPlatform.Modules.Agent.Application.Services;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.BackgroundWork;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers;

public class AgentController : Controller
{
    private readonly PlatformDbContext _db;
    private readonly IBackgroundTaskQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentRunRegistry _registry;
    private readonly IWorkspaceRootPolicy _workspacePolicy;
    private readonly AgentOrchestratorService _orchestrator;

    public AgentController(
        PlatformDbContext db,
        IBackgroundTaskQueue queue,
        IServiceScopeFactory scopeFactory,
        AgentRunRegistry registry,
        IWorkspaceRootPolicy workspacePolicy,
        AgentOrchestratorService orchestrator)
    {
        _db = db;
        _queue = queue;
        _scopeFactory = scopeFactory;
        _registry = registry;
        _workspacePolicy = workspacePolicy;
        _orchestrator = orchestrator;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var userId = User.RequireUserId();
        var repositories = await _db.Repositories.Where(r => r.Project!.OwnerUserId == userId)
            .OrderBy(r => r.LocalPath).ToListAsync(ct);
        var visibleRepositories = repositories.Where(r => _workspacePolicy.IsAllowed(r.LocalPath)).ToList();
        var visibleRepositoryIds = visibleRepositories.Select(r => r.Id).ToArray();
        var vm = new AgentIndexViewModel
        {
            Sessions = await _db.AgentSessions
                .Where(s => s.OwnerUserId == userId && visibleRepositoryIds.Contains(s.RepositoryId))
                .OrderByDescending(s => s.CreatedAtUtc).Take(20).ToListAsync(ct),
            Repositories = visibleRepositories,
            RegisteredModels = await _db.RegisteredModels.OrderByDescending(m => m.IsDefault).ThenBy(m => m.Name).ToListAsync(ct)
        };
        return View(vm);
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        var session = await _db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id && s.OwnerUserId == userId, ct);
        if (session is null || !await IsSessionRepositoryAllowedAsync(session.RepositoryId, ct)) return NotFound();

        var tasks = await _db.AgentTaskNodes
            .Where(t => t.AgentSessionId == id)
            .OrderBy(t => t.OrderIndex)
            .ToListAsync(ct);

        var tokenUsage = await _db.TokenUsageRecords
            .Where(t => t.AgentSessionId == id)
            .ToListAsync(ct);

        var verificationRuns = await _db.VerificationRuns
            .Where(v => v.AgentSessionId == id)
            .OrderBy(v => v.RepairAttemptNumber)
            .ToListAsync(ct);

        return View(new AgentDetailsViewModel
        {
            Session = session,
            Tasks = tasks,
            TokenUsage = tokenUsage,
            VerificationRuns = verificationRuns,
            IsRunning = _registry.IsRunning(id)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(string userRequest, Guid repositoryId, string modelId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userRequest) || userRequest.Length > 4_000)
        {
            TempData["AgentError"] = "A request description is required and must be 4,000 characters or fewer.";
            return RedirectToAction(nameof(Index));
        }

        if (string.IsNullOrWhiteSpace(modelId) || !await _db.RegisteredModels.AnyAsync(m => m.ModelId == modelId, ct))
        {
            TempData["AgentError"] = "Choose a model that is registered with the local runtime.";
            return RedirectToAction(nameof(Index));
        }

        var userId = User.RequireUserId();
        var repo = await _db.Repositories.Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == repositoryId && r.Project!.OwnerUserId == userId, ct);
        if (repo is null || !_workspacePolicy.IsAllowed(repo.LocalPath))
        {
            TempData["AgentError"] = "Repository not found or outside the configured workspace roots.";
            return RedirectToAction(nameof(Index));
        }

        var session = new AgentSession
        {
            OwnerUserId = userId,
            ProjectId = repo.ProjectId,
            RepositoryId = repositoryId,
            UserRequest = userRequest,
            ModelIdUsed = modelId,
            State = "Created"
        };
        _db.AgentSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        _queue.QueueBackgroundWorkItem(async workCt =>
        {
            using var scope = _scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestratorService>();
            await orchestrator.RunAsync(session.Id, workCt);
        });

        return RedirectToAction(nameof(Details), new { id = session.Id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid sessionId, Guid taskId, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        if (!await IsOwnedSessionRepositoryAllowedAsync(sessionId, userId, ct)) return NotFound();
        var canApprove = await _db.AgentTaskNodes.AnyAsync(t =>
            t.Id == taskId && t.AgentSessionId == sessionId && t.Status == "AwaitingApproval" &&
            _db.AgentSessions.Any(s => s.Id == sessionId && s.OwnerUserId == userId && s.State == "AwaitingApproval"), ct);
        if (!canApprove) return NotFound();
        _queue.QueueBackgroundWorkItem(async workCt =>
        {
            using var scope = _scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestratorService>();
            await orchestrator.ApproveAndResumeAsync(sessionId, taskId, workCt);
        });
        return RedirectToAction(nameof(Details), new { id = sessionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(Guid sessionId, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        if (!await IsOwnedSessionRepositoryAllowedAsync(sessionId, userId, ct)) return NotFound();
        var cancellationRequested = _registry.RequestCancellation(sessionId);
        var pendingCancelled = await _orchestrator.CancelPendingAsync(sessionId, userId);
        var cancelled = cancellationRequested || pendingCancelled;
        TempData["AgentError"] = cancelled
            ? null
            : "Session is not currently running or waiting for approval; it may have already finished or the server restarted.";
        return RedirectToAction(nameof(Details), new { id = sessionId });
    }

    private async Task<bool> IsSessionRepositoryAllowedAsync(Guid repositoryId, CancellationToken ct)
    {
        var path = await _db.Repositories.Where(r => r.Id == repositoryId)
            .Select(r => r.LocalPath).FirstOrDefaultAsync(ct);
        return path is not null && _workspacePolicy.IsAllowed(path);
    }

    private async Task<bool> IsOwnedSessionRepositoryAllowedAsync(Guid sessionId, Guid ownerUserId, CancellationToken ct)
    {
        var path = await _db.AgentSessions
            .Where(s => s.Id == sessionId && s.OwnerUserId == ownerUserId)
            .Join(_db.Repositories, s => s.RepositoryId, r => r.Id, (_, r) => r.LocalPath)
            .FirstOrDefaultAsync(ct);
        return path is not null && _workspacePolicy.IsAllowed(path);
    }
}

public class AgentIndexViewModel
{
    public IReadOnlyList<AgentSession> Sessions { get; set; } = Array.Empty<AgentSession>();
    public IReadOnlyList<Repository> Repositories { get; set; } = Array.Empty<Repository>();
    public IReadOnlyList<RegisteredModel> RegisteredModels { get; set; } = Array.Empty<RegisteredModel>();
}

public class AgentDetailsViewModel
{
    public AgentSession Session { get; set; } = default!;
    public IReadOnlyList<AgentTaskNode> Tasks { get; set; } = Array.Empty<AgentTaskNode>();
    public IReadOnlyList<TokenUsageRecord> TokenUsage { get; set; } = Array.Empty<TokenUsageRecord>();
    public IReadOnlyList<VerificationRun> VerificationRuns { get; set; } = Array.Empty<VerificationRun>();
    public bool IsRunning { get; set; }
}

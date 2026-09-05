using LocalAgentPlatform.Modules.Agent.Application.Services;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.BackgroundWork;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers;

public class AgentController : Controller
{
    private readonly PlatformDbContext _db;
    private readonly IBackgroundTaskQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentRunRegistry _registry;

    public AgentController(PlatformDbContext db, IBackgroundTaskQueue queue, IServiceScopeFactory scopeFactory, AgentRunRegistry registry)
    {
        _db = db;
        _queue = queue;
        _scopeFactory = scopeFactory;
        _registry = registry;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var vm = new AgentIndexViewModel
        {
            Sessions = await _db.AgentSessions.OrderByDescending(s => s.CreatedAtUtc).Take(20).ToListAsync(ct),
            Repositories = await _db.Repositories.OrderBy(r => r.LocalPath).ToListAsync(ct),
            RegisteredModels = await _db.RegisteredModels.OrderByDescending(m => m.IsDefault).ThenBy(m => m.Name).ToListAsync(ct)
        };
        return View(vm);
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var session = await _db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return NotFound();

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
        if (string.IsNullOrWhiteSpace(userRequest))
        {
            TempData["AgentError"] = "A request description is required.";
            return RedirectToAction(nameof(Index));
        }

        var repo = await _db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repo is null)
        {
            TempData["AgentError"] = "Repository not found.";
            return RedirectToAction(nameof(Index));
        }

        var session = new AgentSession
        {
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
    public IActionResult Approve(Guid sessionId, Guid taskId)
    {
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
    public IActionResult Cancel(Guid sessionId)
    {
        var cancelled = _registry.RequestCancellation(sessionId);
        TempData["AgentError"] = cancelled
            ? null
            : "Session is not currently running (it may have already finished, or the server restarted since it started).";
        return RedirectToAction(nameof(Details), new { id = sessionId });
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

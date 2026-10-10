using LocalAgentPlatform.Modules.Agent.Application.Services;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.BackgroundWork;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Web.Models.Api;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers.Api;

/// <summary>
/// JSON API surface over the same AgentOrchestratorService the MVC Agent controller
/// uses — no separate/weaker code path for API-driven sessions (spec Section 18/19).
/// </summary>
[ApiController]
[Route("api/agent")]
[EnableRateLimiting("api")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]
public sealed class AgentApiController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IBackgroundTaskQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentRunRegistry _registry;
    private readonly IWorkspaceRootPolicy _workspacePolicy;
    private readonly AgentOrchestratorService _orchestrator;

    public AgentApiController(
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

    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyList<AgentSessionDto>>> ListSessions(CancellationToken ct)
    {
        var userId = User.RequireUserId();
        var repositories = await _db.Repositories
            .Where(r => r.Project!.OwnerUserId == userId)
            .Select(r => new { r.Id, r.LocalPath })
            .ToListAsync(ct);
        var visibleRepositoryIds = repositories
            .Where(r => _workspacePolicy.IsAllowed(r.LocalPath))
            .Select(r => r.Id)
            .ToArray();
        var sessions = await _db.AgentSessions
            .Where(s => s.OwnerUserId == userId && visibleRepositoryIds.Contains(s.RepositoryId))
            .OrderByDescending(s => s.CreatedAtUtc).Take(50).ToListAsync(ct);
        return Ok(sessions.Select(ToDto));
    }

    [HttpGet("sessions/{id:guid}")]
    public async Task<ActionResult<AgentSessionDto>> GetSession(Guid id, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        var s = await _db.AgentSessions.FirstOrDefaultAsync(x => x.Id == id && x.OwnerUserId == userId, ct);
        return s is null || !await IsOwnedSessionRepositoryAllowedAsync(id, userId, ct)
            ? NotFound()
            : Ok(ToDto(s));
    }

    [HttpGet("sessions/{id:guid}/tasks")]
    public async Task<ActionResult<IReadOnlyList<AgentTaskDto>>> GetTasks(Guid id, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        if (!await IsOwnedSessionRepositoryAllowedAsync(id, userId, ct)) return NotFound();
        var tasks = await _db.AgentTaskNodes
            .Where(t => t.AgentSessionId == id)
            .OrderBy(t => t.OrderIndex)
            .Select(t => new AgentTaskDto(t.Id, t.OrderIndex, t.Type, t.Description, t.ToolName, t.ArgumentsJson, t.Status, t.Output, t.Error, t.RetryCount))
            .ToListAsync(ct);
        return Ok(tasks);
    }

    [HttpPost("sessions")]
    public async Task<ActionResult<AgentSessionDto>> StartSession(StartAgentSessionRequest request, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        var repo = await _db.Repositories.Include(r => r.Project).FirstOrDefaultAsync(r => r.Id == request.RepositoryId && r.Project!.OwnerUserId == userId, ct);
        if (repo is null || !_workspacePolicy.IsAllowed(repo.LocalPath))
            return BadRequest(new { error = "Repository not found or outside the configured workspace roots." });
        if (string.IsNullOrWhiteSpace(request.UserRequest) || request.UserRequest.Length > 4_000)
            return BadRequest(new { error = "userRequest is required and must be 4,000 characters or fewer." });
        if (string.IsNullOrWhiteSpace(request.ModelId) ||
            !await _db.RegisteredModels.AnyAsync(m => m.ModelId == request.ModelId, ct))
            return BadRequest(new { error = "Model is not registered with the local runtime." });

        var session = new AgentSession
        {
            OwnerUserId = userId,
            ProjectId = repo.ProjectId,
            RepositoryId = request.RepositoryId,
            UserRequest = request.UserRequest,
            ModelIdUsed = request.ModelId,
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

        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, ToDto(session));
    }

    [HttpPost("sessions/{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, ApproveTaskRequest request, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        if (!await IsOwnedSessionRepositoryAllowedAsync(id, userId, ct)) return NotFound();
        var canApprove = await _db.AgentTaskNodes.AnyAsync(t =>
            t.Id == request.TaskId && t.AgentSessionId == id && t.Status == "AwaitingApproval" &&
            _db.AgentSessions.Any(s => s.Id == id && s.OwnerUserId == userId && s.State == "AwaitingApproval"), ct);
        if (!canApprove) return NotFound();
        _queue.QueueBackgroundWorkItem(async workCt =>
        {
            using var scope = _scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestratorService>();
            await orchestrator.ApproveAndResumeAsync(id, request.TaskId, workCt);
        });
        return Accepted();
    }

    [HttpPost("sessions/{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var userId = User.RequireUserId();
        if (!await IsOwnedSessionRepositoryAllowedAsync(id, userId, ct)) return NotFound();
        var cancellationRequested = _registry.RequestCancellation(id);
        var pendingCancelled = await _orchestrator.CancelPendingAsync(id, userId);
        var cancelled = cancellationRequested || pendingCancelled;
        return cancelled
            ? Accepted()
            : NotFound(new { error = "Session is not currently running or waiting for approval." });
    }

    private async Task<bool> IsOwnedSessionRepositoryAllowedAsync(Guid sessionId, Guid ownerUserId, CancellationToken ct)
    {
        var path = await _db.AgentSessions
            .Where(s => s.Id == sessionId && s.OwnerUserId == ownerUserId)
            .Join(_db.Repositories, s => s.RepositoryId, r => r.Id, (_, r) => r.LocalPath)
            .FirstOrDefaultAsync(ct);
        return path is not null && _workspacePolicy.IsAllowed(path);
    }

    private static AgentSessionDto ToDto(AgentSession s) => new(
        s.Id, s.RepositoryId, s.UserRequest, s.State, s.ModelIdUsed, s.CreatedAtUtc, s.CompletedAtUtc,
        s.IterationCount, s.MaxIterations, s.RepairAttemptCount, s.MaxRepairAttempts,
        s.FailureReason, s.FinalSummary);
}

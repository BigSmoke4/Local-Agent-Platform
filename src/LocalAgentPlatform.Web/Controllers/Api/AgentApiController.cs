using LocalAgentPlatform.Modules.Agent.Application.Services;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.BackgroundWork;
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

    public AgentApiController(PlatformDbContext db, IBackgroundTaskQueue queue, IServiceScopeFactory scopeFactory, AgentRunRegistry registry)
    {
        _db = db;
        _queue = queue;
        _scopeFactory = scopeFactory;
        _registry = registry;
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyList<AgentSessionDto>>> ListSessions(CancellationToken ct)
    {
        var sessions = await _db.AgentSessions.OrderByDescending(s => s.CreatedAtUtc).Take(50).ToListAsync(ct);
        return Ok(sessions.Select(ToDto));
    }

    [HttpGet("sessions/{id:guid}")]
    public async Task<ActionResult<AgentSessionDto>> GetSession(Guid id, CancellationToken ct)
    {
        var s = await _db.AgentSessions.FirstOrDefaultAsync(x => x.Id == id, ct);
        return s is null ? NotFound() : Ok(ToDto(s));
    }

    [HttpGet("sessions/{id:guid}/tasks")]
    public async Task<ActionResult<IReadOnlyList<AgentTaskDto>>> GetTasks(Guid id, CancellationToken ct)
    {
        var tasks = await _db.AgentTaskNodes
            .Where(t => t.AgentSessionId == id)
            .OrderBy(t => t.OrderIndex)
            .Select(t => new AgentTaskDto(t.Id, t.OrderIndex, t.Type, t.Description, t.ToolName, t.Status, t.Output, t.Error, t.RetryCount))
            .ToListAsync(ct);
        return Ok(tasks);
    }

    [HttpPost("sessions")]
    public async Task<ActionResult<AgentSessionDto>> StartSession(StartAgentSessionRequest request, CancellationToken ct)
    {
        var repo = await _db.Repositories.FirstOrDefaultAsync(r => r.Id == request.RepositoryId, ct);
        if (repo is null) return BadRequest(new { error = "Repository not found." });
        if (string.IsNullOrWhiteSpace(request.UserRequest)) return BadRequest(new { error = "userRequest is required." });

        var session = new AgentSession
        {
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
    public IActionResult Approve(Guid id, ApproveTaskRequest request)
    {
        _queue.QueueBackgroundWorkItem(async workCt =>
        {
            using var scope = _scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AgentOrchestratorService>();
            await orchestrator.ApproveAndResumeAsync(id, request.TaskId, workCt);
        });
        return Accepted();
    }

    [HttpPost("sessions/{id:guid}/cancel")]
    public IActionResult Cancel(Guid id)
    {
        var cancelled = _registry.RequestCancellation(id);
        return cancelled ? Accepted() : NotFound(new { error = "Session is not currently running." });
    }

    private static AgentSessionDto ToDto(AgentSession s) => new(
        s.Id, s.UserRequest, s.State, s.ModelIdUsed, s.CreatedAtUtc, s.CompletedAtUtc,
        s.IterationCount, s.MaxIterations, s.RepairAttemptCount, s.MaxRepairAttempts,
        s.FailureReason, s.FinalSummary);
}

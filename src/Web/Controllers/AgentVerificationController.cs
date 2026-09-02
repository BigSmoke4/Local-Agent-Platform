using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;
using Platform.Web.Hubs;
using Platform.Web.Models;
using Platform.Web.Services;
using Platform.Web.Services.Tools;
using Platform.Web.Services.Verification;
using Platform.Web.Services.CodeIntelligence;

namespace Platform.Web.Controllers;

/// <summary>
/// Runs a task graph: Understanding -> Build -> Test -> Review, with retry
/// up to a hard iteration cap (§47 — never an unbounded loop). Every stage
/// is persisted as an AgentTaskNode and broadcast over SignalR as it happens.
/// This verifies whatever is currently in the configured workspace; it does
/// not itself modify code (no FileWriteTool exists yet), so "repair" here
/// means re-running verification, not agent-authored fixes.
/// </summary>
[Authorize]
[ApiController]
[Route("api/agent")]
public class AgentVerificationController : ControllerBase
{
    private const int MaxIterations = 3;

    private readonly PlatformDbContext _db;
    private readonly VerificationEngine _verification;
    private readonly ReviewerService _reviewer;
    private readonly IAgentEventBroadcaster _broadcaster;
    private readonly IModelProvider _modelProvider;
    private readonly SafeFileEditService _safeEdit;
    private readonly FileReadTool _fileRead;
    private readonly Services.Autonomy.AutonomyService _autonomy;
    private readonly Services.CodeIntelligence.SemanticRepairTargetResolver _repairTargetResolver;
    private readonly ILogger<AgentVerificationController> _logger;

    public AgentVerificationController(
        PlatformDbContext db,
        VerificationEngine verification,
        ReviewerService reviewer,
        IAgentEventBroadcaster broadcaster,
        IModelProvider modelProvider,
        SafeFileEditService safeEdit,
        FileReadTool fileRead,
        Services.Autonomy.AutonomyService autonomy,
        Services.CodeIntelligence.SemanticRepairTargetResolver repairTargetResolver,
        ILogger<AgentVerificationController> logger)
    {
        _db = db;
        _verification = verification;
        _reviewer = reviewer;
        _broadcaster = broadcaster;
        _modelProvider = modelProvider;
        _safeEdit = safeEdit;
        _fileRead = fileRead;
        _autonomy = autonomy;
        _repairTargetResolver = repairTargetResolver;
        _logger = logger;
    }

    public record RunVerifiedRequest(string UserRequest, string? ProjectPath, string? RepairTargetFile);

    [HttpPost("run-verified")]
    public async Task<IActionResult> RunVerified(RunVerifiedRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserRequest))
            return BadRequest("UserRequest is required.");

        var defaultModel = await _db.Models.FirstOrDefaultAsync(m => m.IsDefault, ct);
        if (defaultModel is null)
            return UnprocessableEntity("No default model registered. POST /api/models first.");

        var session = new AgentSession
        {
            UserRequest = request.UserRequest,
            ModelId = defaultModel.Id,
            State = AgentSessionState.Verifying
        };
        _db.AgentSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        await _broadcaster.AgentStateChangedAsync(session.Id, session.State.ToString(), ct);

        var iteration = 0;
        VerificationOutcome? lastOutcome = null;
        ReviewOutcome? lastReview = null;

        while (iteration < MaxIterations)
        {
            iteration++;

            var buildNode = await CreateTaskNodeAsync(session.Id, iteration, "Build", $"Verification attempt {iteration}: build", ct);
            var outcome = await _verification.RunAsync(request.ProjectPath, ct);
            lastOutcome = outcome;

            await CompleteTaskNodeAsync(buildNode, outcome.BuildSucceeded, outcome.Summary, ct);
            await _broadcaster.VerificationUpdatedAsync(session.Id, "Build", outcome.BuildSucceeded, outcome.Summary, ct);

            if (!outcome.BuildSucceeded)
            {
                session.State = AgentSessionState.Repairing;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.AgentStateChangedAsync(session.Id, session.State.ToString(), ct);

                if (iteration < MaxIterations)
                {
                    var userId = User.Identity?.Name ?? "anonymous";
                    var autonomyLevel = await _autonomy.GetLevelAsync(userId, ct);

                    if (!_autonomy.CanAutoEditFiles(autonomyLevel))
                    {
                        _logger.LogInformation(
                            "Skipping automated repair for attempt {Iteration}: autonomy level is {Level}, which requires manual approval before file edits.",
                            iteration, autonomyLevel);
                    }
                    else
                    {
                        // Multi-file, semantic-graph-aware repair targeting: expands
                        // beyond the compiler-reported file(s) using real reference
                        // tracing when a semantic workspace has been loaded (see
                        // POST /api/code-intelligence/semantic/load). Falls back to
                        // the plain compiler-diagnostic file list otherwise — never
                        // silently does nothing.
                        List<string> targetFiles;
                        if (request.RepairTargetFile is not null)
                        {
                            targetFiles = new List<string> { request.RepairTargetFile };
                        }
                        else
                        {
                            var resolved = await _repairTargetResolver.ResolveAsync(outcome.RawBuildOutput, ct);
                            targetFiles = resolved.Files;
                            _logger.LogInformation("Repair targeting: {Reasoning}", resolved.Reasoning);
                        }

                        if (targetFiles.Count == 0)
                        {
                            _logger.LogInformation(
                                "No RepairTargetFile supplied and no file paths could be parsed from build output; skipping repair for attempt {Iteration}.",
                                iteration);
                        }
                        else
                        {
                            var anyRepaired = false;
                            foreach (var targetFile in targetFiles)
                            {
                                var repaired = await TryRepairAsync(
                                    session.Id, iteration, defaultModel.RuntimeId, targetFile, outcome.RawBuildOutput, ct);
                                anyRepaired |= repaired;
                            }

                            if (!anyRepaired)
                                _logger.LogInformation("Repair attempt {Iteration} fixed none of the {Count} target file(s).", iteration, targetFiles.Count);
                        }
                    }
                }
                continue;
            }

            var testNode = await CreateTaskNodeAsync(session.Id, iteration, "Test", $"Verification attempt {iteration}: test", ct);
            await CompleteTaskNodeAsync(testNode, outcome.TestsSucceeded, outcome.Summary, ct);
            await _broadcaster.VerificationUpdatedAsync(session.Id, "Test", outcome.TestsSucceeded, outcome.Summary, ct);

            if (!outcome.TestsSucceeded)
            {
                session.State = AgentSessionState.Repairing;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.AgentStateChangedAsync(session.Id, session.State.ToString(), ct);
                continue;
            }

            var reviewNode = await CreateTaskNodeAsync(session.Id, iteration, "Review", "Independent reviewer stage", ct);
            var review = await _reviewer.ReviewAsync(defaultModel.RuntimeId, request.UserRequest, outcome.Summary, outcome.Summary, ct);
            lastReview = review;
            await CompleteTaskNodeAsync(reviewNode, review.Approved, review.Reasoning, ct);
            await _broadcaster.VerificationUpdatedAsync(session.Id, "Review", review.Approved, review.Reasoning, ct);

            if (review.Approved)
            {
                session.State = AgentSessionState.Completed;
                session.FinalResult = outcome.Summary;
                session.CompletedAtUtc = DateTime.UtcNow;
                break;
            }

            session.State = AgentSessionState.Repairing;
            await _db.SaveChangesAsync(ct);
            await _broadcaster.AgentStateChangedAsync(session.Id, session.State.ToString(), ct);
        }

        if (session.State != AgentSessionState.Completed)
        {
            session.State = AgentSessionState.Failed;
            session.ErrorMessage = $"Exhausted {MaxIterations} verification attempts. " +
                                    $"Last build={lastOutcome?.BuildSucceeded} tests={lastOutcome?.TestsSucceeded} " +
                                    $"review={lastReview?.Approved}. " +
                                    (request.RepairTargetFile is null
                                        ? "No RepairTargetFile was supplied, so no automated repair was attempted."
                                        : "Automated repair was attempted via the model but did not resolve the failure within the retry budget.");
            session.CompletedAtUtc = DateTime.UtcNow;
        }

        _db.AuditLogs.Add(new AuditLog { Action = "AgentVerificationLoopCompleted", Details = $"{session.Id}: {session.State}" });
        await _db.SaveChangesAsync(ct);
        await _broadcaster.AgentStateChangedAsync(session.Id, session.State.ToString(), ct);

        return Ok(session);
    }

    [HttpGet("sessions/{id:guid}/tasks")]
    public async Task<IActionResult> GetTaskGraph(Guid id, CancellationToken ct)
        => Ok(await _db.AgentTaskNodes
            .Where(t => t.AgentSessionId == id)
            .OrderBy(t => t.SequenceOrder)
            .ToListAsync(ct));

    /// <summary>
    /// Real dynamic planning per §8/§9: asks the model to break the request
    /// into steps and returns the actual parsed plan. Informational only —
    /// `run-verified` above still executes its fixed Build→Test→Review→Repair
    /// sequence rather than consuming this plan's steps; wiring an executor
    /// that walks an arbitrary model-generated step sequence (rather than a
    /// hardcoded one) is a larger separate change from generating the plan
    /// itself, and isn't conflated with it here.
    /// </summary>
    [HttpPost("plan")]
    public async Task<IActionResult> Plan([FromBody] RunVerifiedRequest request, [FromServices] Services.Planning.PlannerService planner, CancellationToken ct)
    {
        var defaultModel = await _db.Models.FirstOrDefaultAsync(m => m.IsDefault, ct);
        if (defaultModel is null)
            return UnprocessableEntity("No default model registered. POST /api/models first.");

        var plan = await planner.CreatePlanAsync(defaultModel.RuntimeId, request.UserRequest, ct);
        return plan.Succeeded ? Ok(plan) : UnprocessableEntity(plan);
    }

    /// <summary>
    /// Real dynamic-plan-driven execution: generates a plan via
    /// PlannerService, then actually runs it step-by-step via
    /// PlanExecutionService — closing the gap where a plan was generated but
    /// never consumed. This is a genuinely different code path from
    /// run-verified above (which still uses its own fixed sequence); this one
    /// executes whatever the model actually planned, stopping at the first
    /// real step failure rather than continuing blindly.
    /// </summary>
    [HttpPost("run-planned")]
    public async Task<IActionResult> RunPlanned(
        [FromBody] RunVerifiedRequest request,
        [FromServices] Services.Planning.PlannerService planner,
        [FromServices] Services.Planning.PlanExecutionService executor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserRequest))
            return BadRequest("UserRequest is required.");

        var defaultModel = await _db.Models.FirstOrDefaultAsync(m => m.IsDefault, ct);
        if (defaultModel is null)
            return UnprocessableEntity("No default model registered. POST /api/models first.");

        var plan = await planner.CreatePlanAsync(defaultModel.RuntimeId, request.UserRequest, ct);
        if (!plan.Succeeded)
            return UnprocessableEntity(new { message = "Planning failed.", plan.Error });

        var session = new AgentSession
        {
            UserRequest = request.UserRequest,
            ModelId = defaultModel.Id,
            State = AgentSessionState.Planning
        };
        _db.AgentSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        await _broadcaster.AgentStateChangedAsync(session.Id, session.State.ToString(), ct);

        session.State = AgentSessionState.Executing;
        await _db.SaveChangesAsync(ct);
        await _broadcaster.AgentStateChangedAsync(session.Id, session.State.ToString(), ct);

        var execution = await executor.ExecuteAsync(session.Id, defaultModel.RuntimeId, request.UserRequest, plan.Steps, ct);

        session.State = execution.Succeeded ? AgentSessionState.Completed : AgentSessionState.Failed;
        session.FinalResult = execution.Summary;
        if (!execution.Succeeded) session.ErrorMessage = "One or more planned steps failed. See task nodes for detail.";
        session.CompletedAtUtc = DateTime.UtcNow;

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "AgentVerificationController.RunPlanned",
            Details = $"{session.Id}: {session.State}, {execution.ExecutedNodes.Count} step(s) executed"
        });
        await _db.SaveChangesAsync(ct);
        await _broadcaster.AgentStateChangedAsync(session.Id, session.State.ToString(), ct);

        return Ok(new { session, plan = plan.Steps, execution.ExecutedNodes, execution.Summary });
    }

    private async Task<AgentTaskNode> CreateTaskNodeAsync(Guid sessionId, int sequence, string type, string description, CancellationToken ct)
    {
        var node = new AgentTaskNode
        {
            AgentSessionId = sessionId,
            SequenceOrder = sequence,
            Type = type,
            Description = description,
            Status = TaskNodeStatus.Running,
            StartedAtUtc = DateTime.UtcNow
        };
        _db.AgentTaskNodes.Add(node);
        await _db.SaveChangesAsync(ct);
        return node;
    }

    private async Task CompleteTaskNodeAsync(AgentTaskNode node, bool succeeded, string outputSummary, CancellationToken ct)
    {
        node.Status = succeeded ? TaskNodeStatus.Succeeded : TaskNodeStatus.Failed;
        node.OutputSummary = outputSummary;
        node.EndedAtUtc = DateTime.UtcNow;
        if (!succeeded) node.RetryCount++;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Real repair attempt: reads the target file, asks the model for a
    /// corrected version given the actual build output, and applies it
    /// through SafeFileEditService (hash-checked, snapshotted, diffed).
    /// This is intentionally scoped to a single caller-specified file —
    /// there is no diagnostic-to-file mapping (that needs the Roslyn work
    /// called out in README.md), so the agent cannot yet decide on its own
    /// which file caused a given compiler error.
    /// </summary>
    private async Task<bool> TryRepairAsync(
        Guid sessionId, int iteration, string modelRuntimeId, string targetFile, string buildOutput, CancellationToken ct)
    {
        var repairNode = await CreateTaskNodeAsync(sessionId, iteration, "Repair", $"Attempt to fix {targetFile}", ct);

        string currentContent;
        try
        {
            currentContent = await _fileRead.ReadAsync(targetFile, ct);
        }
        catch (Exception ex)
        {
            await CompleteTaskNodeAsync(repairNode, false, $"Could not read {targetFile}: {ex.Message}", ct);
            return false;
        }

        var prompt = $"""
            The following file failed to build. Return ONLY the complete corrected
            file content, with no explanation, no markdown fences, no commentary.

            Build output:
            {buildOutput}

            Current content of {targetFile}:
            {currentContent}
            """;

        try
        {
            var generation = await _modelProvider.GenerateAsync(new GenerationRequest(modelRuntimeId, prompt), ct);
            var newContent = StripMarkdownFences(generation.Text);

            if (string.IsNullOrWhiteSpace(newContent))
            {
                await CompleteTaskNodeAsync(repairNode, false, "Model returned empty content.", ct);
                return false;
            }

            var outcome = await _safeEdit.ApplyAsync(sessionId, targetFile, newContent, ct);
            await CompleteTaskNodeAsync(
                repairNode, true,
                $"Applied edit: +{outcome.Diff.LinesAdded}/-{outcome.Diff.LinesRemoved} lines. Snapshot {outcome.SnapshotId} recorded for rollback.",
                ct);
            await _broadcaster.VerificationUpdatedAsync(sessionId, "Repair", true, $"Edited {targetFile}", ct);
            return true;
        }
        catch (Exception ex)
        {
            await CompleteTaskNodeAsync(repairNode, false, $"Repair failed: {ex.Message}", ct);
            await _broadcaster.VerificationUpdatedAsync(sessionId, "Repair", false, ex.Message, ct);
            return false;
        }
    }

    private static string StripMarkdownFences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0) trimmed = trimmed[(firstNewline + 1)..];
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) trimmed = trimmed[..lastFence];
        }
        return trimmed.Trim();
    }
}

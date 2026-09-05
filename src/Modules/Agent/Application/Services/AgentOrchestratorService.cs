using System.Text;
using System.Text.Json;
using LocalAgentPlatform.Modules.Agent.Domain;
using LocalAgentPlatform.Modules.Memory.Application.Services;
using LocalAgentPlatform.Modules.Tools.Application.Services;
using LocalAgentPlatform.Modules.Verification.Application.Services;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.BackgroundWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LocalAgentPlatform.Modules.Agent.Application.Services;

/// <summary>
/// The real agent execution loop (spec Section 8). Explicit states are persisted on
/// AgentSession.State at every transition. This is intentionally a *linear* task chain
/// (see docs/STATUS.md) — general branching task graphs are a documented follow-up, not
/// a hidden simplification. Every tool call goes through the same
/// <see cref="ToolExecutionService"/> the Tools console uses, so an agent-run tool
/// invocation is indistinguishable in the audit trail from a human-run one — no
/// separate, weaker code path for "the agent did it".
/// </summary>
public sealed class AgentOrchestratorService
{
    private readonly PlatformDbContext _db;
    private readonly AgentPlanningService _planningService;
    private readonly ToolExecutionService _toolExecutionService;
    private readonly VerificationPipelineService _verificationPipeline;
    private readonly ReviewerService _reviewerService;
    private readonly MemoryRetrievalService _memoryRetrievalService;
    private readonly MemoryWriteService _memoryWriteService;
    private readonly IAgentEventBroadcaster _broadcaster;
    private readonly AgentRunRegistry _registry;
    private readonly ILogger<AgentOrchestratorService> _logger;

    public AgentOrchestratorService(
        PlatformDbContext db,
        AgentPlanningService planningService,
        ToolExecutionService toolExecutionService,
        VerificationPipelineService verificationPipeline,
        ReviewerService reviewerService,
        MemoryRetrievalService memoryRetrievalService,
        MemoryWriteService memoryWriteService,
        IAgentEventBroadcaster broadcaster,
        AgentRunRegistry registry,
        ILogger<AgentOrchestratorService> logger)
    {
        _db = db;
        _planningService = planningService;
        _toolExecutionService = toolExecutionService;
        _verificationPipeline = verificationPipeline;
        _reviewerService = reviewerService;
        _memoryRetrievalService = memoryRetrievalService;
        _memoryWriteService = memoryWriteService;
        _broadcaster = broadcaster;
        _registry = registry;
        _logger = logger;
    }

    public async Task RunAsync(Guid sessionId, CancellationToken externalCt = default)
    {
        var cts = _registry.Register(sessionId);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, cts.Token);
        var ct = linkedCts.Token;

        try
        {
            var session = await _db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct)
                ?? throw new InvalidOperationException($"AgentSession {sessionId} not found.");

            // ---- Understanding / Planning ----
            await SetStateAsync(session, "Understanding", ct);

            await SetStateAsync(session, "Planning", ct);
            var tools = _toolExecutionService.AllTools;

            // Real retrieval-based memory (Section 14): pull relevant prior context for
            // this repository instead of injecting everything ever stored.
            var relevantMemory = await _memoryRetrievalService.RetrieveRelevantAsync(session.RepositoryId, session.UserRequest, ct: ct);
            var memoryContext = MemoryRetrievalService.FormatForPrompt(relevantMemory);

            var planningOutcome = await _planningService.CreatePlanAsync(
                session.ModelIdUsed!, session.UserRequest, tools, ct, additionalContext: memoryContext);

            _db.TokenUsageRecords.Add(new TokenUsageRecord
            {
                AgentSessionId = session.Id,
                InputTokens = planningOutcome.ModelResult.InputTokens,
                OutputTokens = planningOutcome.ModelResult.OutputTokens,
                TokensPerSecond = planningOutcome.ModelResult.Duration.TotalSeconds > 0
                    ? planningOutcome.ModelResult.OutputTokens / planningOutcome.ModelResult.Duration.TotalSeconds
                    : 0,
                TimeToFirstToken = planningOutcome.ModelResult.TimeToFirstToken
            });
            await _db.SaveChangesAsync(ct);

            if (planningOutcome.Plan is null)
            {
                await FailAsync(session, $"Planning failed: {planningOutcome.ParseError}. Raw model output was recorded in PlanJson for inspection.", ct);
                session.PlanJson = planningOutcome.RawModelText;
                await _db.SaveChangesAsync(ct);
                return;
            }

            session.PlanJson = JsonSerializer.Serialize(planningOutcome.Plan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await _db.SaveChangesAsync(ct);

            var steps = planningOutcome.Plan.Steps;
            Guid? previousTaskId = null;
            for (var i = 0; i < steps.Count; i++)
            {
                var existingTask = await _db.AgentTaskNodes
                    .FirstOrDefaultAsync(t => t.AgentSessionId == session.Id && t.OrderIndex == i, ct);
                if (existingTask is not null && existingTask.Status == "Completed")
                {
                    previousTaskId = existingTask.Id;
                    continue; // already done in a prior run (e.g. resumed after approval)
                }

                var task = existingTask ?? new AgentTaskNode
                {
                    AgentSessionId = session.Id,
                    OrderIndex = i,
                    ParentId = previousTaskId,
                    Type = steps[i].Type,
                    Description = steps[i].Description,
                    ToolName = steps[i].ToolName,
                    ArgumentsJson = steps[i].Arguments is not null ? JsonSerializer.Serialize(steps[i].Arguments) : null
                };
                if (existingTask is null) _db.AgentTaskNodes.Add(task);

                var stopped = await RunTaskWithRetriesAsync(session, task, approved: false, ct);
                await _db.SaveChangesAsync(ct);

                if (stopped is not null)
                {
                    if (stopped == "AwaitingApproval")
                    {
                        await SetStateAsync(session, "AwaitingApproval", ct);
                    }
                    else
                    {
                        await FailAsync(session, stopped, ct);
                    }
                    return;
                }

                previousTaskId = task.Id;
            }

            await RunVerificationAsync(session, ct);
        }
        catch (OperationCanceledException)
        {
            var session = await _db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, CancellationToken.None);
            if (session is not null)
            {
                session.State = "Cancelled";
                session.CompletedAtUtc = DateTimeOffset.UtcNow;
                session.FinalSummary = "Cancelled by user request.";
                await _db.SaveChangesAsync(CancellationToken.None);
                await _broadcaster.SessionUpdatedAsync(session.Id, session.State, CancellationToken.None);
                await _memoryWriteService.RecordSessionOutcomeAsync(session, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent session {SessionId} failed with an unhandled exception.", sessionId);
            var session = await _db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, CancellationToken.None);
            if (session is not null)
                await FailAsync(session, $"Unhandled exception: {ex.Message}", CancellationToken.None);
        }
        finally
        {
            _registry.Unregister(sessionId);
        }
    }

    /// <summary>Resumes a session sitting in AwaitingApproval: approves exactly the
    /// pending task, then continues the loop from the next step.</summary>
    public Task ApproveAndResumeAsync(Guid sessionId, Guid taskId, CancellationToken ct = default) =>
        ResumeInternalAsync(sessionId, taskId, ct);

    private async Task ResumeInternalAsync(Guid sessionId, Guid taskId, CancellationToken externalCt)
    {
        var session = await _db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, externalCt)
            ?? throw new InvalidOperationException($"AgentSession {sessionId} not found.");
        var task = await _db.AgentTaskNodes.FirstOrDefaultAsync(t => t.Id == taskId, externalCt)
            ?? throw new InvalidOperationException($"AgentTaskNode {taskId} not found.");

        var cts = _registry.Register(sessionId);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, cts.Token);
        var ct = linkedCts.Token;

        try
        {
            await SetStateAsync(session, "Executing", ct);
            var stopped = await RunTaskWithRetriesAsync(session, task, approved: true, ct);
            await _db.SaveChangesAsync(ct);

            if (stopped is not null)
            {
                if (stopped == "AwaitingApproval") await SetStateAsync(session, "AwaitingApproval", ct);
                else await FailAsync(session, stopped, ct);
                return;
            }

            // Continue remaining steps by re-entering the main loop from PlanJson.
            var plan = JsonSerializer.Deserialize<AgentPlan>(session.PlanJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (plan is null) { await FailAsync(session, "Could not resume: stored plan JSON was invalid.", ct); return; }

            Guid? previousTaskId = task.Id;
            for (var i = task.OrderIndex + 1; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                var nextTask = new AgentTaskNode
                {
                    AgentSessionId = session.Id,
                    OrderIndex = i,
                    ParentId = previousTaskId,
                    Type = step.Type,
                    Description = step.Description,
                    ToolName = step.ToolName,
                    ArgumentsJson = step.Arguments is not null ? JsonSerializer.Serialize(step.Arguments) : null
                };
                _db.AgentTaskNodes.Add(nextTask);

                var stoppedHere = await RunTaskWithRetriesAsync(session, nextTask, approved: false, ct);
                await _db.SaveChangesAsync(ct);

                if (stoppedHere is not null)
                {
                    if (stoppedHere == "AwaitingApproval") await SetStateAsync(session, "AwaitingApproval", ct);
                    else await FailAsync(session, stoppedHere, ct);
                    return;
                }
                previousTaskId = nextTask.Id;
            }

            await RunVerificationAsync(session, ct);
        }
        finally
        {
            _registry.Unregister(sessionId);
        }
    }

    /// <summary>Runs one task node, retrying tool failures up to the session's MaxRetries.
    /// Returns null on success, "AwaitingApproval" if it needs a human, or a failure
    /// reason string if the task (or a budget) exhausted its allowance.</summary>
    private async Task<string?> RunTaskWithRetriesAsync(AgentSession session, AgentTaskNode task, bool approved, CancellationToken ct)
    {
        await SetStateAsync(session, "Executing", ct);
        task.Status = "Executing";
        task.StartedAtUtc ??= DateTimeOffset.UtcNow;

        while (true)
        {
            session.IterationCount++;
            var budget = AgentBudgetPolicy.Check(
                session.IterationCount, session.MaxIterations,
                task.RetryCount, session.MaxRetries,
                session.CreatedAtUtc, session.MaxDurationMinutes, DateTimeOffset.UtcNow);

            if (!budget.CanContinue)
            {
                task.Status = "Failed";
                task.Error = budget.StopReason;
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                return budget.StopReason;
            }

            if (task.Type == "Reasoning")
            {
                task.Status = "Completed";
                task.Output = task.Description;
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                return null;
            }

            if (string.IsNullOrWhiteSpace(task.ToolName))
            {
                task.Status = "Failed";
                task.Error = "Plan step marked as ToolCall but named no tool.";
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                return task.Error;
            }

            var arguments = string.IsNullOrEmpty(task.ArgumentsJson)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(task.ArgumentsJson)!;

            ToolInvocationOutcome outcome;
            try
            {
                outcome = await _toolExecutionService.InvokeAsync(task.ToolName, session.RepositoryId, arguments, approved, ct);
            }
            catch (InvalidOperationException ex)
            {
                task.Status = "Failed";
                task.Error = ex.Message;
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                return ex.Message;
            }

            if (outcome.Decision == "Denied")
            {
                task.Status = "Failed";
                task.Error = $"Denied by command policy: {outcome.DecisionReason}";
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                return task.Error;
            }

            if (outcome.Decision == "PendingApproval")
            {
                task.Status = "AwaitingApproval";
                task.Error = outcome.DecisionReason;
                return "AwaitingApproval";
            }

            // Allowed and executed.
            if (outcome.Result is { Success: true })
            {
                task.Status = "Completed";
                task.Output = outcome.Result.Output;
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                return null;
            }

            // Tool ran but failed — retry within budget.
            task.RetryCount++;
            task.Error = outcome.Result?.Error ?? "Tool returned failure with no error message.";
            approved = true; // once approved for one attempt, retries of the same already-cleared command don't need re-approval

            if (task.RetryCount > session.MaxRetries)
            {
                task.Status = "Failed";
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                return $"Task '{task.Description}' failed after {task.RetryCount} attempts: {task.Error}";
            }
            // loop and retry
        }
    }

    /// <summary>
    /// Real verification + self-critic + bounded repair loop (Sections 15/16/47).
    /// Runs the actual VerificationPipelineService (build/test/security); if it fails,
    /// or the advisory reviewer rejects, attempts a real repair: it re-plans with the
    /// concrete failure text as extra context, executes the repair steps, and
    /// re-verifies — up to session.MaxRepairAttempts times. If repairs run out, the
    /// session ends Failed with the real, last verification result attached. Success is
    /// only ever reported when the deterministic pipeline actually passed.
    /// </summary>
    private async Task RunVerificationAsync(AgentSession session, CancellationToken ct)
    {
        var tasks = await _db.AgentTaskNodes
            .Where(t => t.AgentSessionId == session.Id)
            .OrderBy(t => t.OrderIndex)
            .ToListAsync(ct);

        var touchedFiles = tasks.Any(t =>
            t.Status == "Completed" &&
            (t.ToolName == "FileWriteTool" || t.ToolName == "FileEditTool"));

        if (!touchedFiles)
        {
            // Nothing to verify — a pure read/reasoning plan has no build/test surface.
            await CompleteAsync(session, tasks, verification: null, review: null, ct);
            return;
        }

        while (true)
        {
            await SetStateAsync(session, "Verifying", ct);
            var runTests = session.RepairAttemptCount == 0 || tasks.Any(t => t.ToolName == "TestTool");
            var verification = await _verificationPipeline.RunAsync(session.Id, session.RepositoryId, session.RepairAttemptCount, runTests, ct);

            var review = await _reviewerService.ReviewAsync(session.ModelIdUsed!, session.UserRequest, verification, ct);
            verification.ReviewerVerdict = review.Verdict;
            verification.ReviewerReason = review.Reason;
            await _db.SaveChangesAsync(ct);

            var passed = verification.OverallResult == "Passed" && review.Verdict != "Rejected";
            if (passed)
            {
                await CompleteAsync(session, tasks, verification, review, ct);
                return;
            }

            if (session.RepairAttemptCount >= session.MaxRepairAttempts)
            {
                session.State = "Failed";
                session.FailureReason = BuildFailureReason(verification, review);
                session.CompletedAtUtc = DateTimeOffset.UtcNow;
                session.FinalSummary = BuildFinalSummary(session, tasks, verification, review);
                await _db.SaveChangesAsync(ct);
                await _broadcaster.SessionUpdatedAsync(session.Id, session.State, ct);
                await _memoryWriteService.RecordSessionOutcomeAsync(session, ct);
                return;
            }

            // ---- Repairing: real re-plan using the actual failure as context ----
            session.RepairAttemptCount++;
            await SetStateAsync(session, "Repairing", ct);

            var repairRequest =
                $"The previous attempt at this request failed verification. Original request: {session.UserRequest}\n" +
                $"Verification result: {BuildFailureReason(verification, review)}\n" +
                "Produce a short plan of additional steps to fix this.";

            var repairPlanning = await _planningService.CreatePlanAsync(session.ModelIdUsed!, repairRequest, _toolExecutionService.AllTools, ct);
            _db.TokenUsageRecords.Add(new TokenUsageRecord
            {
                AgentSessionId = session.Id,
                InputTokens = repairPlanning.ModelResult.InputTokens,
                OutputTokens = repairPlanning.ModelResult.OutputTokens,
                TokensPerSecond = repairPlanning.ModelResult.Duration.TotalSeconds > 0
                    ? repairPlanning.ModelResult.OutputTokens / repairPlanning.ModelResult.Duration.TotalSeconds : 0,
                TimeToFirstToken = repairPlanning.ModelResult.TimeToFirstToken
            });
            await _db.SaveChangesAsync(ct);

            if (repairPlanning.Plan is null)
            {
                session.State = "Failed";
                session.FailureReason = $"Repair planning failed: {repairPlanning.ParseError}";
                session.CompletedAtUtc = DateTimeOffset.UtcNow;
                session.FinalSummary = BuildFinalSummary(session, tasks, verification, review);
                await _db.SaveChangesAsync(ct);
                await _broadcaster.SessionUpdatedAsync(session.Id, session.State, ct);
                await _memoryWriteService.RecordSessionOutcomeAsync(session, ct);
                return;
            }

            var baseIndex = tasks.Count;
            Guid? previousTaskId = tasks.LastOrDefault()?.Id;
            for (var i = 0; i < repairPlanning.Plan.Steps.Count; i++)
            {
                var step = repairPlanning.Plan.Steps[i];
                var repairTask = new AgentTaskNode
                {
                    AgentSessionId = session.Id,
                    OrderIndex = baseIndex + i,
                    ParentId = previousTaskId,
                    Type = step.Type,
                    Description = $"[repair #{session.RepairAttemptCount}] {step.Description}",
                    ToolName = step.ToolName,
                    ArgumentsJson = step.Arguments is not null ? JsonSerializer.Serialize(step.Arguments) : null
                };
                _db.AgentTaskNodes.Add(repairTask);

                var stopped = await RunTaskWithRetriesAsync(session, repairTask, approved: false, ct);
                await _db.SaveChangesAsync(ct);
                tasks.Add(repairTask);

                if (stopped is not null)
                {
                    if (stopped == "AwaitingApproval") { await SetStateAsync(session, "AwaitingApproval", ct); return; }
                    session.State = "Failed";
                    session.FailureReason = stopped;
                    session.CompletedAtUtc = DateTimeOffset.UtcNow;
                    session.FinalSummary = BuildFinalSummary(session, tasks, verification, review);
                    await _db.SaveChangesAsync(ct);
                    await _broadcaster.SessionUpdatedAsync(session.Id, session.State, ct);
                    await _memoryWriteService.RecordSessionOutcomeAsync(session, ct);
                    return;
                }
                previousTaskId = repairTask.Id;
            }
            // loop back and re-verify
        }
    }

    private static string BuildFailureReason(VerificationRun v, ReviewOutcome review)
    {
        var parts = new List<string>();
        if (v.BuildPassed != true) parts.Add($"build failed ({v.CompilerErrorCount} error(s))");
        if (v.TestsRan == true && v.TestsPassed != true) parts.Add($"tests failed ({v.TestOutputSummary})");
        if (v.SecurityFindingCount > 0) parts.Add($"{v.SecurityFindingCount} security finding(s)");
        if (review.Verdict == "Rejected") parts.Add($"reviewer rejected: {review.Reason}");
        return parts.Count > 0 ? string.Join("; ", parts) : "verification failed for an unspecified reason";
    }

    private async Task CompleteAsync(AgentSession session, List<AgentTaskNode> tasks, VerificationRun? verification, ReviewOutcome? review, CancellationToken ct)
    {
        session.State = "Completed";
        session.CompletedAtUtc = DateTimeOffset.UtcNow;
        session.FinalSummary = BuildFinalSummary(session, tasks, verification, review);
        await _db.SaveChangesAsync(ct);
        await _broadcaster.SessionUpdatedAsync(session.Id, session.State, ct);
        await _memoryWriteService.RecordSessionOutcomeAsync(session, ct);
    }

    /// <summary>Deterministic, template-based summary from the actual task and
    /// verification results — no extra model call, no hidden chain-of-thought exposed
    /// (spec Section 61).</summary>
    private static string BuildFinalSummary(AgentSession session, List<AgentTaskNode> tasks, VerificationRun? verification, ReviewOutcome? review)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Request: {session.UserRequest}");
        sb.AppendLine($"Steps executed: {tasks.Count(t => t.Status == "Completed")}/{tasks.Count}");
        foreach (var t in tasks)
        {
            sb.AppendLine($"- [{t.Status}] {t.Description}" + (t.ToolName is not null ? $" (tool: {t.ToolName})" : ""));
            if (t.Status == "Failed" && t.Error is not null) sb.AppendLine($"    error: {t.Error}");
        }
        if (verification is not null)
        {
            sb.AppendLine($"Build: {(verification.BuildPassed == true ? "PASS" : "FAIL")} ({verification.CompilerErrorCount} error(s), {verification.CompilerWarningCount} warning(s))");
            if (verification.TestsRan == true) sb.AppendLine($"Tests: {verification.TestOutputSummary}");
            sb.AppendLine($"Security findings: {verification.SecurityFindingCount}");
            if (review is not null) sb.AppendLine($"Reviewer: {review.Verdict} — {review.Reason}");
        }
        if (session.RepairAttemptCount > 0) sb.AppendLine($"Repair attempts used: {session.RepairAttemptCount}/{session.MaxRepairAttempts}");
        return sb.ToString();
    }

    private async Task FailAsync(AgentSession session, string reason, CancellationToken ct)
    {
        session.State = "Failed";
        session.FailureReason = reason;
        session.CompletedAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _broadcaster.SessionUpdatedAsync(session.Id, session.State, ct);
        await _memoryWriteService.RecordSessionOutcomeAsync(session, ct);
    }

    private async Task SetStateAsync(AgentSession session, string state, CancellationToken ct)
    {
        session.State = state;
        await _db.SaveChangesAsync(ct);
        await _broadcaster.SessionUpdatedAsync(session.Id, state, ct);
    }

}

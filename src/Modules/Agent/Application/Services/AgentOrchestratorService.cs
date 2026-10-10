using System.Text;
using System.Text.Json;
using LocalAgentPlatform.Modules.Agent.Domain;
using LocalAgentPlatform.Modules.Memory.Application.Services;
using LocalAgentPlatform.Modules.RepositoryAnalysis.Application.Services;
using LocalAgentPlatform.Modules.Tools.Application.Services;
using LocalAgentPlatform.Modules.Verification.Application.Services;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.BackgroundWork;
using LocalAgentPlatform.Shared.Kernel.Models;
using LocalAgentPlatform.Shared.Kernel.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LocalAgentPlatform.Modules.Agent.Application.Services;

/// <summary>
/// The real agent execution loop (spec Section 8). Explicit states are persisted on
/// AgentSession.State at every transition. Plans are validated as dependency DAGs and
/// scheduled sequentially in deterministic topological order; parallel execution is
/// deferred until resource locking exists. Every tool call goes through the same
/// <see cref="ToolExecutionService"/> the Tools console uses, so agent-run invocations
/// receive the same policy gates and audit trail as human-run ones.
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
    private readonly IRepositoryContextEngine _contextEngine;
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
        IRepositoryContextEngine contextEngine,
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
        _contextEngine = contextEngine;
        _broadcaster = broadcaster;
        _registry = registry;
        _logger = logger;
    }

    public async Task RunAsync(Guid sessionId, CancellationToken externalCt = default)
    {
        if (!_registry.TryRegister(sessionId, out var cts))
        {
            _logger.LogWarning("Ignoring duplicate delivery for agent session {SessionId}.", sessionId);
            return;
        }
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, cts.Token);
        var ct = linkedCts.Token;
        var ownsSessionClaim = false;

        try
        {
            var session = await _db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct)
                ?? throw new InvalidOperationException($"AgentSession {sessionId} not found.");
            if (!string.Equals(session.State, "Created", StringComparison.Ordinal))
            {
                _logger.LogInformation("Ignoring duplicate or stale delivery for agent session {SessionId} in state {State}.", sessionId, session.State);
                return;
            }

            // Claim the session with a conditional database update as well as the
            // in-process registry. Duplicate queue deliveries on separate instances
            // cannot both begin planning from the same Created state.
            ct.ThrowIfCancellationRequested();
            var claimed = await _db.AgentSessions
                .Where(s => s.Id == sessionId && s.State == "Created")
                // The short claim must return a definite row count; otherwise a canceled
                // client call could commit the update but lose ownership of the run.
                .ExecuteUpdateAsync(update => update.SetProperty(s => s.State, "Understanding"), CancellationToken.None);
            if (claimed != 1)
            {
                _logger.LogInformation("Ignoring duplicate delivery for already-claimed agent session {SessionId}.", sessionId);
                return;
            }
            ownsSessionClaim = true;
            // The conditional update changed only this column; keep the already-loaded
            // tracked entity in sync without a second read that could fail after claim.
            session.State = "Understanding";
            await _broadcaster.SessionUpdatedAsync(session.Id, session.State, ct);

            // ---- Understanding / Planning ----
            await SetStateAsync(session, "Planning", ct);
            var tools = _toolExecutionService.AllTools;

            // Real retrieval-based memory (Section 14): pull relevant prior context for
            // this repository instead of injecting everything ever stored.
            var relevantMemory = await _memoryRetrievalService.RetrieveRelevantAsync(session.OwnerUserId, session.RepositoryId, session.UserRequest, ct: ct);
            var memoryContext = MemoryRetrievalService.FormatForPrompt(relevantMemory);
            var repoContext = await _contextEngine.BuildContextAsync(session.RepositoryId, session.UserRequest, ct: ct);
            var codeContext = RepositoryContextEngine.FormatForPrompt(repoContext);
            var combinedContext = string.Join("\n\n", new[] { memoryContext, codeContext }.Where(x => !string.IsNullOrWhiteSpace(x)));

            var planningOutcome = await _planningService.CreatePlanAsync(
                session.ModelIdUsed!, session.UserRequest, tools, ct, additionalContext: combinedContext);

            _db.TokenUsageRecords.Add(new TokenUsageRecord
            {
                AgentSessionId = session.Id,
                InputTokens = planningOutcome.ModelResult.InputTokens,
                OutputTokens = planningOutcome.ModelResult.OutputTokens,
                TokensPerSecond = planningOutcome.ModelResult.GenerationDuration is TimeSpan generationDuration && generationDuration.TotalSeconds > 0
                    ? planningOutcome.ModelResult.OutputTokens / generationDuration.TotalSeconds
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

            var stopReason = await ExecutePlanGraphAsync(session, planningOutcome.Plan, approvedTaskId: null, ct);
            if (stopReason is not null)
            {
                if (stopReason == "AwaitingApproval") await SetStateAsync(session, "AwaitingApproval", ct);
                else await FailAsync(session, stopReason, ct);
                return;
            }

            var verificationStop = await RunVerificationAsync(session, ct);
            if (verificationStop == "AwaitingApproval") await SetStateAsync(session, "AwaitingApproval", ct);
            else if (verificationStop is not null) await FailAsync(session, verificationStop, ct);
        }
        catch (OperationCanceledException)
        {
            var session = await _db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, CancellationToken.None);
            if (ownsSessionClaim && session is not null && session.State is not ("Completed" or "Failed" or "Cancelled"))
            {
                session.State = "Cancelled";
                session.CompletedAtUtc = DateTimeOffset.UtcNow;
                session.FinalSummary = "Cancelled by user request.";
                await _db.SaveChangesAsync(CancellationToken.None);
                await MarkTasksAfterCancellationAsync(sessionId, CancellationToken.None);
                await _broadcaster.SessionUpdatedAsync(session.Id, session.State, CancellationToken.None);
                await _memoryWriteService.RecordSessionOutcomeAsync(session, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent session {SessionId} failed with an unhandled exception.", sessionId);
            var session = await _db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, CancellationToken.None);
            if (ownsSessionClaim && session is not null && session.State is not ("Completed" or "Failed" or "Cancelled"))
                await FailAsync(session, $"Unhandled exception: {ex.Message}", CancellationToken.None);
        }
        finally
        {
            _registry.Unregister(sessionId);
        }
    }

    /// <summary>Atomically cancels a session that has not been claimed by a live run
    /// (queued or waiting for human approval). Active runs use the in-memory token
    /// registry so their current model/process call is interrupted promptly.</summary>
    public async Task<bool> CancelPendingAsync(Guid sessionId, Guid ownerUserId)
    {
        // Cancellation is the user's requested domain operation; don't let a browser
        // disconnect abort the atomic transition or its audit/memory side effects.
        var now = DateTimeOffset.UtcNow;
        var changed = await _db.AgentSessions
            .Where(s => s.Id == sessionId && s.OwnerUserId == ownerUserId &&
                        (s.State == "Created" || s.State == "AwaitingApproval"))
            .ExecuteUpdateAsync(update => update
                .SetProperty(s => s.State, "Cancelled")
                .SetProperty(s => s.CompletedAtUtc, now)
                .SetProperty(s => s.FinalSummary, "Cancelled before execution started."), CancellationToken.None);
        if (changed != 1) return false;

        // Once the conditional update commits, finish the audit/memory side effects even
        // if the requesting browser disconnects.
        var session = await _db.AgentSessions.FirstOrDefaultAsync(
            s => s.Id == sessionId && s.OwnerUserId == ownerUserId, CancellationToken.None);
        if (session is null) return false;
        await MarkTasksAfterCancellationAsync(sessionId, CancellationToken.None);
        await _broadcaster.SessionUpdatedAsync(session.Id, session.State, CancellationToken.None);
        await _memoryWriteService.RecordSessionOutcomeAsync(session, CancellationToken.None);
        return true;
    }

    /// <summary>Resumes a session sitting in AwaitingApproval: approves exactly the
    /// pending task, then continues the loop from the next step.</summary>
    public Task ApproveAndResumeAsync(Guid sessionId, Guid taskId, CancellationToken ct = default) =>
        ResumeInternalAsync(sessionId, taskId, ct);

    private async Task ResumeInternalAsync(Guid sessionId, Guid taskId, CancellationToken externalCt)
    {
        var session = await _db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, externalCt)
            ?? throw new InvalidOperationException($"AgentSession {sessionId} not found.");
        if (!string.Equals(session.State, "AwaitingApproval", StringComparison.Ordinal))
            throw new InvalidOperationException("Agent session is not awaiting approval.");
        var task = await _db.AgentTaskNodes.FirstOrDefaultAsync(
                t => t.Id == taskId && t.AgentSessionId == sessionId && t.Status == "AwaitingApproval", externalCt)
            ?? throw new InvalidOperationException("Pending approval task was not found for this session.");

        if (!_registry.TryRegister(sessionId, out var cts))
            throw new InvalidOperationException($"Agent session {sessionId} is already running.");
        try
        {
            externalCt.ThrowIfCancellationRequested();
            var claimed = await _db.AgentSessions
                .Where(s => s.Id == sessionId && s.State == "AwaitingApproval")
                .ExecuteUpdateAsync(update => update.SetProperty(s => s.State, "Executing"), CancellationToken.None);
            if (claimed != 1)
                throw new InvalidOperationException("Agent session approval was already claimed or is no longer pending.");
            // The conditional update changed only State. Synchronize the tracked entity
            // locally so no follow-up database read can fail after we own the claim.
            session.State = "Executing";
        }
        catch
        {
            _registry.Unregister(sessionId);
            throw;
        }
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

            var plan = JsonSerializer.Deserialize<AgentPlan>(session.PlanJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (plan is null) { await FailAsync(session, "Could not resume: stored plan JSON was invalid.", ct); return; }

            var stopReason = await ExecutePlanGraphAsync(session, plan, approvedTaskId: null, ct);
            if (stopReason is not null)
            {
                if (stopReason == "AwaitingApproval") await SetStateAsync(session, "AwaitingApproval", ct);
                else await FailAsync(session, stopReason, ct);
                return;
            }

            var verificationStop = await RunVerificationAsync(session, ct);
            if (verificationStop == "AwaitingApproval") await SetStateAsync(session, "AwaitingApproval", ct);
            else if (verificationStop is not null) await FailAsync(session, verificationStop, ct);
        }
        catch (OperationCanceledException)
        {
            if (session.State is not ("Completed" or "Failed" or "Cancelled"))
            {
                session.State = "Cancelled";
                session.CompletedAtUtc = DateTimeOffset.UtcNow;
                session.FinalSummary = "Cancelled by user request.";
                await _db.SaveChangesAsync(CancellationToken.None);
                await MarkTasksAfterCancellationAsync(sessionId, CancellationToken.None);
                await _broadcaster.SessionUpdatedAsync(session.Id, session.State, CancellationToken.None);
                await _memoryWriteService.RecordSessionOutcomeAsync(session, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent session {SessionId} failed while resuming after approval.", sessionId);
            if (session.State is not ("Completed" or "Failed" or "Cancelled"))
                await FailAsync(session, $"Unhandled exception while resuming: {ex.Message}", CancellationToken.None);
        }
        finally
        {
            _registry.Unregister(sessionId);
        }
    }

    private async Task<string?> ExecutePlanGraphAsync(AgentSession session, AgentPlan plan, Guid? approvedTaskId, CancellationToken ct)
    {
        var orderedSteps = AgentPlanGraph.TryTopologicalOrder(plan.Steps);
        if (orderedSteps is null) return "Plan graph is invalid: dependencies are missing, duplicated, or cyclic.";

        var tasks = await _db.AgentTaskNodes.Where(t => t.AgentSessionId == session.Id).ToListAsync(ct);
        var byStep = tasks.Where(t => !string.IsNullOrWhiteSpace(t.StepKey))
            .ToDictionary(t => t.StepKey, StringComparer.OrdinalIgnoreCase);
        var nextOrder = tasks.Count == 0 ? 0 : tasks.Max(t => t.OrderIndex) + 1;

        for (var i = 0; i < orderedSteps.Count; i++)
        {
            var step = orderedSteps[i];
            if (byStep.ContainsKey(step.Id)) continue;
            var deps = step.DependsOn ?? Array.Empty<string>();
            var task = new AgentTaskNode
            {
                AgentSessionId = session.Id,
                OrderIndex = nextOrder + i,
                StepKey = step.Id,
                DependenciesJson = JsonSerializer.Serialize(deps),
                Type = step.Type,
                Description = step.Description,
                ToolName = step.ToolName,
                ArgumentsJson = step.Arguments is null ? null : JsonSerializer.Serialize(step.Arguments)
            };
            _db.AgentTaskNodes.Add(task);
            byStep[step.Id] = task;
        }
        await _db.SaveChangesAsync(ct);

        foreach (var step in orderedSteps)
        {
            var task = byStep[step.Id];
            if (task.Status == "Completed") continue;
            var deps = step.DependsOn ?? Array.Empty<string>();
            var failedDependency = deps.FirstOrDefault(d => byStep[d].Status is "Failed" or "Skipped");
            if (failedDependency is not null)
            {
                task.Status = "Skipped";
                task.Error = $"Dependency '{failedDependency}' did not complete successfully.";
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.TaskUpdatedAsync(session.Id, task.Id, task.Status, ct);
                return $"Plan step '{step.Id}' cannot run because dependency '{failedDependency}' failed.";
            }
            if (deps.Any(d => byStep[d].Status != "Completed"))
                return $"Plan graph stalled before '{step.Id}': one or more dependencies are incomplete.";

            task.ParentId = deps.Count > 0 ? byStep[deps[0]].Id : null;
            var stopped = await RunTaskWithRetriesAsync(session, task, approvedTaskId == task.Id, ct);
            await _db.SaveChangesAsync(ct);
            if (stopped is not null) return stopped;
        }
        return null;
    }

    /// <summary>Runs one task node, retrying tool failures up to the session's MaxRetries.
    /// Returns null on success, "AwaitingApproval" if it needs a human, or a failure
    /// reason string if the task (or a budget) exhausted its allowance.</summary>
    private async Task<string?> RunTaskWithRetriesAsync(AgentSession session, AgentTaskNode task, bool approved, CancellationToken ct)
    {
        await SetStateAsync(session, "Executing", ct);
        task.Status = "Executing";
        task.Error = null;
        task.StartedAtUtc ??= DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _broadcaster.TaskUpdatedAsync(session.Id, task.Id, task.Status, ct);

        while (true)
        {
            var budget = AgentBudgetPolicy.Check(
                session.IterationCount, session.MaxIterations,
                task.RetryCount, session.MaxRetries,
                session.CreatedAtUtc, session.MaxDurationMinutes, DateTimeOffset.UtcNow);

            if (!budget.CanContinue)
            {
                task.Status = "Failed";
                task.Error = budget.StopReason;
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.TaskUpdatedAsync(session.Id, task.Id, task.Status, ct);
                return budget.StopReason;
            }

            // Count only attempts that actually proceed past the budget gate. This
            // permits exactly MaxIterations tool/reasoning attempts rather than one fewer.
            session.IterationCount++;

            if (task.Type == "Reasoning")
            {
                task.Status = "Completed";
                task.Output = task.Description;
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.TaskUpdatedAsync(session.Id, task.Id, task.Status, ct);
                return null;
            }

            if (string.IsNullOrWhiteSpace(task.ToolName))
            {
                task.Status = "Failed";
                task.Error = "Plan step marked as ToolCall but named no tool.";
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.TaskUpdatedAsync(session.Id, task.Id, task.Status, ct);
                return task.Error;
            }

            Dictionary<string, string> arguments;
            try
            {
                arguments = string.IsNullOrEmpty(task.ArgumentsJson)
                    ? new Dictionary<string, string>()
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(task.ArgumentsJson)
                      ?? throw new JsonException("Tool arguments were null.");
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
            {
                task.Status = "Failed";
                task.Error = $"Invalid persisted tool arguments: {ex.Message}";
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.TaskUpdatedAsync(session.Id, task.Id, task.Status, ct);
                return task.Error;
            }

            // Persisted task arguments are untrusted metadata, even if they were valid
            // when originally planned. Never pass null/blank paths onward to file tools.
            foreach (var key in arguments.Keys.Where(k => k.Equals("path", StringComparison.OrdinalIgnoreCase)).ToArray())
                if (string.IsNullOrWhiteSpace(arguments[key])) arguments.Remove(key);

            ToolInvocationOutcome outcome;
            try
            {
                outcome = await _toolExecutionService.InvokeAsync(
                    task.ToolName, session.RepositoryId, arguments, approved, ct, session.OwnerUserId);
            }
            catch (InvalidOperationException ex)
            {
                task.Status = "Failed";
                task.Error = ex.Message;
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.TaskUpdatedAsync(session.Id, task.Id, task.Status, ct);
                return ex.Message;
            }

            if (outcome.Decision == "Denied")
            {
                task.Status = "Failed";
                task.Error = $"Denied by command policy: {outcome.DecisionReason}";
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.TaskUpdatedAsync(session.Id, task.Id, task.Status, ct);
                return task.Error;
            }

            if (outcome.Decision == "PendingApproval")
            {
                task.Status = "AwaitingApproval";
                task.Error = outcome.DecisionReason;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.TaskUpdatedAsync(session.Id, task.Id, task.Status, ct);
                return "AwaitingApproval";
            }

            // Verification tasks record the actual compiler/test result as a completed
            // action even when the process exits non-zero. The verification pipeline—not
            // the generic tool retry handler—decides whether a failed build/test is a
            // repairable verification failure.
            if (string.Equals(task.Type, "Verification", StringComparison.Ordinal))
            {
                task.Status = "Completed";
                task.Output = outcome.Result?.Output ?? string.Empty;
                task.Error = outcome.Result?.Success == true ? null : outcome.Result?.Error ?? "Tool returned no result.";
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.TaskUpdatedAsync(session.Id, task.Id, task.Status, ct);
                return null;
            }

            if (outcome.Result is { Success: true })
            {
                task.Status = "Completed";
                task.Output = outcome.Result.Output;
                task.Error = null;
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.TaskUpdatedAsync(session.Id, task.Id, task.Status, ct);
                return null;
            }

            task.RetryCount++;
            task.Error = outcome.Result?.Error ?? "Tool returned failure with no error message.";
            // One-time approval is consumed by the invocation above. A retry that would
            // execute a high-risk tool or command must return to the approval gate.
            approved = false;
            if (task.RetryCount > session.MaxRetries)
            {
                task.Status = "Failed";
                task.CompletedAtUtc = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
                await _broadcaster.TaskUpdatedAsync(session.Id, task.Id, task.Status, ct);
                return $"Task '{task.Description}' failed after {task.RetryCount} attempts: {task.Error}";
            }

            task.Status = "Executing";
            await _db.SaveChangesAsync(ct);
            await _broadcaster.TaskUpdatedAsync(session.Id, task.Id, task.Status, ct);
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
    private async Task<string?> RunVerificationAsync(AgentSession session, CancellationToken ct)
    {
        var tasks = await _db.AgentTaskNodes
            .Where(t => t.AgentSessionId == session.Id)
            .OrderBy(t => t.OrderIndex)
            .ToListAsync(ct);

        while (true)
        {
            await SetStateAsync(session, "Verifying", ct);
            var attempt = session.RepairAttemptCount;

            // Builds and tests execute repository-controlled MSBuild/test code. They are
            // high-risk tools, so verification obtains an explicit one-time approval and
            // passes only that actual result into the verifier.
            var buildGate = await EnsureVerificationToolAsync(session, "BuildTool", attempt, ct);
            if (buildGate.StopReason is not null) return buildGate.StopReason;
            var buildResult = ToVerificationResult(buildGate.Task!);

            var runTests = true;
            ToolExecutionResult? testResult = null;
            if (buildResult.Success && runTests)
            {
                var testGate = await EnsureVerificationToolAsync(session, "TestTool", attempt, ct);
                if (testGate.StopReason is not null) return testGate.StopReason;
                testResult = ToVerificationResult(testGate.Task!);
            }

            var verification = await _verificationPipeline.RunAsync(
                session.Id, session.RepositoryId, attempt, runTests, buildResult, testResult, ct);

            var review = await _reviewerService.ReviewAsync(session.ModelIdUsed!, session.UserRequest, verification, ct);
            verification.ReviewerVerdict = review.Verdict;
            verification.ReviewerReason = review.Reason;
            await _db.SaveChangesAsync(ct);
            tasks = await _db.AgentTaskNodes
                .Where(t => t.AgentSessionId == session.Id)
                .OrderBy(t => t.OrderIndex)
                .ToListAsync(ct);

            var passed = verification.OverallResult == "Passed" && review.Verdict != "Rejected";
            if (passed)
            {
                await CompleteAsync(session, tasks, verification, review, ct);
                return null;
            }

            if (session.RepairAttemptCount >= session.MaxRepairAttempts)
            {
                var reason = BuildFailureReason(verification, review);
                var summary = BuildFinalSummary(session, tasks, verification, review);
                await FailAsync(session, reason, ct, summary);
                return null;
            }

            session.RepairAttemptCount++;
            await SetStateAsync(session, "Repairing", ct);
            var repairRequest =
                $"The previous attempt at this request failed verification. Original request: {session.UserRequest}\n" +
                $"Verification result: {BuildFailureReason(verification, review)}\n" +
                "Produce a short plan of additional steps to fix this.";

            var repairPlanning = await _planningService.CreatePlanAsync(
                session.ModelIdUsed!, repairRequest, _toolExecutionService.AllTools, ct);
            await RecordTokenUsageAsync(session, repairPlanning.ModelResult, ct);

            if (repairPlanning.Plan is null)
            {
                var reason = $"Repair planning failed: {repairPlanning.ParseError}";
                var summary = BuildFinalSummary(session, tasks, verification, review);
                await FailAsync(session, reason, ct, summary);
                return null;
            }

            // Persist the current repair plan before execution. If approval is requested,
            // resume uses these stable IDs rather than replaying the original plan.
            var repairPlan = PrefixPlan(repairPlanning.Plan, $"repair-{session.RepairAttemptCount}-");
            session.PlanJson = JsonSerializer.Serialize(repairPlan, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await _db.SaveChangesAsync(ct);
            var stopReason = await ExecutePlanGraphAsync(session, repairPlan, approvedTaskId: null, ct);
            if (stopReason is not null) return stopReason;

            tasks = await _db.AgentTaskNodes
                .Where(t => t.AgentSessionId == session.Id)
                .OrderBy(t => t.OrderIndex)
                .ToListAsync(ct);
        }
    }

    private async Task<(AgentTaskNode? Task, string? StopReason)> EnsureVerificationToolAsync(
        AgentSession session, string toolName, int attempt, CancellationToken ct)
    {
        var stepKey = $"__system_verification_{attempt}_{toolName}";
        var task = await _db.AgentTaskNodes.FirstOrDefaultAsync(
            t => t.AgentSessionId == session.Id && t.StepKey == stepKey, ct);
        if (task is null)
        {
            var nextOrder = await _db.AgentTaskNodes.Where(t => t.AgentSessionId == session.Id)
                .Select(t => (int?)t.OrderIndex).MaxAsync(ct) ?? -1;
            task = new AgentTaskNode
            {
                AgentSessionId = session.Id,
                OrderIndex = nextOrder + 1,
                StepKey = stepKey,
                Type = "Verification",
                Description = toolName == "BuildTool"
                    ? "Run an approved workspace build for verification. Build targets can execute repository code."
                    : "Run approved workspace tests for verification. Tests execute repository code.",
                ToolName = toolName,
                ArgumentsJson = "{}",
                DependenciesJson = "[]"
            };
            _db.AgentTaskNodes.Add(task);
            await _db.SaveChangesAsync(ct);
        }

        if (task.Status == "Completed") return (task, null);
        if (task.Status == "Failed") return (null, task.Error ?? $"{toolName} verification command failed to execute.");

        var stopReason = await RunTaskWithRetriesAsync(session, task, approved: false, ct);
        await _db.SaveChangesAsync(ct);
        return stopReason is null ? (task, null) : (null, stopReason);
    }

    private static ToolExecutionResult ToVerificationResult(AgentTaskNode task) =>
        new(task.Error is null, task.Output ?? string.Empty, task.Error);

    private async Task RecordTokenUsageAsync(AgentSession session, ModelGenerationResult result, CancellationToken ct)
    {
        _db.TokenUsageRecords.Add(new TokenUsageRecord
        {
            AgentSessionId = session.Id,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            TokensPerSecond = result.GenerationDuration is TimeSpan generationDuration && generationDuration.TotalSeconds > 0
                ? result.OutputTokens / generationDuration.TotalSeconds
                : 0,
            TimeToFirstToken = result.TimeToFirstToken
        });
        await _db.SaveChangesAsync(ct);
    }

    private static AgentPlan PrefixPlan(AgentPlan plan, string prefix)
    {
        var ids = plan.Steps.ToDictionary(s => s.Id, s => prefix + s.Id, StringComparer.OrdinalIgnoreCase);
        return new AgentPlan(plan.Steps.Select(s => s with
        {
            Id = ids[s.Id],
            DependsOn = (s.DependsOn ?? Array.Empty<string>()).Select(d => ids[d]).ToArray()
        }).ToList());
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

    private async Task MarkTasksAfterCancellationAsync(Guid sessionId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var executing = await _db.AgentTaskNodes
            .Where(t => t.AgentSessionId == sessionId && t.Status == "Executing")
            .ToListAsync(ct);
        foreach (var task in executing)
        {
            task.Status = "Cancelled";
            task.Error = "Task was cancelled with its agent session.";
            task.CompletedAtUtc = now;
        }

        var pending = await _db.AgentTaskNodes
            .Where(t => t.AgentSessionId == sessionId && (t.Status == "Pending" || t.Status == "AwaitingApproval"))
            .ToListAsync(ct);
        foreach (var task in pending)
        {
            task.Status = "Skipped";
            task.Error = "Task was skipped because the agent session was cancelled.";
            task.CompletedAtUtc = now;
        }

        if (executing.Count + pending.Count == 0) return;
        await _db.SaveChangesAsync(ct);
        foreach (var task in executing.Concat(pending))
            await _broadcaster.TaskUpdatedAsync(sessionId, task.Id, task.Status, ct);
    }

    private async Task MarkTasksAfterFailureAsync(Guid sessionId, string reason, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var executing = await _db.AgentTaskNodes
            .Where(t => t.AgentSessionId == sessionId && t.Status == "Executing")
            .ToListAsync(ct);
        foreach (var task in executing)
        {
            task.Status = "Failed";
            task.Error = reason.Length > 2_000 ? reason[..2_000] + "... [truncated]" : reason;
            task.CompletedAtUtc = now;
        }

        var pending = await _db.AgentTaskNodes
            .Where(t => t.AgentSessionId == sessionId && (t.Status == "Pending" || t.Status == "AwaitingApproval"))
            .ToListAsync(ct);
        foreach (var task in pending)
        {
            task.Status = "Skipped";
            task.Error = "Task was skipped because the agent session failed.";
            task.CompletedAtUtc = now;
        }

        if (executing.Count + pending.Count == 0) return;
        await _db.SaveChangesAsync(ct);
        foreach (var task in executing.Concat(pending))
            await _broadcaster.TaskUpdatedAsync(sessionId, task.Id, task.Status, ct);
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

    private async Task FailAsync(AgentSession session, string reason, CancellationToken ct, string? finalSummary = null)
    {
        session.State = "Failed";
        session.FailureReason = reason;
        if (finalSummary is not null) session.FinalSummary = finalSummary;
        session.CompletedAtUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // The terminal session transition is committed. Finish task cleanup and audit
        // side effects even if the originating queue/request token is shutting down.
        try { await MarkTasksAfterFailureAsync(session.Id, reason, CancellationToken.None); }
        catch (Exception ex) { _logger.LogError(ex, "Could not finalize task statuses for failed agent session {SessionId}.", session.Id); }
        await _broadcaster.SessionUpdatedAsync(session.Id, session.State, CancellationToken.None);
        await _memoryWriteService.RecordSessionOutcomeAsync(session, CancellationToken.None);
    }

    private async Task SetStateAsync(AgentSession session, string state, CancellationToken ct)
    {
        session.State = state;
        await _db.SaveChangesAsync(ct);
        await _broadcaster.SessionUpdatedAsync(session.Id, state, ct);
    }

}

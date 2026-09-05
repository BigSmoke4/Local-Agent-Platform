namespace LocalAgentPlatform.Modules.Agent.Domain;

/// <summary>
/// Pure resource-budget checks for the agent loop (spec Section 47: "prevent infinite
/// loops... maxIterations, maxRetries, maxDuration"). No I/O — the orchestrator calls
/// this every iteration and stops the loop the moment any budget is exceeded, always
/// with an explicit reason surfaced to the user rather than a silent stop.
/// </summary>
public static class AgentBudgetPolicy
{
    public static BudgetCheckResult Check(
        int iterationCount, int maxIterations,
        int currentTaskRetryCount, int maxRetries,
        DateTimeOffset startedAtUtc, int maxDurationMinutes,
        DateTimeOffset nowUtc)
    {
        if (iterationCount >= maxIterations)
            return new BudgetCheckResult(false, $"Stopped: reached the maximum of {maxIterations} iterations.");

        if (currentTaskRetryCount > maxRetries)
            return new BudgetCheckResult(false, $"Stopped: a task exceeded the maximum of {maxRetries} retries.");

        var elapsed = nowUtc - startedAtUtc;
        if (elapsed.TotalMinutes >= maxDurationMinutes)
            return new BudgetCheckResult(false, $"Stopped: reached the maximum duration of {maxDurationMinutes} minutes (ran for {elapsed.TotalMinutes:0.0}m).");

        return new BudgetCheckResult(true, null);
    }
}

public sealed record BudgetCheckResult(bool CanContinue, string? StopReason);

/// <summary>
/// The JSON contract the planning prompt asks the model to produce, and the type the
/// orchestrator deserializes into. Kept in Domain (not Infrastructure) because both the
/// prompt-builder and the executor need the same shape and neither should own it.
/// </summary>
public sealed record AgentPlan(List<AgentPlanStep> Steps);

public sealed record AgentPlanStep(
    string Description,
    string Type, // "ToolCall" or "Reasoning"
    string? ToolName,
    Dictionary<string, string>? Arguments
);

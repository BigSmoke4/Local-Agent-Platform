using LocalAgentPlatform.Modules.Agent.Domain;
using Xunit;

namespace LocalAgentPlatform.Domain.Tests;

public class AgentBudgetPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Allows_continuation_within_all_budgets()
    {
        var result = AgentBudgetPolicy.Check(
            iterationCount: 5, maxIterations: 20,
            currentTaskRetryCount: 1, maxRetries: 3,
            startedAtUtc: Now, maxDurationMinutes: 15, nowUtc: Now.AddMinutes(2));

        Assert.True(result.CanContinue);
        Assert.Null(result.StopReason);
    }

    [Fact]
    public void Stops_when_iteration_limit_reached()
    {
        var result = AgentBudgetPolicy.Check(20, 20, 0, 3, Now, 15, Now);
        Assert.False(result.CanContinue);
        Assert.Contains("iterations", result.StopReason);
    }

    [Fact]
    public void Stops_when_retry_limit_exceeded()
    {
        var result = AgentBudgetPolicy.Check(1, 20, 4, 3, Now, 15, Now);
        Assert.False(result.CanContinue);
        Assert.Contains("retries", result.StopReason);
    }

    [Fact]
    public void Stops_when_duration_limit_reached()
    {
        var result = AgentBudgetPolicy.Check(1, 20, 0, 3, Now, 15, Now.AddMinutes(16));
        Assert.False(result.CanContinue);
        Assert.Contains("duration", result.StopReason);
    }

    [Fact]
    public void Retry_count_exactly_at_max_is_still_allowed()
    {
        // Orchestrator semantics: retryCount > maxRetries is the stop condition, so
        // being exactly at the max should still be allowed to attempt once more.
        var result = AgentBudgetPolicy.Check(1, 20, 3, 3, Now, 15, Now);
        Assert.True(result.CanContinue);
    }
}

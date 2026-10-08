using LocalAgentPlatform.Modules.Agent.Domain;
using Xunit;

namespace LocalAgentPlatform.Domain.Tests;

public sealed class AgentPlanGraphTests
{
    [Fact]
    public void Orders_a_branching_dag_deterministically_without_serializing_dependencies_as_chains()
    {
        var steps = new[]
        {
            Step("finish", "left", "right"),
            Step("right"),
            Step("start"),
            Step("left", "start")
        };

        var ordered = AgentPlanGraph.TryTopologicalOrder(steps);

        Assert.NotNull(ordered);
        Assert.Equal(new[] { "right", "start", "left", "finish" }, ordered!.Select(step => step.Id));
    }

    [Fact]
    public void Rejects_unknown_self_duplicate_and_cyclic_dependencies_or_duplicate_ids()
    {
        Assert.Null(AgentPlanGraph.TryTopologicalOrder(new[] { Step("a", "missing") }));
        Assert.Null(AgentPlanGraph.TryTopologicalOrder(new[] { Step("a", "a") }));
        Assert.Null(AgentPlanGraph.TryTopologicalOrder(new[] { Step("a", "b"), Step("b", "a") }));
        Assert.Null(AgentPlanGraph.TryTopologicalOrder(new[] { Step("a", "b", "B"), Step("b") }));
        Assert.Null(AgentPlanGraph.TryTopologicalOrder(new[] { Step("a"), Step("A") }));
        Assert.Null(AgentPlanGraph.TryTopologicalOrder(new[] { Step("bad id") }));
    }

    [Fact]
    public void Enforces_the_plan_size_limit()
    {
        var tooMany = Enumerable.Range(0, 21).Select(i => Step($"step-{i}")).ToArray();
        Assert.Null(AgentPlanGraph.TryTopologicalOrder(tooMany));
    }

    private static AgentPlanStep Step(string id, params string[] dependencies) => new(
        Id: id,
        Description: $"Step {id}",
        Type: "Reasoning",
        ToolName: null,
        Arguments: null,
        DependsOn: dependencies);
}

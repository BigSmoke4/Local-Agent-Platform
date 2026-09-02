using Microsoft.Extensions.Logging.Abstractions;
using Platform.Web.Services.Planning;
using Xunit;

namespace Platform.Tests;

public class PlannerServiceTests
{
    [Fact]
    public async Task CreatePlanAsync_ParsesValidJsonArray()
    {
        const string modelResponse = """
            [
              {"type": "Build", "description": "Build the solution"},
              {"type": "Test", "description": "Run the test suite"},
              {"type": "Review", "description": "Independent review"}
            ]
            """;

        var planner = new PlannerService(new FixedResponseModelProvider(modelResponse), NullLogger<PlannerService>.Instance);
        var result = await planner.CreatePlanAsync("fake-model", "Verify the build is healthy");

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal("Build", result.Steps[0].Type);
    }

    [Fact]
    public async Task CreatePlanAsync_StripsMarkdownFencesAroundJson()
    {
        const string modelResponse = """
            Here is the plan:
            ```json
            [{"type": "Build", "description": "Build it"}]
            ```
            """;

        var planner = new PlannerService(new FixedResponseModelProvider(modelResponse), NullLogger<PlannerService>.Instance);
        var result = await planner.CreatePlanAsync("fake-model", "Build the project");

        Assert.True(result.Succeeded);
        Assert.Single(result.Steps);
    }

    [Fact]
    public async Task CreatePlanAsync_DropsUnknownStepTypes_KeepsKnownOnes()
    {
        const string modelResponse = """
            [
              {"type": "Build", "description": "Build it"},
              {"type": "DeployToProduction", "description": "Not a real tool in this codebase"}
            ]
            """;

        var planner = new PlannerService(new FixedResponseModelProvider(modelResponse), NullLogger<PlannerService>.Instance);
        var result = await planner.CreatePlanAsync("fake-model", "Ship it");

        Assert.True(result.Succeeded);
        Assert.Single(result.Steps);
        Assert.Equal("Build", result.Steps[0].Type);
    }

    [Fact]
    public async Task CreatePlanAsync_UnparseableResponse_ReturnsFailureNotFakeSuccess()
    {
        var planner = new PlannerService(new FixedResponseModelProvider("not json at all"), NullLogger<PlannerService>.Instance);
        var result = await planner.CreatePlanAsync("fake-model", "Do something");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Steps);
        Assert.NotNull(result.Error);
    }
}

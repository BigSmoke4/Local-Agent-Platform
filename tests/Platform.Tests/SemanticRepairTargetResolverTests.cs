using Microsoft.Extensions.Logging.Abstractions;
using Platform.Web.Services.CodeIntelligence;
using Xunit;

namespace Platform.Tests;

public class SemanticRepairTargetResolverTests
{
    private const string SampleBuildOutput = """
        src/Web/Controllers/FooController.cs(23,13): error CS0103: The name 'bar' does not exist in the current context [/repo/src/Web/Platform.Web.csproj]

            0 Warning(s)
            1 Error(s)
        """;

    [Fact]
    public async Task ResolveAsync_NoWorkspaceLoaded_FallsBackToCompilerFileListHonestly()
    {
        // A fresh SemanticCodeGraphService with no LoadSolutionAsync call —
        // IsLoaded is false, so the resolver must fall back rather than
        // claim semantic expansion happened.
        var semantic = new SemanticCodeGraphService(NullLogger<SemanticCodeGraphService>.Instance);
        var resolver = new SemanticRepairTargetResolver(semantic, NullLogger<SemanticRepairTargetResolver>.Instance);

        var result = await resolver.ResolveAsync(SampleBuildOutput);

        Assert.Single(result.Files);
        Assert.Equal("src/Web/Controllers/FooController.cs", result.Files[0]);
        Assert.Contains("No semantic workspace loaded", result.Reasoning);
    }

    [Fact]
    public async Task ResolveAsync_NoErrors_ReturnsEmptyFileList()
    {
        var semantic = new SemanticCodeGraphService(NullLogger<SemanticCodeGraphService>.Instance);
        var resolver = new SemanticRepairTargetResolver(semantic, NullLogger<SemanticRepairTargetResolver>.Instance);

        var result = await resolver.ResolveAsync("Build succeeded.\n0 Warning(s)\n0 Error(s)");

        Assert.Empty(result.Files);
    }
}

using LocalAgentPlatform.Modules.RepositoryAnalysis.Domain;
using Xunit;

namespace LocalAgentPlatform.Domain.Tests;

public class IndexingIgnoreRulesTests
{
    [Theory]
    [InlineData(".git")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    public void Ignores_standard_build_and_vcs_directories(string dirName)
    {
        Assert.True(IndexingIgnoreRules.IsIgnoredDirectory(dirName));
    }

    [Fact]
    public void Does_not_ignore_ordinary_source_directories()
    {
        Assert.False(IndexingIgnoreRules.IsIgnoredDirectory("Controllers"));
        Assert.False(IndexingIgnoreRules.IsIgnoredDirectory("src"));
    }

    [Fact]
    public void Ignored_path_check_catches_a_nested_ignored_segment()
    {
        Assert.True(IndexingIgnoreRules.IsIgnoredPath("src/Project/bin/Debug/net8.0/App.dll"));
    }

    [Fact]
    public void Ordinary_nested_path_is_not_ignored()
    {
        Assert.False(IndexingIgnoreRules.IsIgnoredPath("src/Modules/Agent/Domain/AgentBudgetPolicy.cs"));
    }

    [Theory]
    [InlineData("Program.cs", "csharp")]
    [InlineData("app.tsx", "typescript-react")]
    [InlineData("readme.md", "markdown")]
    [InlineData("data.unknownext", null)]
    public void Detects_language_from_extension_without_guessing_unknowns(string fileName, string? expected)
    {
        Assert.Equal(expected, IndexingIgnoreRules.DetectLanguage(fileName));
    }
}

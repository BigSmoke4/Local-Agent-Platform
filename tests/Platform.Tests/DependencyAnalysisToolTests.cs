using Microsoft.Extensions.Configuration;
using Platform.Web.Services.Tools;
using Xunit;

namespace Platform.Tests;

public class DependencyAnalysisToolTests : IDisposable
{
    private readonly string _tempWorkspace;

    public DependencyAnalysisToolTests()
    {
        _tempWorkspace = Path.Combine(Path.GetTempPath(), "platform-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempWorkspace);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempWorkspace))
            Directory.Delete(_tempWorkspace, recursive: true);
    }

    [Fact]
    public async Task AnalyzeAsync_ParsesRealPackageReferences()
    {
        var csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageReference Include="Serilog" Version="3.1.1" />
              </ItemGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(Path.Combine(_tempWorkspace, "Test.csproj"), csproj);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspace:Root"] = _tempWorkspace })
            .Build();

        var tool = new DependencyAnalysisTool(config);
        var results = await tool.AnalyzeAsync();

        Assert.Single(results);
        Assert.Equal("net8.0", results[0].TargetFramework);
        Assert.Equal(2, results[0].Packages.Count);
        Assert.Contains(results[0].Packages, p => p.Name == "Newtonsoft.Json" && p.Version == "13.0.3");
    }
}

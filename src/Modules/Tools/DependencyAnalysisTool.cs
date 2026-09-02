using System.Xml.Linq;

namespace Platform.Web.Services.Tools;

public record PackageDependency(string Name, string? Version);
public record ProjectDependencies(string ProjectFile, string? TargetFramework, List<PackageDependency> Packages);

/// <summary>
/// Real XML parsing of .csproj files in the workspace — reads actual
/// PackageReference elements, no fabricated dependency list.
/// </summary>
public class DependencyAnalysisTool
{
    private readonly string _workspaceRoot;

    public string Name => "DependencyAnalysisTool";

    public DependencyAnalysisTool(IConfiguration config)
    {
        _workspaceRoot = Path.GetFullPath(
            config["Workspace:Root"] ?? Path.Combine(AppContext.BaseDirectory, "workspace"));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public async Task<List<ProjectDependencies>> AnalyzeAsync(CancellationToken ct = default)
    {
        var results = new List<ProjectDependencies>();

        var csprojFiles = Directory.EnumerateFiles(_workspaceRoot, "*.csproj", SearchOption.AllDirectories);

        foreach (var file in csprojFiles)
        {
            ct.ThrowIfCancellationRequested();

            XDocument doc;
            try
            {
                doc = XDocument.Load(file);
            }
            catch (Exception)
            {
                continue; // malformed project file — skip rather than guess its contents
            }

            var targetFramework = doc.Descendants("TargetFramework").FirstOrDefault()?.Value
                ?? doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value;

            var packages = doc.Descendants("PackageReference")
                .Select(e => new PackageDependency(
                    e.Attribute("Include")?.Value ?? "unknown",
                    e.Attribute("Version")?.Value))
                .ToList();

            results.Add(new ProjectDependencies(
                Path.GetRelativePath(_workspaceRoot, file),
                targetFramework,
                packages));
        }

        return results;
    }
}

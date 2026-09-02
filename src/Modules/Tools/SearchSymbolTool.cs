using System.Text.RegularExpressions;

namespace Platform.Web.Services.Tools;

public record SymbolMatch(string FilePath, int LineNumber, string LineText);

/// <summary>
/// Real, working symbol search over workspace source files using regex
/// matching against common C# declaration patterns (class/interface/method/
/// property). This is a textual search, NOT a Roslyn semantic index — it
/// will not resolve overloads, inheritance, or cross-file references.
/// A true semantic index (§12) is a separate, larger piece of work; this
/// tool is honestly scoped and labeled rather than presented as more than
/// it is.
/// </summary>
public class SearchSymbolTool
{
    private readonly string _workspaceRoot;
    private static readonly string[] SourceExtensions = { ".cs" };

    private static readonly Regex DeclarationPattern = new(
        @"\b(class|interface|struct|record|enum)\s+(\w+)|" +
        @"\b(public|private|protected|internal)\s+[\w<>\[\],\s]+\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    public string Name => "SearchSymbolTool";

    public SearchSymbolTool(IConfiguration config)
    {
        _workspaceRoot = Path.GetFullPath(
            config["Workspace:Root"] ?? Path.Combine(AppContext.BaseDirectory, "workspace"));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public async Task<List<SymbolMatch>> FindAsync(string symbolName, CancellationToken ct = default)
    {
        var matches = new List<SymbolMatch>();
        if (string.IsNullOrWhiteSpace(symbolName)) return matches;

        var files = Directory.EnumerateFiles(_workspaceRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => SourceExtensions.Contains(Path.GetExtension(f))
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var lines = await File.ReadAllLinesAsync(file, ct);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!DeclarationPattern.IsMatch(lines[i])) continue;
                if (!lines[i].Contains(symbolName, StringComparison.OrdinalIgnoreCase)) continue;

                matches.Add(new SymbolMatch(
                    Path.GetRelativePath(_workspaceRoot, file),
                    i + 1,
                    lines[i].Trim()));
            }
        }

        return matches;
    }
}

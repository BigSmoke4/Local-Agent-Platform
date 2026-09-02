using System.Text.RegularExpressions;

namespace Platform.Web.Services.CodeIntelligence;

public record BuildDiagnostic(string FilePath, int Line, int Column, string Severity, string Code, string Message);

/// <summary>
/// Parses the real diagnostic lines that `dotnet build` / csc emit, e.g.:
/// "src/Web/Foo.cs(12,9): error CS0103: The name 'x' does not exist ..."
/// This is a genuine format contract of the .NET compiler, not a guess —
/// used to let the agent locate the actual failing file instead of
/// requiring the caller to name it.
/// </summary>
public static class BuildDiagnosticParser
{
    private static readonly Regex DiagnosticRegex = new(
        @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning)\s+(?<code>\w+\d+):\s*(?<message>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static List<BuildDiagnostic> Parse(string buildOutput)
    {
        var results = new List<BuildDiagnostic>();
        if (string.IsNullOrWhiteSpace(buildOutput)) return results;

        foreach (Match match in DiagnosticRegex.Matches(buildOutput))
        {
            results.Add(new BuildDiagnostic(
                FilePath: match.Groups["file"].Value.Trim(),
                Line: int.Parse(match.Groups["line"].Value),
                Column: int.Parse(match.Groups["col"].Value),
                Severity: match.Groups["severity"].Value,
                Code: match.Groups["code"].Value,
                Message: match.Groups["message"].Value.Trim()));
        }

        return results;
    }

    /// <summary>Returns the file path of the first real error diagnostic, if any.</summary>
    public static string? FindFirstErrorFile(string buildOutput)
        => Parse(buildOutput).FirstOrDefault(d => d.Severity == "error")?.FilePath;

    /// <summary>
    /// Returns the distinct set of files with real error diagnostics, in the
    /// order they first appear — used for multi-file repair so the agent
    /// doesn't stop after fixing (or failing to fix) just one file.
    /// </summary>
    public static List<string> FindAllErrorFiles(string buildOutput)
        => Parse(buildOutput)
            .Where(d => d.Severity == "error")
            .Select(d => d.FilePath)
            .Distinct()
            .ToList();
}

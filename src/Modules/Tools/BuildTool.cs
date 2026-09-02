using System.Text.RegularExpressions;

namespace Platform.Web.Services.Tools;

public record BuildResult(bool Succeeded, int ErrorCount, int WarningCount, string RawOutput);

/// <summary>
/// Runs the real `dotnet build` process and parses its actual output.
/// Never reports Succeeded=true unless the process exit code was 0 and
/// no compiler errors were found in the output — per the platform rule
/// against fabricating "Build: PASS".
/// </summary>
public class BuildTool
{
    private readonly TerminalTool _terminal;
    private static readonly Regex ErrorRegex = new(@"(\d+) Error\(s\)", RegexOptions.IgnoreCase);
    private static readonly Regex WarningRegex = new(@"(\d+) Warning\(s\)", RegexOptions.IgnoreCase);

    public string Name => "BuildTool";

    public BuildTool(TerminalTool terminal)
    {
        _terminal = terminal;
    }

    public async Task<BuildResult> RunAsync(string? projectOrSolutionPath = null, CancellationToken ct = default)
    {
        var command = projectOrSolutionPath is null
            ? "dotnet build"
            : $"dotnet build {projectOrSolutionPath}";

        var result = await _terminal.ExecuteAsync(command, preApproved: true, ct);
        var combined = result.StdOut + result.StdErr;

        var errorMatch = ErrorRegex.Match(combined);
        var warningMatch = WarningRegex.Match(combined);

        var errorCount = errorMatch.Success ? int.Parse(errorMatch.Groups[1].Value) : (result.ExitCode != 0 ? 1 : 0);
        var warningCount = warningMatch.Success ? int.Parse(warningMatch.Groups[1].Value) : 0;

        var succeeded = result.ExitCode == 0 && errorCount == 0;

        return new BuildResult(succeeded, errorCount, warningCount, combined);
    }
}

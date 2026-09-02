using System.Text.RegularExpressions;

namespace Platform.Web.Services.Tools;

public record TestResult(bool Succeeded, int Passed, int Failed, int Skipped, string RawOutput);

/// <summary>
/// Runs the real `dotnet test` process and parses its actual summary line.
/// Never reports a pass count that wasn't actually observed in output.
/// </summary>
public class TestTool
{
    private readonly TerminalTool _terminal;

    // Matches dotnet test's summary line, e.g.:
    // "Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3"
    private static readonly Regex SummaryRegex = new(
        @"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)",
        RegexOptions.IgnoreCase);

    public string Name => "TestTool";

    public TestTool(TerminalTool terminal)
    {
        _terminal = terminal;
    }

    public async Task<TestResult> RunAsync(string? projectPath = null, CancellationToken ct = default)
    {
        var command = projectPath is null
            ? "dotnet test"
            : $"dotnet test {projectPath}";

        var result = await _terminal.ExecuteAsync(command, preApproved: true, ct);
        var combined = result.StdOut + result.StdErr;

        var match = SummaryRegex.Match(combined);
        if (!match.Success)
        {
            // No summary line found — cannot claim a pass count. Report failure honestly.
            return new TestResult(false, 0, 0, 0, combined);
        }

        var failed = int.Parse(match.Groups[1].Value);
        var passed = int.Parse(match.Groups[2].Value);
        var skipped = int.Parse(match.Groups[3].Value);

        return new TestResult(result.ExitCode == 0 && failed == 0, passed, failed, skipped, combined);
    }
}

using System.Text.RegularExpressions;

namespace LocalAgentPlatform.Modules.Verification.Domain;

public sealed record BuildParseResult(int ErrorCount, int WarningCount);

/// <summary>
/// Counts real "error CS####"/"warning CS####" occurrences in actual `dotnet build`
/// console output. This is intentionally a count of the compiler's own diagnostics —
/// not a separate static analyzer — so it can never disagree with the build's real
/// exit code. Pure string parsing, no I/O, unit-testable.
/// </summary>
public static class BuildOutputParser
{
    private static readonly Regex ErrorPattern = new(@"error\s+[A-Z]+\d+", RegexOptions.Compiled);
    private static readonly Regex WarningPattern = new(@"warning\s+[A-Z]+\d+", RegexOptions.Compiled);

    public static BuildParseResult Parse(string buildOutput)
    {
        if (string.IsNullOrEmpty(buildOutput)) return new BuildParseResult(0, 0);
        var errors = ErrorPattern.Matches(buildOutput).Count;
        var warnings = WarningPattern.Matches(buildOutput).Count;
        return new BuildParseResult(errors, warnings);
    }
}

public sealed record TestParseResult(bool Recognized, int? Total, int? Passed, int? Failed, int? Skipped);

/// <summary>
/// Parses the real summary line `dotnet test` prints, e.g.
/// "Passed!  - Failed: 0, Passed: 12, Skipped: 1, Total: 13" or "Failed! - Failed: 2, ...".
/// If the format isn't recognized (different SDK/test framework versions vary), this
/// returns Recognized=false rather than guessing zeros — an unrecognized result must
/// never be silently reported as "0 failures".
/// </summary>
public static class TestOutputParser
{
    private static readonly Regex SummaryPattern = new(
        @"Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)",
        RegexOptions.Compiled);

    public static TestParseResult Parse(string testOutput)
    {
        if (string.IsNullOrEmpty(testOutput)) return new TestParseResult(false, null, null, null, null);

        var match = SummaryPattern.Match(testOutput);
        if (!match.Success) return new TestParseResult(false, null, null, null, null);

        var failed = int.Parse(match.Groups[1].Value);
        var passed = int.Parse(match.Groups[2].Value);
        var skipped = int.Parse(match.Groups[3].Value);
        var total = int.Parse(match.Groups[4].Value);

        return new TestParseResult(true, total, passed, failed, skipped);
    }
}

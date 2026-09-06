using LocalAgentPlatform.Modules.Verification.Domain;
using Xunit;

namespace LocalAgentPlatform.Domain.Tests;

public class BuildOutputParserTests
{
    [Fact]
    public void Counts_real_error_and_warning_codes()
    {
        var output = """
            Program.cs(10,5): error CS0103: The name 'foo' does not exist
            Program.cs(12,5): warning CS0168: variable declared but never used
            Program.cs(20,5): warning CS0219: variable assigned but never used
            Build FAILED.
            """;

        var result = BuildOutputParser.Parse(output);
        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(2, result.WarningCount);
    }

    [Fact]
    public void Empty_output_yields_zero_zero_not_a_guess()
    {
        var result = BuildOutputParser.Parse("");
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(0, result.WarningCount);
    }

    [Fact]
    public void Clean_build_output_has_no_errors_or_warnings()
    {
        var result = BuildOutputParser.Parse("Build succeeded.\n0 Warning(s)\n0 Error(s)");
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(0, result.WarningCount);
    }
}

public class TestOutputParserTests
{
    [Fact]
    public void Parses_real_dotnet_test_summary_line()
    {
        var output = "Passed!  - Failed: 0, Passed: 12, Skipped: 1, Total: 13, Duration: 2 s";
        var result = TestOutputParser.Parse(output);

        Assert.True(result.Recognized);
        Assert.Equal(0, result.Failed);
        Assert.Equal(12, result.Passed);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(13, result.Total);
    }

    [Fact]
    public void Failed_run_is_parsed_correctly_not_defaulted_to_success()
    {
        var output = "Failed!  - Failed: 2, Passed: 10, Skipped: 0, Total: 12, Duration: 1 s";
        var result = TestOutputParser.Parse(output);

        Assert.True(result.Recognized);
        Assert.Equal(2, result.Failed);
    }

    [Fact]
    public void Unrecognized_format_never_reports_zero_failures_by_default()
    {
        var result = TestOutputParser.Parse("some completely unrelated output with no summary line");
        Assert.False(result.Recognized);
        Assert.Null(result.Failed);
    }
}

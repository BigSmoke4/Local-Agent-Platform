using Platform.Web.Services.Tools;

namespace Platform.Web.Services.Verification;

public record VerificationOutcome(
    bool BuildSucceeded,
    int BuildErrors,
    bool TestsSucceeded,
    int TestsPassed,
    int TestsFailed,
    string Summary,
    string RawBuildOutput);

/// <summary>
/// Orchestrates the real build -> test pipeline. Never claims success unless
/// BuildTool/TestTool actually reported it. This operates on whatever is
/// currently in the workspace — there is no FileWriteTool/FileEditTool yet
/// in this codebase, so this verifies the existing state rather than a
/// fresh agent-authored change. That gap is called out in README.md.
/// </summary>
public class VerificationEngine
{
    private readonly BuildTool _build;
    private readonly TestTool _test;
    private readonly ILogger<VerificationEngine> _logger;

    public VerificationEngine(BuildTool build, TestTool test, ILogger<VerificationEngine> logger)
    {
        _build = build;
        _test = test;
        _logger = logger;
    }

    public async Task<VerificationOutcome> RunAsync(string? projectPath, CancellationToken ct = default)
    {
        var buildResult = await _build.RunAsync(projectPath, ct);
        _logger.LogInformation("Verification build: succeeded={Succeeded} errors={Errors}",
            buildResult.Succeeded, buildResult.ErrorCount);

        if (!buildResult.Succeeded)
        {
            return new VerificationOutcome(
                BuildSucceeded: false,
                BuildErrors: buildResult.ErrorCount,
                TestsSucceeded: false,
                TestsPassed: 0,
                TestsFailed: 0,
                Summary: $"Build failed with {buildResult.ErrorCount} error(s). Tests were not run.",
                RawBuildOutput: buildResult.RawOutput);
        }

        var testResult = await _test.RunAsync(projectPath, ct);
        _logger.LogInformation("Verification tests: succeeded={Succeeded} passed={Passed} failed={Failed}",
            testResult.Succeeded, testResult.Passed, testResult.Failed);

        var summary = testResult.Succeeded
            ? $"Build: PASS. Tests: {testResult.Passed}/{testResult.Passed + testResult.Failed}."
            : $"Build: PASS. Tests: {testResult.Passed}/{testResult.Passed + testResult.Failed} ({testResult.Failed} failed).";

        return new VerificationOutcome(
            BuildSucceeded: true,
            BuildErrors: 0,
            TestsSucceeded: testResult.Succeeded,
            TestsPassed: testResult.Passed,
            TestsFailed: testResult.Failed,
            Summary: summary,
            RawBuildOutput: buildResult.RawOutput);
    }
}

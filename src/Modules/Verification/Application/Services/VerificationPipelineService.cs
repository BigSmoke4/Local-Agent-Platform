using System.Text.Json;
using LocalAgentPlatform.Modules.Tools.Application.Services;
using LocalAgentPlatform.Modules.Verification.Domain;
using LocalAgentPlatform.Modules.Verification.Infrastructure.Security;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Modules.Verification.Application.Services;

/// <summary>
/// Real verification pipeline: build -> test -> pattern-based security scan -> persist
/// a VerificationRun row. Every field on the result comes from an actual tool
/// invocation or file scan — this service never invents a pass/fail. The reviewer's
/// advisory opinion (see <see cref="ReviewerService"/>) is layered on top separately and
/// never substitutes for these real checks (spec Section 65).
/// </summary>
public sealed class VerificationPipelineService
{
    private readonly ToolExecutionService _toolExecutionService;
    private readonly ISecurityPatternScanner _securityScanner;
    private readonly PlatformDbContext _db;

    private static readonly string[] ScannableExtensions = { ".cs", ".json", ".config" };

    public VerificationPipelineService(
        ToolExecutionService toolExecutionService, ISecurityPatternScanner securityScanner, PlatformDbContext db)
    {
        _toolExecutionService = toolExecutionService;
        _securityScanner = securityScanner;
        _db = db;
    }

    public async Task<VerificationRun> RunAsync(Guid sessionId, Guid repositoryId, int repairAttemptNumber, bool runTests, CancellationToken ct = default)
    {
        var run = new VerificationRun { AgentSessionId = sessionId, RepairAttemptNumber = repairAttemptNumber };

        // ---- Build (real dotnet build via BuildTool) ----
        var buildOutcome = await _toolExecutionService.InvokeAsync(
            "BuildTool", repositoryId, new Dictionary<string, string>(), approved: true, ct);
        var buildText = (buildOutcome.Result?.Output ?? "") + "\n" + (buildOutcome.Result?.Error ?? "");
        var buildParsed = BuildOutputParser.Parse(buildText);

        run.BuildPassed = buildOutcome.Result?.Success ?? false;
        run.CompilerErrorCount = buildParsed.ErrorCount;
        run.CompilerWarningCount = buildParsed.WarningCount;
        run.BuildOutputSummary = Truncate(run.BuildPassed == true
            ? $"Build passed with {buildParsed.WarningCount} warning(s)."
            : $"Build failed with {buildParsed.ErrorCount} error(s): {buildOutcome.Result?.Error}");

        // ---- Tests (only if the build passed — running tests against broken code is pointless) ----
        if (run.BuildPassed == true && runTests)
        {
            var testOutcome = await _toolExecutionService.InvokeAsync(
                "TestTool", repositoryId, new Dictionary<string, string>(), approved: true, ct);
            var testText = (testOutcome.Result?.Output ?? "") + "\n" + (testOutcome.Result?.Error ?? "");
            var testParsed = TestOutputParser.Parse(testText);

            run.TestsRan = true;
            if (testParsed.Recognized)
            {
                run.TestsPassed = testParsed.Failed == 0;
                run.TestsTotal = testParsed.Total;
                run.TestsFailed = testParsed.Failed;
                run.TestsSkipped = testParsed.Skipped;
                run.TestOutputSummary = $"{testParsed.Passed}/{testParsed.Total} passed, {testParsed.Failed} failed, {testParsed.Skipped} skipped.";
            }
            else
            {
                // Section 65: never claim tests passed when the result couldn't actually be parsed.
                run.TestsPassed = testOutcome.Result?.Success ?? false;
                run.TestOutputSummary = "Could not parse a standard test summary line; falling back to the test runner's raw exit code: " +
                    (run.TestsPassed == true ? "success." : "non-zero/failure.");
            }
        }
        else
        {
            run.TestsRan = false;
        }

        // ---- Security scan (real regex scan over real, currently-tracked files) ----
        var trackedFiles = await _db.FileSnapshots
            .Where(f => f.RepositoryId == repositoryId && !f.IsDeleted)
            .Select(f => f.RelativePath)
            .ToListAsync(ct);
        var scannableFiles = trackedFiles.Where(p => ScannableExtensions.Contains(Path.GetExtension(p))).ToList();

        var repo = await _db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        var findings = repo is not null
            ? await _securityScanner.ScanAsync(repo.LocalPath, scannableFiles, ct)
            : Array.Empty<SecurityFinding>();

        run.SecurityFindingCount = findings.Count;
        run.SecurityFindingsJson = JsonSerializer.Serialize(findings);

        // ---- Overall result: real build/test/security gates only — reviewer is separate ----
        var securityHasHighSeverity = findings.Any(f => f.Severity == "High");
        run.OverallResult = (run.BuildPassed == true &&
                              (run.TestsRan == false || run.TestsPassed == true) &&
                              !securityHasHighSeverity)
            ? "Passed" : "Failed";

        _db.VerificationRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        return run;
    }

    private static string? Truncate(string? s) => s is { Length: > 2000 } ? s[..2000] + "... [truncated]" : s;
}

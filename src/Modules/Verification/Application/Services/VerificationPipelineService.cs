using System.Text.Json;
using LocalAgentPlatform.Modules.Verification.Domain;
using LocalAgentPlatform.Modules.RepositoryAnalysis.Infrastructure;
using LocalAgentPlatform.Modules.Verification.Infrastructure.Security;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Data.Entities;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Shared.Kernel.Tools;
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
    private readonly ISecurityPatternScanner _securityScanner;
    private readonly PlatformDbContext _db;
    private readonly IWorkspaceRootPolicy _workspacePolicy;
    private readonly IRepositoryFileScanner _fileScanner;

    private static readonly HashSet<string> ScannableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".razor", ".cshtml", ".fs", ".fsx", ".fsproj", ".vb", ".vbproj", ".csproj",
        ".props", ".targets", ".sln", ".slnx", ".json", ".config", ".xml", ".js", ".jsx", ".ts", ".tsx",
        ".vue", ".svelte", ".py", ".php", ".rb", ".go", ".rs", ".c", ".h", ".cc", ".cpp", ".hpp",
        ".java", ".kt", ".kts", ".scala", ".swift", ".dart", ".sql", ".yml", ".yaml", ".toml", ".ini",
        ".properties", ".gradle", ".sh", ".bash", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".tf",
        ".hcl", ".html", ".htm", ".md", ".txt", ".proto", ".graphql", ".gql", ".cmake", ".mk", ".pem", ".key"
    };

    private static readonly HashSet<string> ScannableExtensionlessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".gitconfig", ".npmrc", ".pypirc", ".netrc", ".dockerignore", ".editorconfig",
        "Dockerfile", "Containerfile", "Makefile", "Jenkinsfile", "Procfile", "CMakeLists.txt",
        "WORKSPACE", "BUILD", "Justfile", "Vagrantfile", "Gemfile", "Rakefile", "Brewfile"
    };

    public VerificationPipelineService(
        ISecurityPatternScanner securityScanner,
        PlatformDbContext db,
        IWorkspaceRootPolicy workspacePolicy,
        IRepositoryFileScanner fileScanner)
    {
        _securityScanner = securityScanner;
        _db = db;
        _workspacePolicy = workspacePolicy;
        _fileScanner = fileScanner;
    }

    public async Task<VerificationRun> RunAsync(
        Guid sessionId,
        Guid repositoryId,
        int repairAttemptNumber,
        bool runTests,
        ToolExecutionResult approvedBuildResult,
        ToolExecutionResult? approvedTestResult = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approvedBuildResult);
        if (runTests && approvedBuildResult.Success && approvedTestResult is null)
            throw new InvalidOperationException("Test verification requires an approved, real TestTool result.");

        var run = new VerificationRun { AgentSessionId = sessionId, RepairAttemptNumber = repairAttemptNumber };

        // Build/test are user-approved tool executions. The pipeline consumes their
        // real results; it never launches repository-controlled code behind the user's back.
        var buildText = (approvedBuildResult.Output ?? "") + "\n" + (approvedBuildResult.Error ?? "");
        var buildParsed = BuildOutputParser.Parse(buildText);
        run.BuildPassed = approvedBuildResult.Success;
        run.CompilerErrorCount = buildParsed.ErrorCount;
        run.CompilerWarningCount = buildParsed.WarningCount;
        run.BuildOutputSummary = Truncate(run.BuildPassed == true
            ? $"Build passed with {buildParsed.WarningCount} warning(s)."
            : $"Build failed with {buildParsed.ErrorCount} error(s): {approvedBuildResult.Error}");

        if (run.BuildPassed == true && runTests)
        {
            var testText = (approvedTestResult!.Output ?? "") + "\n" + (approvedTestResult.Error ?? "");
            var testParsed = TestOutputParser.Parse(testText);
            run.TestsRan = true;
            if (testParsed.Recognized)
            {
                run.TestsPassed = TestOutputParser.IndicatesVerifiedPass(testParsed, approvedTestResult!.Success);
                run.TestsTotal = testParsed.Total;
                run.TestsFailed = testParsed.Failed;
                run.TestsSkipped = testParsed.Skipped;
                run.TestOutputSummary = $"{testParsed.Passed}/{testParsed.Total} passed, {testParsed.Failed} failed, {testParsed.Skipped} skipped; process exit code {approvedTestResult.ExitCode?.ToString() ?? (approvedTestResult.Success ? "0" : "non-zero")}.";
            }
            else
            {
                // A zero process exit code without a parseable test summary does not prove
                // that any tests ran; never turn missing evidence into a pass.
                run.TestsPassed = false;
                run.TestOutputSummary = $"Could not verify a standard test summary; process exit code was {approvedTestResult!.ExitCode?.ToString() ?? (approvedTestResult.Success ? "0" : "non-zero")}. Tests are not considered passed.";
            }
        }
        else
        {
            run.TestsRan = false;
        }

        // ---- Security scan over a fresh workspace traversal, including unindexed/changed files ----
        var repo = await _db.Repositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct)
            ?? throw new InvalidOperationException("Repository not found; security verification cannot run.");
        if (!_workspacePolicy.IsAllowed(repo.LocalPath))
            throw new InvalidOperationException("Repository is outside the configured workspace roots; security verification cannot run.");
        var scannableFiles = new List<string>();
        foreach (var relativePath in _fileScanner.EnumeratePaths(repo.LocalPath))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(relativePath);
            if (!ScannableExtensions.Contains(Path.GetExtension(relativePath)) &&
                !ScannableExtensionlessNames.Contains(fileName) &&
                !fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)) continue;
            scannableFiles.Add(relativePath);
            if (scannableFiles.Count > 5_000)
                throw new InvalidOperationException("Security scan stopped: repository has more than 5,000 scannable files; verification is incomplete.");
        }

        if (scannableFiles.Count == 0)
            throw new InvalidOperationException("Security scan found no supported source/configuration files; verification is incomplete.");
        var findings = await _securityScanner.ScanAsync(repo.LocalPath, scannableFiles, ct);

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

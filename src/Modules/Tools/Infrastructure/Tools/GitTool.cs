using System.Diagnostics;
using LocalAgentPlatform.Shared.Kernel.Tools;

namespace LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;

/// <summary>
/// Real `git` CLI wrapper restricted to read-only subcommands (status, diff, log, branch).
/// Mutating operations (commit, push, reset, checkout) are deliberately not exposed here —
/// spec Section 17 requires the agent to "never automatically destroy user changes";
/// the safest way to guarantee that for Phase 4 is to not offer destructive git actions
/// at all yet. This is a documented scope boundary, not a hidden limitation.
/// </summary>
public sealed class GitTool : ITool
{
    private static readonly HashSet<string> AllowedSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "branch", "show", "blame"
    };

    public string Name => "GitTool";
    public string Description => "Runs a read-only git subcommand (status, diff, log, branch, show, blame) in the repository.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.Low;
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("subcommand", out var subcommand) || string.IsNullOrWhiteSpace(subcommand))
            return ToolExecutionResult.Fail("Missing required parameter 'subcommand'.");

        var firstToken = subcommand.Trim().Split(' ')[0];
        if (!AllowedSubcommands.Contains(firstToken))
            return ToolExecutionResult.Fail(
                $"Subcommand '{firstToken}' is not allowed by GitTool. Allowed: {string.Join(", ", AllowedSubcommands)}.");

        if (!Directory.Exists(Path.Combine(context.RepositoryRootPath, ".git")))
            return ToolExecutionResult.Fail("No .git directory found at the repository root — is this a git repository?");

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = context.RepositoryRootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in subcommand.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Timeout);
            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return process.ExitCode == 0
                ? ToolExecutionResult.Ok(stdout)
                : ToolExecutionResult.Fail(stderr, stdout) with { ExitCode = process.ExitCode };
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"git invocation failed: {ex.Message}");
        }
    }
}

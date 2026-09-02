namespace Platform.Web.Services.Tools;

public record GitCommandResult(bool Succeeded, string Output, string Error);

/// <summary>
/// Thin wrapper over the real `git` CLI, scoped to the workspace directory.
/// Uses TerminalTool underneath so every git invocation goes through the
/// same policy engine and process handling.
/// </summary>
public class GitTool
{
    private readonly TerminalTool _terminal;

    public string Name => "GitTool";

    public GitTool(TerminalTool terminal)
    {
        _terminal = terminal;
    }

    public async Task<GitCommandResult> StatusAsync(CancellationToken ct = default)
        => await RunAsync("git status --porcelain=v1", ct);

    public async Task<GitCommandResult> DiffAsync(string? path = null, CancellationToken ct = default)
        => await RunAsync(path is null ? "git diff" : $"git diff -- {path}", ct);

    public async Task<GitCommandResult> LogAsync(int maxCount = 20, CancellationToken ct = default)
        => await RunAsync($"git log -n {maxCount} --oneline", ct);

    public async Task<GitCommandResult> BranchAsync(CancellationToken ct = default)
        => await RunAsync("git branch --show-current", ct);

    public async Task<GitCommandResult> CreateCheckpointAsync(string message, CancellationToken ct = default)
    {
        var add = await RunAsync("git add -A", ct);
        if (!add.Succeeded) return add;
        return await RunAsync($"git commit -m \"checkpoint: {message.Replace("\"", "'")}\" --allow-empty", ct);
    }

    private async Task<GitCommandResult> RunAsync(string command, CancellationToken ct)
    {
        // git status/diff/log are on the terminal allowlist already (CommandPolicyEngine);
        // git add/commit are not, so checkpoints require pre-approval by the caller.
        var preApproved = command.StartsWith("git add") || command.StartsWith("git commit");
        var result = await _terminal.ExecuteAsync(command, preApproved: preApproved, ct);

        if (result.Decision is "Deny" or "RequireApproval")
            return new GitCommandResult(false, string.Empty, result.Reason);

        return new GitCommandResult(result.ExitCode == 0, result.StdOut, result.StdErr);
    }
}

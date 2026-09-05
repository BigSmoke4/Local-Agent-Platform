using System.Diagnostics;
using System.Text;
using LocalAgentPlatform.Modules.Tools.Domain;
using LocalAgentPlatform.Shared.Kernel.Tools;
using Microsoft.Extensions.Logging;

namespace LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;

/// <summary>
/// Executes a shell command via a real child process, restricted to the repository's
/// workspace root as the working directory. Every invocation is checked against
/// <see cref="CommandPolicyEngine"/> by the Application-layer ToolExecutionService
/// *before* this tool is ever called — this class assumes it has already been cleared
/// (Allow) or explicitly approved, and re-validates defensively regardless.
/// </summary>
public sealed class TerminalTool : ITool
{
    private readonly ILogger<TerminalTool> _logger;

    public TerminalTool(ILogger<TerminalTool> logger) => _logger = logger;

    public string Name => "TerminalTool";
    public string Description => "Runs a shell command inside the repository workspace, subject to the command policy engine.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.High;
    public TimeSpan Timeout => TimeSpan.FromMinutes(2);

    public async Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command))
            return ToolExecutionResult.Fail("Missing required parameter 'command'.");

        // Defensive re-check even though the Application layer should have already gated this.
        var policy = CommandPolicyEngine.Evaluate(command);
        if (policy.Decision != CommandDecision.Allow)
            return ToolExecutionResult.Fail($"TerminalTool refused to run an unapproved command: {policy.Reason}");

        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            ArgumentList = { isWindows ? "/c" : "-c", command },
            WorkingDirectory = context.RepositoryRootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(timeoutCts.Token);

            var success = process.ExitCode == 0;
            var redactedOut = RedactSecrets(stdout.ToString());
            var redactedErr = RedactSecrets(stderr.ToString());

            return success
                ? ToolExecutionResult.Ok(redactedOut)
                : ToolExecutionResult.Fail(redactedErr, redactedOut) with { ExitCode = process.ExitCode };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            TryKill(process);
            return ToolExecutionResult.Fail($"Command timed out after {Timeout.TotalSeconds:0}s and was terminated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TerminalTool failed to execute command.");
            return ToolExecutionResult.Fail($"Failed to execute command: {ex.Message}");
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }

    /// <summary>Basic secret redaction on tool output (Section 38: "redact API keys,
    /// passwords, tokens, connection strings, private keys"). Pattern-based, not exhaustive.</summary>
    private static string RedactSecrets(string text)
    {
        var patterns = new (string Pattern, string Replacement)[]
        {
            (@"(?i)(api[_-]?key\s*[:=]\s*)[\w\-]{8,}", "$1[REDACTED]"),
            (@"(?i)(password\s*[:=]\s*)\S+", "$1[REDACTED]"),
            (@"(?i)(secret\s*[:=]\s*)\S+", "$1[REDACTED]"),
            (@"(?i)(Authorization:\s*Bearer\s+)[\w\-\.]+", "$1[REDACTED]"),
            (@"postgres(ql)?://[^:]+:[^@]+@", "postgresql://[REDACTED]@")
        };
        foreach (var (pattern, replacement) in patterns)
            text = System.Text.RegularExpressions.Regex.Replace(text, pattern, replacement);
        return text;
    }
}

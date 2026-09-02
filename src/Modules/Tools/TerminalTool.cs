using System.Diagnostics;

namespace Platform.Web.Services.Tools;

public record TerminalResult(bool Executed, int? ExitCode, string StdOut, string StdErr, string Decision, string Reason);

/// <summary>
/// Executes shell commands only after CommandPolicyEngine evaluation.
/// Denied commands never run. Commands requiring approval are not run here —
/// callers must obtain approval first (see AgentController approval flow).
/// </summary>
public class TerminalTool
{
    private readonly CommandPolicyEngine _policy;
    private readonly string _workingDirectory;
    private readonly ILogger<TerminalTool> _logger;

    public string Name => "TerminalTool";

    public TerminalTool(CommandPolicyEngine policy, IConfiguration config, ILogger<TerminalTool> logger)
    {
        _policy = policy;
        _workingDirectory = Path.GetFullPath(
            config["Workspace:Root"] ?? Path.Combine(AppContext.BaseDirectory, "workspace"));
        Directory.CreateDirectory(_workingDirectory);
        _logger = logger;
    }

    public async Task<TerminalResult> ExecuteAsync(string command, bool preApproved, CancellationToken ct = default)
    {
        var decision = _policy.Evaluate(command);

        if (decision.Decision == CommandDecision.Deny)
        {
            _logger.LogWarning("Denied command: {Command} ({Reason})", command, decision.Reason);
            return new TerminalResult(false, null, string.Empty, string.Empty, "Deny", decision.Reason);
        }

        if (decision.Decision == CommandDecision.RequireApproval && !preApproved)
        {
            return new TerminalResult(false, null, string.Empty, string.Empty, "RequireApproval", decision.Reason);
        }

        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/bash",
            Arguments = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        var stdOut = new System.Text.StringBuilder();
        var stdErr = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdOut.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stdErr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeout = TimeSpan.FromMinutes(5);
        var completed = await WaitForExitAsync(process, timeout, ct);

        if (!completed)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            _logger.LogWarning("Command timed out after {Timeout}: {Command}", timeout, command);
            return new TerminalResult(true, null, stdOut.ToString(), stdErr.ToString(), "Timeout", "Exceeded 5 minute timeout.");
        }

        _logger.LogInformation("Executed command '{Command}' exit={ExitCode}", command, process.ExitCode);
        return new TerminalResult(true, process.ExitCode, stdOut.ToString(), stdErr.ToString(), decision.Decision.ToString(), decision.Reason);
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

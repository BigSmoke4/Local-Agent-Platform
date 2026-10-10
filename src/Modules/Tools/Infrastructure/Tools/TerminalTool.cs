using System.Diagnostics;
using System.Text;
using LocalAgentPlatform.Modules.Tools.Domain;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Shared.Kernel.Security;
using LocalAgentPlatform.Shared.Kernel.Tools;
using Microsoft.Extensions.Logging;

namespace LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;

/// <summary>
/// Runs one parsed executable directly with an argument vector. No shell is spawned,
/// so separators, pipelines, redirects, substitutions, and shell expansion cannot turn
/// an approved command into additional commands. ToolExecutionService performs the
/// user/risk approval gate; this tool repeats the static deny/approval checks as defense
/// in depth for direct callers.
/// </summary>
public sealed class TerminalTool : ITool
{
    private const int MaxCapturedCharacters = 100_000;
    private readonly ILogger<TerminalTool> _logger;

    public TerminalTool(ILogger<TerminalTool> logger) => _logger = logger;

    public string Name => "TerminalTool";
    public string Description => "Runs one executable inside the repository workspace without a shell; chaining, pipes, redirection, and shell expansion are unsupported.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.High;
    public TimeSpan Timeout => TimeSpan.FromMinutes(2);

    public async Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        if (!parameters.TryGetValue("command", out var command) || string.IsNullOrWhiteSpace(command))
            return ToolExecutionResult.Fail("Missing required parameter 'command'.");
        if (!context.ApprovalGranted)
            return ToolExecutionResult.Fail("TerminalTool is high risk and requires explicit one-time human approval.");

        if (!CommandPolicyEngine.TryParseSimpleCommand(command, out var parsed, out var parseError) || parsed is null)
            return ToolExecutionResult.Fail($"TerminalTool refused the command: {parseError}");

        var policy = CommandPolicyEngine.Evaluate(command);
        if (policy.Decision == CommandDecision.Deny)
            return ToolExecutionResult.Fail($"TerminalTool refused to run the command: {policy.Reason}");
        if (policy.Decision == CommandDecision.RequireApproval && !context.ApprovalGranted)
            return ToolExecutionResult.Fail($"TerminalTool requires explicit approval: {policy.Reason}");

        if (!WorkspacePathGuard.IsSafeWorkspaceRoot(context.RepositoryRootPath))
            return ToolExecutionResult.Fail("Repository workspace must be an existing real directory without linked path components.");

        var psi = new ProcessStartInfo
        {
            FileName = parsed.Executable,
            WorkingDirectory = context.RepositoryRootPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in parsed.Arguments) psi.ArgumentList.Add(argument);
        RestrictEnvironment(psi);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;

        try
        {
            process.Start();
            stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxCapturedCharacters);
            stderrTask = ReadBoundedAsync(process.StandardError, MaxCapturedCharacters);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Timeout);
            await process.WaitForExitAsync(timeoutCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var redactedOut = SecretRedactor.Redact(stdout);
            var redactedErr = SecretRedactor.Redact(stderr);
            return process.ExitCode == 0
                ? ToolExecutionResult.Ok(redactedOut)
                : ToolExecutionResult.Fail(redactedErr, redactedOut) with { ExitCode = process.ExitCode };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            await DrainOutputAsync(stdoutTask, stderrTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            var (stdout, stderr) = await DrainOutputAsync(stdoutTask, stderrTask);
            return ToolExecutionResult.Fail(
                $"Command timed out after {Timeout.TotalSeconds:0}s and was terminated. {SecretRedactor.Redact(stderr)}",
                SecretRedactor.Redact(stdout));
        }
        catch (Exception ex)
        {
            TryKill(process);
            await DrainOutputAsync(stdoutTask, stderrTask);
            _logger.LogError(ex, "TerminalTool failed to execute {Executable}.", parsed.Executable);
            return ToolExecutionResult.Fail($"Failed to execute command: {SecretRedactor.Redact(ex.Message)}");
        }
    }

    private static void RestrictEnvironment(ProcessStartInfo psi)
    {
        // Child tools do not inherit application secrets (e.g. database credentials or
        // API keys). Keep only variables needed to resolve runtimes and temp paths.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PATH", "HOME", "USERPROFILE", "SYSTEMROOT", "WINDIR", "TEMP", "TMP",
            "DOTNET_ROOT", "DOTNET_ROOT(x86)", "NUGET_PACKAGES", "XDG_CACHE_HOME",
            "XDG_CONFIG_HOME", "XDG_DATA_HOME"
        };
        foreach (var name in psi.Environment.Keys.ToArray())
            if (!allowed.Contains(name)) psi.Environment.Remove(name);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters)
    {
        var builder = new StringBuilder(Math.Min(maxCharacters, 4096));
        var buffer = new char[4096];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
        {
            var remaining = maxCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
        }
        if (truncated) builder.Append("\n[output truncated at the capture limit]");
        return builder.ToString();
    }

    private static async Task<(string Stdout, string Stderr)> DrainOutputAsync(Task<string>? stdout, Task<string>? stderr)
    {
        try
        {
            if (stdout is null || stderr is null) return (string.Empty, string.Empty);
            await Task.WhenAll(stdout, stderr);
            return (await stdout, await stderr);
        }
        catch { return (string.Empty, string.Empty); }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

}

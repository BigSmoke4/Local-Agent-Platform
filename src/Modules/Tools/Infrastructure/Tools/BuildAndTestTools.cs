using System.Diagnostics;
using System.Text;
using LocalAgentPlatform.Modules.Tools.Domain;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Shared.Kernel.Security;
using LocalAgentPlatform.Shared.Kernel.Tools;

namespace LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;

/// <summary>Runs `dotnet build` for real. A repository build can execute MSBuild targets,
/// analyzers, and generators, so it is a high-risk operation and requires explicit
/// approval through ToolExecutionService.</summary>
public sealed class BuildTool : ITool
{
    public string Name => "BuildTool";
    public string Description => "Runs 'dotnet build' against a workspace-local solution or project. Repository build targets can execute code, so approval is required.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.High;
    public TimeSpan Timeout => TimeSpan.FromMinutes(5);

    public Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        var requested = parameters.TryGetValue("target", out var target) && !string.IsNullOrWhiteSpace(target) ? target : ".";
        if (!TryResolveTarget(requested, context.RepositoryRootPath, out var safeTarget, out var error))
            return Task.FromResult(ToolExecutionResult.Fail(error));
        return DotnetProcessRunner.RunAsync("build", new[] { safeTarget, "--nologo" }, context.RepositoryRootPath, Timeout, ct);
    }

    internal static bool TryResolveTarget(string target, string workspaceRoot, out string safeTarget, out string error)
    {
        safeTarget = string.Empty;
        error = "Target must be an existing directory or a .sln/.slnx/.csproj/.fsproj/.vbproj file inside the workspace.";
        if (string.IsNullOrWhiteSpace(target) || target.StartsWith("-", StringComparison.Ordinal)) return false;
        if (!CommandPolicyEngine.IsWithinWorkspace(workspaceRoot, target)) return false;

        try
        {
            var fullPath = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(workspaceRoot, target));
            if (File.Exists(fullPath))
            {
                if (!WorkspacePathGuard.IsRegularFile(fullPath)) return false;
                var extension = Path.GetExtension(fullPath);
                if (!new[] { ".sln", ".slnx", ".csproj", ".fsproj", ".vbproj" }
                    .Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;
            }
            else if (!Directory.Exists(fullPath))
            {
                return false;
            }

            safeTarget = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), fullPath);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>Runs `dotnet test` for real. Test projects execute arbitrary user test code,
/// so running them is high risk and requires explicit approval.</summary>
public sealed class TestTool : ITool
{
    public string Name => "TestTool";
    public string Description => "Runs 'dotnet test' against a workspace-local solution or project. Tests execute repository code, so approval is required.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.High;
    public TimeSpan Timeout => TimeSpan.FromMinutes(10);

    public Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        var requested = parameters.TryGetValue("target", out var target) && !string.IsNullOrWhiteSpace(target) ? target : ".";
        if (!BuildTool.TryResolveTarget(requested, context.RepositoryRootPath, out var safeTarget, out var error))
            return Task.FromResult(ToolExecutionResult.Fail(error));
        return DotnetProcessRunner.RunAsync("test", new[] { safeTarget, "--nologo" }, context.RepositoryRootPath, Timeout, ct);
    }
}

internal static class DotnetProcessRunner
{
    private const int MaxCapturedCharacters = 100_000;

    public static async Task<ToolExecutionResult> RunAsync(
        string verb, IEnumerable<string> args, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(verb);
        foreach (var argument in args) psi.ArgumentList.Add(argument);
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
            timeoutCts.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutCts.Token);
            var stdout = SecretRedactor.Redact(await stdoutTask);
            var stderr = SecretRedactor.Redact(await stderrTask);
            return process.ExitCode == 0
                ? ToolExecutionResult.Ok(stdout)
                : ToolExecutionResult.Fail(stderr.Length > 0 ? stderr : "Non-zero exit code.", stdout) with { ExitCode = process.ExitCode };
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
                $"'dotnet {verb}' timed out after {timeout.TotalSeconds:0}s and was terminated. {SecretRedactor.Redact(stderr)}",
                SecretRedactor.Redact(stdout));
        }
        catch (Exception ex)
        {
            TryKill(process);
            await DrainOutputAsync(stdoutTask, stderrTask);
            return ToolExecutionResult.Fail($"Failed to run 'dotnet {verb}': {SecretRedactor.Redact(ex.Message)}");
        }
    }

    private static void RestrictEnvironment(ProcessStartInfo psi)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PATH", "HOME", "USERPROFILE", "SYSTEMROOT", "WINDIR", "TEMP", "TMP",
            "DOTNET_ROOT", "DOTNET_ROOT(x86)", "DOTNET_CLI_HOME", "DOTNET_CLI_TELEMETRY_OPTOUT",
            "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "NUGET_PACKAGES",
            "XDG_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME"
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

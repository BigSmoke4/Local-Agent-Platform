using System.Diagnostics;
using LocalAgentPlatform.Shared.Kernel.Tools;

namespace LocalAgentPlatform.Modules.Tools.Infrastructure.Tools;

/// <summary>Runs `dotnet build` for real. Success/failure comes directly from the
/// compiler's own exit code — never assumed or hard-coded (spec Section 65).</summary>
public sealed class BuildTool : ITool
{
    public string Name => "BuildTool";
    public string Description => "Runs 'dotnet build' against the repository (or a specified project/solution file) and reports the real result.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.Medium;
    public TimeSpan Timeout => TimeSpan.FromMinutes(5);

    public Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        var target = parameters.TryGetValue("target", out var t) && !string.IsNullOrWhiteSpace(t) ? t : ".";
        return DotnetProcessRunner.RunAsync("build", new[] { target, "--nologo" }, context.RepositoryRootPath, Timeout, ct);
    }
}

/// <summary>Runs `dotnet test` for real. Pass/fail counts come from the test runner's
/// actual output/exit code — the agent must never claim tests passed without this.</summary>
public sealed class TestTool : ITool
{
    public string Name => "TestTool";
    public string Description => "Runs 'dotnet test' against the repository (or a specified project) and reports the real pass/fail result.";
    public ToolRiskLevel RiskLevel => ToolRiskLevel.Medium;
    public TimeSpan Timeout => TimeSpan.FromMinutes(10);

    public Task<ToolExecutionResult> ExecuteAsync(
        IReadOnlyDictionary<string, string> parameters, ToolExecutionContext context, CancellationToken ct = default)
    {
        var target = parameters.TryGetValue("target", out var t) && !string.IsNullOrWhiteSpace(t) ? t : ".";
        return DotnetProcessRunner.RunAsync("test", new[] { target, "--nologo" }, context.RepositoryRootPath, Timeout, ct);
    }
}

internal static class DotnetProcessRunner
{
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
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return process.ExitCode == 0
                ? ToolExecutionResult.Ok(stdout)
                : ToolExecutionResult.Fail(stderr.Length > 0 ? stderr : "Non-zero exit code.", stdout) with { ExitCode = process.ExitCode };
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return ToolExecutionResult.Fail($"'dotnet {verb}' timed out after {timeout.TotalSeconds:0}s and was terminated.");
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail($"Failed to run 'dotnet {verb}': {ex.Message}");
        }
    }
}

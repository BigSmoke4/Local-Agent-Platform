using System.Diagnostics;
using LocalAgentPlatform.Modules.IdeIntegration.Domain;

namespace LocalAgentPlatform.Web.Ide;

/// <summary>Concrete VS Code CLI adapter. Paths are passed as discrete process
/// arguments, never through a shell.</summary>
public sealed class VsCodeIdeIntegrationProvider : IIdeIntegrationProvider
{
    public string IdeName => "VS Code";

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "code",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--version");
            RestrictEnvironment(startInfo);

            using var process = Process.Start(startInfo);
            if (process is null) return false;
            var stdoutDrain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
            var stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                await Task.WhenAll(stdoutDrain, stderrDrain);
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await DrainAsync(stdoutDrain, stderrDrain);
                if (ct.IsCancellationRequested) throw;
                return false;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    public async Task OpenFileAsync(string filePath, int? line = null, int? column = null, CancellationToken ct = default)
    {
        var target = line is not null
            ? $"{filePath}:{Math.Max(1, line.Value)}:{Math.Max(1, column ?? 1)}"
            : filePath;
        var startInfo = new ProcessStartInfo
        {
            FileName = "code",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--reuse-window");
        startInfo.ArgumentList.Add("--goto");
        startInfo.ArgumentList.Add(target);
        RestrictEnvironment(startInfo);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the VS Code command-line adapter.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("VS Code command-line adapter did not finish within 10 seconds.");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException($"VS Code exited with code {process.ExitCode}.");
    }

    private static void RestrictEnvironment(ProcessStartInfo startInfo)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PATH", "HOME", "USERPROFILE", "SYSTEMROOT", "WINDIR", "TEMP", "TMP"
        };
        foreach (var name in startInfo.Environment.Keys.ToArray())
            if (!allowed.Contains(name)) startInfo.Environment.Remove(name);
    }

    private static async Task DrainAsync(Task stdout, Task stderr)
    {
        try { await Task.WhenAll(stdout, stderr); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

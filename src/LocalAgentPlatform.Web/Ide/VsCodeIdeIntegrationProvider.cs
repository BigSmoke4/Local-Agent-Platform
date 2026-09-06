using System.Diagnostics;
using LocalAgentPlatform.Modules.IdeIntegration.Domain;

namespace LocalAgentPlatform.Web.Ide;

public sealed class VsCodeIdeIntegrationProvider : IIdeIntegrationProvider
{
    public string IdeName => "VS Code";

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "code",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return false;
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    public async Task OpenFileAsync(string filePath, int? line = null, int? column = null, CancellationToken ct = default)
    {
        var target = filePath;
        if (line is not null) target += $":{Math.Max(1, line.Value)}:{Math.Max(1, column ?? 1)}";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "code",
            Arguments = $"--reuse-window --goto \"{target.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the VS Code command-line adapter.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException($"VS Code exited with code {process.ExitCode}.");
    }
}

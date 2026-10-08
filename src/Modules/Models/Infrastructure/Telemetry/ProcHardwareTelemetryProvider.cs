using System.Diagnostics;
using LocalAgentPlatform.Shared.Kernel.Telemetry;
using Microsoft.Extensions.Logging;

namespace LocalAgentPlatform.Modules.Models.Infrastructure.Telemetry;

/// <summary>
/// Reads real CPU/RAM figures from /proc and queries optional NVIDIA/AMD vendor tools
/// when installed. GPU/temperature/power remain null ("Unavailable" in the UI) when a
/// metric cannot be read; the provider never fabricates values (rule #7 / #65).
/// </summary>
public sealed class ProcHardwareTelemetryProvider : IHardwareTelemetryProvider
{
    private readonly ILogger<ProcHardwareTelemetryProvider> _logger;
    private (long idle, long total)? _lastCpuSample;

    public ProcHardwareTelemetryProvider(ILogger<ProcHardwareTelemetryProvider> logger)
    {
        _logger = logger;
    }

    public async Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        double? cpuPercent = null;
        long? ramUsed = null, ramTotal = null;

        try
        {
            cpuPercent = await ReadCpuUtilizationAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CPU telemetry unavailable on this host.");
        }

        try
        {
            (ramUsed, ramTotal) = await ReadMemoryAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Memory telemetry unavailable on this host.");
        }

        long? processMemory = null;
        try
        {
            processMemory = Process.GetCurrentProcess().WorkingSet64;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Process memory telemetry unavailable.");
        }

        var gpu = await ReadGpuAsync(ct);
        long? diskUsed = null, diskTotal = null;
        try
        {
            var root = Path.GetPathRoot(Environment.CurrentDirectory);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                diskTotal = drive.TotalSize;
                diskUsed = drive.TotalSize - drive.AvailableFreeSpace;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Disk telemetry unavailable."); }

        return new HardwareSnapshot(
            TimestampUtc: DateTimeOffset.UtcNow,
            CpuUtilizationPercent: cpuPercent,
            RamUsedBytes: ramUsed,
            RamTotalBytes: ramTotal,
            GpuUtilizationPercent: gpu.Utilization,
            GpuVramUsedBytes: gpu.UsedBytes,
            GpuVramTotalBytes: gpu.TotalBytes,
            DiskUsedBytes: diskUsed,
            DiskTotalBytes: diskTotal,
            CurrentProcessMemoryBytes: processMemory,
            TemperatureCelsius: gpu.Temperature,
            PowerWatts: gpu.PowerWatts
        );
    }

    private async Task<(double? Utilization, long? UsedBytes, long? TotalBytes, double? Temperature, double? PowerWatts)> ReadGpuAsync(CancellationToken ct)
    {
        // NVIDIA: query real vendor tooling when installed. CSV is stable and easy to parse.
        try
        {
            var csv = await TryRunTelemetryCommandAsync("nvidia-smi", new[]
            {
                "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw",
                "--format=csv,noheader,nounits"
            }, ct);
            var line = csv?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(line))
            {
                var p = line.Split(',', StringSplitOptions.TrimEntries);
                if (p.Length >= 5)
                {
                    double? D(string x) => double.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
                    var util = D(p[0]); var usedMiB = D(p[1]); var totalMiB = D(p[2]); var temp = D(p[3]); var power = D(p[4]);
                    return (util, usedMiB is null ? null : (long?)(usedMiB.Value * 1024 * 1024), totalMiB is null ? null : (long?)(totalMiB.Value * 1024 * 1024), temp, power);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "nvidia-smi telemetry unavailable."); }

        // AMD ROCm: use rocm-smi JSON if present. Exact fields differ by version, so
        // unsupported versions simply report null rather than fabricated values.
        try
        {
            var json = await TryRunTelemetryCommandAsync("rocm-smi", new[]
            {
                "--showuse", "--showmemuse", "--showtemp", "--showpower", "--json"
            }, ct);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var gpu = doc.RootElement.EnumerateObject().FirstOrDefault().Value;
                if (gpu.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    double? FindNumber(params string[] names)
                    {
                        foreach (var prop in gpu.EnumerateObject())
                            if (names.Any(n => prop.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                            {
                                var digits = new string(prop.Value.ToString().Where(c => char.IsDigit(c) || c == '.').ToArray());
                                if (double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
                            }
                        return null;
                    }
                    return (FindNumber("GPU use"), null, null, FindNumber("Temperature"), FindNumber("Power"));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "rocm-smi telemetry unavailable."); }
        return (null, null, null, null, null);
    }

    private async Task<string?> TryRunTelemetryCommandAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        const int maxCapturedCharacters = 64 * 1024;
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            process.Start();
            stdoutTask = ReadBoundedAsync(process.StandardOutput, maxCapturedCharacters);
            stderrTask = ReadBoundedAsync(process.StandardError, maxCapturedCharacters);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(timeoutCts.Token);
            var output = await stdoutTask;
            _ = await stderrTask;
            return process.ExitCode == 0 && !output.EndsWith("[output truncated]", StringComparison.Ordinal)
                ? output
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            await DrainAsync(stdoutTask, stderrTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainAsync(stdoutTask, stderrTask);
            _logger.LogDebug("{TelemetryExecutable} did not finish within the telemetry timeout.", executable);
            return null;
        }
        catch (Exception ex)
        {
            TryKill(process);
            await DrainAsync(stdoutTask, stderrTask);
            _logger.LogDebug(ex, "Could not run telemetry executable {TelemetryExecutable}.", executable);
            return null;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters)
    {
        var builder = new System.Text.StringBuilder(Math.Min(maxCharacters, 4096));
        var buffer = new char[4096];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
        {
            var remaining = maxCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) truncated = true;
        }
        if (truncated) builder.Append("\n[output truncated]");
        return builder.ToString();
    }

    private static async Task DrainAsync(Task<string>? stdout, Task<string>? stderr)
    {
        try { if (stdout is not null && stderr is not null) await Task.WhenAll(stdout, stderr); }
        catch { /* Output is intentionally best-effort during timeout/cancellation cleanup. */ }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private async Task<double?> ReadCpuUtilizationAsync(CancellationToken ct)
    {
        if (!File.Exists("/proc/stat")) return null;

        var line = (await File.ReadAllLinesAsync("/proc/stat", ct)).FirstOrDefault(l => l.StartsWith("cpu "));
        if (line is null) return null;

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(long.Parse).ToArray();
        // user, nice, system, idle, iowait, irq, softirq, steal
        long idle = parts[3] + (parts.Length > 4 ? parts[4] : 0);
        long total = parts.Sum();

        if (_lastCpuSample is { } last)
        {
            var idleDelta = idle - last.idle;
            var totalDelta = total - last.total;
            _lastCpuSample = (idle, total);
            if (totalDelta <= 0) return null;
            return 100.0 * (1.0 - (double)idleDelta / totalDelta);
        }

        _lastCpuSample = (idle, total);
        return null; // first sample has no delta yet
    }

    private static async Task<(long? used, long? total)> ReadMemoryAsync(CancellationToken ct)
    {
        if (!File.Exists("/proc/meminfo")) return (null, null);

        var lines = await File.ReadAllLinesAsync("/proc/meminfo", ct);
        long? totalKb = null, availableKb = null;
        foreach (var l in lines)
        {
            if (l.StartsWith("MemTotal:")) totalKb = ParseKb(l);
            else if (l.StartsWith("MemAvailable:")) availableKb = ParseKb(l);
        }
        if (totalKb is null || availableKb is null) return (null, null);
        var usedBytes = (totalKb.Value - availableKb.Value) * 1024L;
        return (usedBytes, totalKb.Value * 1024L);
    }

    private static long? ParseKb(string line)
    {
        var digits = new string(line.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var v) ? v : null;
    }
}

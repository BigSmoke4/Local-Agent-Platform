using System.Diagnostics;
using LocalAgentPlatform.Shared.Kernel.Telemetry;
using Microsoft.Extensions.Logging;

namespace LocalAgentPlatform.Modules.Models.Infrastructure.Telemetry;

/// <summary>
/// Reads real CPU/RAM figures from /proc on Linux. GPU/temperature/power are reported
/// as null ("Unavailable" in the UI) unless a platform-specific provider is added later —
/// we do not fabricate values for metrics we cannot actually read (rule #7 / #65).
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CPU telemetry unavailable on this host.");
        }

        try
        {
            (ramUsed, ramTotal) = await ReadMemoryAsync(ct);
        }
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
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw --format=csv,noheader,nounits",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is not null)
            {
                var line = await process.StandardOutput.ReadLineAsync(ct);
                await process.WaitForExitAsync(ct);
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(line))
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
        }
        catch (Exception ex) { _logger.LogDebug(ex, "nvidia-smi telemetry unavailable."); }

        // AMD ROCm: use rocm-smi JSON if present. Exact fields differ by version, so
        // unsupported versions simply report null rather than fabricated values.
        try
        {
            var psi = new ProcessStartInfo { FileName = "rocm-smi", Arguments = "--showuse --showmemuse --showtemp --showpower --json", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(psi);
            if (process is not null)
            {
                var json = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                if (process.ExitCode == 0 && json.Length > 0)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var gpu = doc.RootElement.EnumerateObject().FirstOrDefault().Value;
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
        catch (Exception ex) { _logger.LogDebug(ex, "rocm-smi telemetry unavailable."); }
        return (null, null, null, null, null);
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

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

        return new HardwareSnapshot(
            TimestampUtc: DateTimeOffset.UtcNow,
            CpuUtilizationPercent: cpuPercent,
            RamUsedBytes: ramUsed,
            RamTotalBytes: ramTotal,
            GpuUtilizationPercent: null,   // requires vendor-specific tooling (nvidia-smi / rocm-smi); extension point
            GpuVramUsedBytes: null,
            GpuVramTotalBytes: null,
            DiskUsedBytes: null,           // extension point: DriveInfo per configured workspace volume
            DiskTotalBytes: null,
            CurrentProcessMemoryBytes: processMemory,
            TemperatureCelsius: null,      // requires lm-sensors or platform API; extension point
            PowerWatts: null
        );
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

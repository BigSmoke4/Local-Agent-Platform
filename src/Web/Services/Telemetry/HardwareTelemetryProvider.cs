using System.Diagnostics;

namespace Platform.Web.Services.Telemetry;

public record HardwareSnapshot(
    double? CpuPercent,
    long? ProcessMemoryBytes,
    long? TotalPhysicalMemoryBytes,
    string GpuStatus,
    DateTime SampledAtUtc);

/// <summary>
/// Reports real process/CPU/RAM metrics available cross-platform via .NET APIs.
/// GPU metrics require vendor-specific tooling (nvidia-smi etc.) not present
/// in this environment by default, so GPU is honestly reported as
/// "Unavailable" rather than fabricated, per platform rule §7.
/// </summary>
public class HardwareTelemetryProvider
{
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpuTime = TimeSpan.Zero;
    private DateTime _lastSampleUtc = DateTime.UtcNow;

    public Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        _process.Refresh();

        double? cpuPercent = null;
        var now = DateTime.UtcNow;
        var currentCpuTime = _process.TotalProcessorTime;

        if (_lastSampleUtc != now)
        {
            var cpuUsedMs = (currentCpuTime - _lastCpuTime).TotalMilliseconds;
            var wallElapsedMs = (now - _lastSampleUtc).TotalMilliseconds;
            if (wallElapsedMs > 0)
            {
                cpuPercent = Math.Round(cpuUsedMs / (Environment.ProcessorCount * wallElapsedMs) * 100.0, 1);
                cpuPercent = Math.Clamp(cpuPercent.Value, 0, 100);
            }
        }

        _lastCpuTime = currentCpuTime;
        _lastSampleUtc = now;

        long? totalPhysicalMemory = null;
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            totalPhysicalMemory = gcInfo.TotalAvailableMemoryBytes > 0 ? gcInfo.TotalAvailableMemoryBytes : null;
        }
        catch
        {
            totalPhysicalMemory = null;
        }

        var snapshot = new HardwareSnapshot(
            CpuPercent: cpuPercent,
            ProcessMemoryBytes: _process.WorkingSet64,
            TotalPhysicalMemoryBytes: totalPhysicalMemory,
            GpuStatus: "Unavailable",
            SampledAtUtc: now);

        return Task.FromResult(snapshot);
    }
}

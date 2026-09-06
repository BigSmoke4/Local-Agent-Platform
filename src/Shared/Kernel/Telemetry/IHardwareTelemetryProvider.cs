namespace LocalAgentPlatform.Shared.Kernel.Telemetry;

/// <summary>
/// Reads real hardware metrics from the host. Any metric the host OS does not expose
/// must be returned as null — callers render "Unavailable" rather than a fabricated number.
/// </summary>
public interface IHardwareTelemetryProvider
{
    Task<HardwareSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}

public sealed record HardwareSnapshot(
    DateTimeOffset TimestampUtc,
    double? CpuUtilizationPercent,
    long? RamUsedBytes,
    long? RamTotalBytes,
    double? GpuUtilizationPercent,
    long? GpuVramUsedBytes,
    long? GpuVramTotalBytes,
    long? DiskUsedBytes,
    long? DiskTotalBytes,
    long? CurrentProcessMemoryBytes,
    double? TemperatureCelsius,
    double? PowerWatts
);

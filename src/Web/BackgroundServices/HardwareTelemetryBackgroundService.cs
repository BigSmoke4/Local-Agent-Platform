using Platform.Web.Hubs;
using Platform.Web.Services.Telemetry;

namespace Platform.Web.BackgroundServices;

/// <summary>
/// Periodically samples real hardware telemetry and broadcasts it over
/// SignalR. Runs off the request pipeline per platform rule §40.
/// </summary>
public class HardwareTelemetryBackgroundService : BackgroundService
{
    private readonly HardwareTelemetryProvider _provider;
    private readonly IAgentEventBroadcaster _broadcaster;
    private readonly ILogger<HardwareTelemetryBackgroundService> _logger;

    public HardwareTelemetryBackgroundService(
        HardwareTelemetryProvider provider,
        IAgentEventBroadcaster broadcaster,
        ILogger<HardwareTelemetryBackgroundService> logger)
    {
        _provider = provider;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _provider.GetSnapshotAsync(stoppingToken);
                await _broadcaster.HardwareTelemetryUpdatedAsync(snapshot, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Hardware telemetry sampling failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

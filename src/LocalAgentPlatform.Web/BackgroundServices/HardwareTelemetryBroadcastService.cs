using LocalAgentPlatform.Shared.Kernel.Telemetry;
using LocalAgentPlatform.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace LocalAgentPlatform.Web.BackgroundServices;

/// <summary>
/// Polls the real IHardwareTelemetryProvider on an interval and pushes the actual
/// snapshot to any connected client via SignalR (spec Section 20:
/// HardwareTelemetryUpdated). This does not invent a push-based OS API — it's a
/// real, if simple, polling loop, documented as such.
/// </summary>
public sealed class HardwareTelemetryBroadcastService : BackgroundService
{
    private readonly IHardwareTelemetryProvider _hardware;
    private readonly IHubContext<AgentTelemetryHub> _hub;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    public HardwareTelemetryBroadcastService(IHardwareTelemetryProvider hardware, IHubContext<AgentTelemetryHub> hub)
    {
        _hardware = hardware;
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var snapshot = await _hardware.GetSnapshotAsync(stoppingToken);
                await _hub.Clients.Group(AgentTelemetryHub.HardwareGroup)
                    .SendAsync(AgentTelemetryEvents.HardwareTelemetryUpdated, snapshot, cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Best-effort broadcast — a transient telemetry read failure should
                // never crash the host; the next tick will simply try again.
            }
        }
    }
}

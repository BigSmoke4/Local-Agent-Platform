using LocalAgentPlatform.Shared.Kernel.BackgroundWork;
using Microsoft.AspNetCore.SignalR;

namespace LocalAgentPlatform.Web.Hubs;

public sealed class SignalRAgentEventBroadcaster : IAgentEventBroadcaster
{
    private readonly IHubContext<AgentTelemetryHub> _hub;

    public SignalRAgentEventBroadcaster(IHubContext<AgentTelemetryHub> hub) => _hub = hub;

    public Task SessionUpdatedAsync(Guid sessionId, string state, CancellationToken ct = default) =>
        _hub.Clients.Group(AgentTelemetryHub.SessionGroup(sessionId.ToString()))
            .SendAsync(AgentTelemetryEvents.AgentSessionUpdated, new { sessionId, state }, cancellationToken: ct);

    public Task TaskUpdatedAsync(Guid sessionId, Guid taskId, string status, CancellationToken ct = default) =>
        _hub.Clients.Group(AgentTelemetryHub.SessionGroup(sessionId.ToString()))
            .SendAsync(AgentTelemetryEvents.AgentTaskUpdated, new { sessionId, taskId, status }, cancellationToken: ct);
}

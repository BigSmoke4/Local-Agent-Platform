using Microsoft.AspNetCore.SignalR;

namespace Platform.Web.Hubs;

/// <summary>
/// Real-time event hub. Server-to-client only (clients don't invoke methods
/// on this hub); AgentEventBroadcaster pushes genuine events as they occur
/// during agent execution and tool calls — nothing here is a timer-driven
/// fake animation.
/// </summary>
public class AgentHub : Hub
{
}

public interface IAgentEventBroadcaster
{
    Task AgentStateChangedAsync(Guid sessionId, string state, CancellationToken ct = default);
    Task ToolInvokedAsync(Guid sessionId, string toolName, CancellationToken ct = default);
    Task ToolCompletedAsync(Guid sessionId, string toolName, bool succeeded, CancellationToken ct = default);
    Task TokenUsageUpdatedAsync(Guid sessionId, int inputTokens, int outputTokens, double tokensPerSecond, CancellationToken ct = default);
    Task HardwareTelemetryUpdatedAsync(object snapshot, CancellationToken ct = default);
    Task VerificationUpdatedAsync(Guid sessionId, string stage, bool succeeded, string summary, CancellationToken ct = default);
}

public class AgentEventBroadcaster : IAgentEventBroadcaster
{
    private readonly IHubContext<AgentHub> _hub;

    public AgentEventBroadcaster(IHubContext<AgentHub> hub)
    {
        _hub = hub;
    }

    public Task AgentStateChangedAsync(Guid sessionId, string state, CancellationToken ct = default)
        => _hub.Clients.All.SendAsync("AgentStateChanged", new { sessionId, state }, ct);

    public Task ToolInvokedAsync(Guid sessionId, string toolName, CancellationToken ct = default)
        => _hub.Clients.All.SendAsync("ToolInvoked", new { sessionId, toolName }, ct);

    public Task ToolCompletedAsync(Guid sessionId, string toolName, bool succeeded, CancellationToken ct = default)
        => _hub.Clients.All.SendAsync("ToolCompleted", new { sessionId, toolName, succeeded }, ct);

    public Task TokenUsageUpdatedAsync(Guid sessionId, int inputTokens, int outputTokens, double tokensPerSecond, CancellationToken ct = default)
        => _hub.Clients.All.SendAsync("TokenUsageUpdated", new { sessionId, inputTokens, outputTokens, tokensPerSecond }, ct);

    public Task HardwareTelemetryUpdatedAsync(object snapshot, CancellationToken ct = default)
        => _hub.Clients.All.SendAsync("HardwareTelemetryUpdated", snapshot, ct);

    public Task VerificationUpdatedAsync(Guid sessionId, string stage, bool succeeded, string summary, CancellationToken ct = default)
        => _hub.Clients.All.SendAsync("VerificationUpdated", new { sessionId, stage, succeeded, summary }, ct);
}

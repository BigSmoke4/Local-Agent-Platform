using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LocalAgentPlatform.Web.Hubs;

/// <summary>
/// Real-time push channel for agent state, hardware telemetry, and tool events
/// (spec Section 18/20). Clients join a group per agent session to receive that
/// session's updates without polling. This hub only *broadcasts* — all state still
/// lives in Postgres; a client that misses a message (or connects late) can always
/// fall back to the existing MVC pages, which read the same source of truth.
/// Requires the same cookie session as the MVC UI — the browser sends it
/// automatically on the hub's WebSocket/long-polling handshake.
/// </summary>
[Authorize]
public sealed class AgentTelemetryHub : Hub
{
    public async Task JoinSessionGroup(string sessionId) =>
        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId));

    public async Task LeaveSessionGroup(string sessionId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, SessionGroup(sessionId));

    public async Task JoinHardwareGroup() =>
        await Groups.AddToGroupAsync(Context.ConnectionId, HardwareGroup);

    public static string SessionGroup(string sessionId) => $"session:{sessionId}";
    public const string HardwareGroup = "hardware-telemetry";
}

/// <summary>Typed helper so publishers don't hand-roll method name strings.</summary>
public static class AgentTelemetryEvents
{
    public const string AgentSessionUpdated = "AgentSessionUpdated";
    public const string AgentTaskUpdated = "AgentTaskUpdated";
    public const string HardwareTelemetryUpdated = "HardwareTelemetryUpdated";
}

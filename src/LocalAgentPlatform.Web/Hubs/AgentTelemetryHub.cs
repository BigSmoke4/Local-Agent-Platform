using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Hubs;

/// <summary>Real-time push channel for agent state and hardware telemetry. Joining an
/// agent group is owner-checked against PostgreSQL; authenticated users cannot subscribe
/// to another user's session by guessing its GUID.</summary>
[Authorize]
public sealed class AgentTelemetryHub : Hub
{
    private readonly PlatformDbContext _db;
    private readonly IWorkspaceRootPolicy _workspacePolicy;

    public AgentTelemetryHub(PlatformDbContext db, IWorkspaceRootPolicy workspacePolicy)
    {
        _db = db;
        _workspacePolicy = workspacePolicy;
    }

    public async Task JoinSessionGroup(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var id)) throw new HubException("Session not found.");
        var ownerId = Context.User?.RequireUserId();
        if (ownerId is null) throw new HubException("Session not found.");
        var repositoryPath = await _db.AgentSessions
            .Where(s => s.Id == id && s.OwnerUserId == ownerId.Value)
            .Join(_db.Repositories, s => s.RepositoryId, r => r.Id, (_, r) => r.LocalPath)
            .FirstOrDefaultAsync();
        if (repositoryPath is null || !_workspacePolicy.IsAllowed(repositoryPath))
            throw new HubException("Session not found.");
        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(id.ToString()));
    }

    public async Task LeaveSessionGroup(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var id)) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, SessionGroup(id.ToString()));
    }

    public Task JoinHardwareGroup() => Groups.AddToGroupAsync(Context.ConnectionId, HardwareGroup);

    public static string SessionGroup(string sessionId) => $"session:{sessionId}";
    public const string HardwareGroup = "hardware-telemetry";
}

public static class AgentTelemetryEvents
{
    public const string AgentSessionUpdated = "AgentSessionUpdated";
    public const string AgentTaskUpdated = "AgentTaskUpdated";
    public const string HardwareTelemetryUpdated = "HardwareTelemetryUpdated";
}

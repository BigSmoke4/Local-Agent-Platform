namespace LocalAgentPlatform.Shared.Kernel.BackgroundWork;

/// <summary>
/// Lets application-layer services (like the Agent orchestrator) publish real-time
/// events without depending on SignalR/ASP.NET Core directly — the Web host supplies
/// the real implementation (backed by <c>IHubContext</c>); anything without a host
/// (a future CLI, a test) can supply a no-op.
/// </summary>
public interface IAgentEventBroadcaster
{
    Task SessionUpdatedAsync(Guid sessionId, string state, CancellationToken ct = default);
    Task TaskUpdatedAsync(Guid sessionId, Guid taskId, string status, CancellationToken ct = default);
}

/// <summary>Default no-op so the orchestrator never needs a null-check — used when no
/// real-time transport is registered.</summary>
public sealed class NullAgentEventBroadcaster : IAgentEventBroadcaster
{
    public Task SessionUpdatedAsync(Guid sessionId, string state, CancellationToken ct = default) => Task.CompletedTask;
    public Task TaskUpdatedAsync(Guid sessionId, Guid taskId, string status, CancellationToken ct = default) => Task.CompletedTask;
}

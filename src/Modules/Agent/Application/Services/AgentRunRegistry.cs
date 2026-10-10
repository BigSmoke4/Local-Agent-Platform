using System.Collections.Concurrent;

namespace LocalAgentPlatform.Modules.Agent.Application.Services;

/// <summary>Tracks a single live cancellation source per session. Duplicate queue
/// deliveries are ignored rather than replacing a source and orphaning cancellation.</summary>
public sealed class AgentRunRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public bool TryRegister(Guid sessionId, out CancellationTokenSource cancellation)
    {
        cancellation = new CancellationTokenSource();
        if (_running.TryAdd(sessionId, cancellation)) return true;
        cancellation.Dispose();
        cancellation = null!;
        return false;
    }

    public CancellationTokenSource Register(Guid sessionId) =>
        TryRegister(sessionId, out var cancellation)
            ? cancellation
            : throw new InvalidOperationException($"Agent session {sessionId} is already running.");

    public void Unregister(Guid sessionId)
    {
        if (_running.TryRemove(sessionId, out var cancellation)) cancellation.Dispose();
    }

    public bool RequestCancellation(Guid sessionId)
    {
        if (!_running.TryGetValue(sessionId, out var cancellation)) return false;
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public bool IsRunning(Guid sessionId) => _running.ContainsKey(sessionId);
}

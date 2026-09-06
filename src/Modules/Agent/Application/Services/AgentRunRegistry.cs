using System.Collections.Concurrent;

namespace LocalAgentPlatform.Modules.Agent.Application.Services;

/// <summary>
/// Tracks a live CancellationTokenSource per running agent session so the "Cancel"
/// button in the UI can actually stop an in-progress background loop (spec Section 8:
/// "Support cancellation"). Deliberately a plain in-memory registry — if the process
/// restarts, in-flight sessions are simply no longer cancellable this way, which is a
/// real, documented limitation (see docs/STATUS.md), not hidden behavior.
/// </summary>
public sealed class AgentRunRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public CancellationTokenSource Register(Guid sessionId)
    {
        var cts = new CancellationTokenSource();
        _running[sessionId] = cts;
        return cts;
    }

    public void Unregister(Guid sessionId) => _running.TryRemove(sessionId, out _);

    public bool RequestCancellation(Guid sessionId)
    {
        if (_running.TryGetValue(sessionId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    public bool IsRunning(Guid sessionId) => _running.ContainsKey(sessionId);
}

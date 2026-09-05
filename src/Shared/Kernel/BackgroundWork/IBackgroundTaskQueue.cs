namespace LocalAgentPlatform.Shared.Kernel.BackgroundWork;

/// <summary>
/// Minimal background job queue so long-running work (repository indexing, telemetry
/// collection, etc.) never blocks an HTTP request (spec Section 40). A real
/// System.Threading.Channels-backed implementation lives in the Web host's composition
/// root; this interface is what application services depend on.
/// </summary>
public interface IBackgroundTaskQueue
{
    void QueueBackgroundWorkItem(Func<CancellationToken, Task> workItem);
}

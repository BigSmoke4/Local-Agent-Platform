using System.Threading.Channels;
using LocalAgentPlatform.Shared.Kernel.BackgroundWork;

namespace LocalAgentPlatform.Web.Infrastructure;

public sealed class ChannelBackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _channel =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    public void QueueBackgroundWorkItem(Func<CancellationToken, Task> workItem)
    {
        if (!_channel.Writer.TryWrite(workItem))
            throw new InvalidOperationException("Failed to enqueue background work item.");
    }

    public IAsyncEnumerable<Func<CancellationToken, Task>> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Drains the background queue sequentially. Real work (e.g. repository indexing) runs
/// here, off the HTTP request thread, per spec Section 40.
/// </summary>
public sealed class QueuedHostedService : BackgroundService
{
    private readonly ChannelBackgroundTaskQueue _queue;
    private readonly ILogger<QueuedHostedService> _logger;

    public QueuedHostedService(ChannelBackgroundTaskQueue queue, ILogger<QueuedHostedService> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var workItem in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await workItem(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in background work item.");
            }
        }
    }
}

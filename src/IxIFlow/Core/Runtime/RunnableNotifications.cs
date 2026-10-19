using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Core.Runtime;

// One bounded queue and at most one outstanding notifier call per host. A notifier that ignores
// cancellation may occupy that call forever; it cannot accumulate tasks or hold up store callers.
internal sealed class RunnableNotifications(IRunnableNotifier notifier,
    ILogger<RunnableNotifications>? logger = null) : IDisposable
{
    private readonly ILogger _logger = logger ?? NullLogger<RunnableNotifications>.Instance;
    private readonly Channel<RunnableNotice> _queue = Channel.CreateBounded<RunnableNotice>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private int _started;

    internal void Publish(RunnableNotice? notice)
    {
        if (notice == null || notifier is NullRunnableNotifier) return;
        if (Interlocked.Exchange(ref _started, 1) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(_ => _ = DrainAsync(), (object?)null);
        if (!_queue.Writer.TryWrite(notice))
            _logger.LogWarning("Runnable notification queue is full or stopped; dropped notice for {InstanceId}", notice.InstanceId);
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var notice in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                using var deliveryStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var delivery = Task.Run(() => notifier.NotifyAsync(notice, deliveryStop.Token));
                try
                {
                    await delivery.WaitAsync(TimeSpan.FromSeconds(1), _stop.Token);
                }
                catch (TimeoutException error)
                {
                    _logger.LogError(error, "Runnable notifier timed out for {InstanceId}", notice.InstanceId);
                    _ = deliveryStop.CancelAsync();
                    // Do not launch more calls while this one is stuck, even if it blocks synchronously.
                    try { await delivery.WaitAsync(_stop.Token); }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                    catch (Exception failure)
                    {
                        _logger.LogError(failure, "Runnable notifier failed after timing out for {InstanceId}", notice.InstanceId);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    _logger.LogError(error, "Runnable notifier failed for {InstanceId}", notice.InstanceId);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            _logger.LogError(error, "Runnable notification delivery stopped");
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _ = _stop.CancelAsync();
    }
}

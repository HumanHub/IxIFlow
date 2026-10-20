using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Core.Runtime;

// One bounded queue and at most one outstanding notifier call per host. A notifier that ignores
// cancellation may occupy that call forever; it cannot accumulate tasks or hold up store callers.
internal sealed class RunnableNotifications(IRunnableNotifier notifier,
    ILogger<RunnableNotifications>? logger = null, TimeProvider? timeProvider = null,
    ILogger<SensitiveDiagnostics>? sensitiveDiagnostics = null, InspectionRedactionPolicy? inspectionPolicy = null) : IDisposable
{
    private readonly ILogger _logger = logger ?? NullLogger<RunnableNotifications>.Instance;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _dropGate = new();
    private long _pendingDrops;
    private long? _lastDropLog;
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
        {
            Telemetry.RunnableNoticeDrops.Add(1);
            long dropped;
            lock (_dropGate)
            {
                _pendingDrops++;
                if (_lastDropLog is { } last && _time.GetElapsedTime(last) < TimeSpan.FromSeconds(30)) return;
                _lastDropLog = _time.GetTimestamp();
                dropped = _pendingDrops;
                _pendingDrops = 0;
            }
            _logger.LogWarning("Runnable notification queue is full or stopped; dropped {DroppedCount} notices since the last report", dropped);
        }
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
                    (inspectionPolicy ?? InspectionRedactionPolicy.Default).Log(_logger, sensitiveDiagnostics, LogLevel.Error, error, "Runnable notifier timed out for {InstanceId}", notice.InstanceId);
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
                    (inspectionPolicy ?? InspectionRedactionPolicy.Default).Log(_logger, sensitiveDiagnostics, LogLevel.Error, error, "Runnable notifier failed for {InstanceId}", notice.InstanceId);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            (inspectionPolicy ?? InspectionRedactionPolicy.Default).Log(_logger, sensitiveDiagnostics, LogLevel.Error, error, "Runnable notification delivery stopped");
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _ = _stop.CancelAsync();
    }
}

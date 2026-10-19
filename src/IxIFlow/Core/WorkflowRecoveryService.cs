using System.Threading.Channels;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
/// Wakes due waits and deadlines, and recovers interrupted instances. It polls the store and, when the store can wake
/// it, also claims as soon as the store says an instance became claimable.
/// </summary>
internal sealed partial class WorkflowRecoveryService(
    IServiceScopeFactory scopes,
    IWorkflowStateRepository repository,
    ILogger<WorkflowRecoveryService> logger,
    TimeProvider? timeProvider = null,
    HostSettings? settings = null,
    IRunnableWakeSource? wakeSource = null,
    CoordinationState? coordination = null) : BackgroundService
{
    private readonly HostSettings _settings = settings ?? HostSettings.Default;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly CoordinationState _coordination = coordination ?? new();
    private readonly object _lock = new();
    // Instances a pass is recovering now, and instances this host could not run, with the time it
    // tries them again, so the earliest rows cannot keep every slot's worth of the poll to themselves.
    private readonly HashSet<string> _recovering = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTime Until, int Times)> _deferred = new(StringComparer.Ordinal);

    // A rejection may be a race, such as another pass or the host's timer getting there first, or
    // lasting, such as a definition this host lacks; the wait grows from seconds to minutes.
    private void Defer(string instanceId)
    {
        lock (_lock)
        {
            var times = _deferred.TryGetValue(instanceId, out var previous) ? previous.Times + 1 : 1;
            var wait = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(times - 1, 6))));
            _deferred[instanceId] = (_time.GetUtcNow().UtcDateTime + wait, times);
        }
    }

    internal async Task RecoverOnceAsync(CancellationToken cancellationToken) =>
        await await ClaimAsync(cancellationToken);

    // Lists the instances this host may claim now and reserves them; the task returned recovers them.
    private async Task<Task> ClaimAsync(CancellationToken cancellationToken)
    {
        // A host claims only what it has slots for (D-10); the rest waits for the next pass or another host.
        int limit;
        using (var probe = scopes.CreateScope())
            limit = probe.ServiceProvider.GetService<WorkflowHost>()?.FreeSlots ?? _settings.Slots;
        if (limit == 0)
            return Task.CompletedTask;
        HashSet<string> skipped;
        lock (_lock)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            // Expired entries stay a while, so a row rejected again waits longer; long-expired ones go.
            foreach (var stale in _deferred.Where(item => now - item.Value.Until > TimeSpan.FromMinutes(10))
                         .Select(item => item.Key).ToArray())
                _deferred.Remove(stale);
            skipped = new HashSet<string>(_recovering.Concat(_deferred.Where(item => item.Value.Until > now)
                .Select(item => item.Key)), StringComparer.Ordinal);
        }
        ClaimQuery query;
        using (var probe = scopes.CreateScope())
            query = new ClaimQuery(_settings.Id, await WorkflowHeartbeatService.CapabilitiesAsync(
                probe.ServiceProvider.GetRequiredService<IWorkflowVersionRegistry>(), _settings), limit + skipped.Count);
        var candidates = await repository.GetRunnableInstancesAsync(query);
        // Reserve against the live sets, so a pass running alongside cannot take the same instance.
        var instances = new List<RunnableInstance>();
        lock (_lock)
            foreach (var candidate in candidates)
            {
                if (instances.Count == limit)
                    break;
                if (!skipped.Contains(candidate.InstanceId) && _recovering.Add(candidate.InstanceId))
                    instances.Add(candidate);
            }
        return RecoverReservedAsync(instances, cancellationToken);
    }

    private async Task RecoverReservedAsync(List<RunnableInstance> instances, CancellationToken cancellationToken)
    {
        try
        {
            await RecoverAsync(instances, cancellationToken);
        }
        finally
        {
            // Reservations of instances this pass never reached, for example after cancellation.
            lock (_lock)
                foreach (var instance in instances)
                    _recovering.Remove(instance.InstanceId);
        }
    }

    private async Task RecoverAsync(IReadOnlyList<RunnableInstance> instances, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(instances, new ParallelOptions
        {
            MaxDegreeOfParallelism = 8,
            CancellationToken = cancellationToken
        }, async (instance, token) =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                // The host stops before the scanner and drains the work it admitted; the scanner's
                // own token then only interrupts runs still going, which stay recoverable.
                var result = await scope.ServiceProvider.GetRequiredService<IWorkflowClient>()
                    .RecoverAsync(instance.InstanceId, token);
                if (result.Status == WorkflowExecutionStatus.Faulted)
                    logger.LogWarning("Recovery of workflow {InstanceId} was rejected: {Reason}",
                        instance.InstanceId, result.ErrorMessage);
                else if (result.Status == WorkflowExecutionStatus.NeedsResolution && result.ErrorMessage != null)
                    logger.LogWarning("Workflow {InstanceId} needs an operator: {Reason}",
                        instance.InstanceId, result.ErrorMessage);
                // A row this host rejected, or left stopped, may still be due; let others go first.
                if (result.Status is WorkflowExecutionStatus.Faulted or WorkflowExecutionStatus.NeedsResolution)
                    Defer(instance.InstanceId);
                else
                    lock (_lock)
                        _deferred.Remove(instance.InstanceId);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // The host is stopping; the instance is recovered after the next start.
            }
            catch (Exception error)
            {
                logger.LogError(error, "Could not recover workflow {InstanceId}", instance.InstanceId);
                // A row whose recovery throws, such as unreadable data, must not hold the poll.
                Defer(instance.InstanceId);
            }
            finally
            {
                lock (_lock)
                    _recovering.Remove(instance.InstanceId);
            }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var passes = new List<Task>();
        var wakes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true
        });
        void Wake() => wakes.Writer.TryWrite(true);
        _coordination.Wake += Wake;
        var listening = wakeSource == null ? Task.CompletedTask : ListenAsync(wakeSource, Wake, stoppingToken);
        var diagnostics = UnschedulableAsync(stoppingToken);
        // A pass runs until the work it handed out is idle or parked; the next tick does not wait for
        // it, and skips the instances it is still recovering.
        try
        {
            do
            {
                try { Track(passes, PassAsync(await ClaimAsync(stoppingToken), stoppingToken)); }
                catch (Exception error) when (error is not OperationCanceledException)
                { logger.LogError(error, "Could not scan running workflows for recovery"); }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var tick = Task.Delay(_coordination.Connected ? _coordination.PollInterval : _settings.PollInterval,
                    _time, wait.Token);
                await Task.WhenAny(wakes.Reader.ReadAsync(wait.Token).AsTask(), tick);
                wait.Cancel();
                stoppingToken.ThrowIfCancellationRequested();
            } while (true);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally { _coordination.Wake -= Wake; }
        await listening;
        await diagnostics;
        Task[] running;
        lock (passes)
            running = passes.ToArray();
        await Task.WhenAll(running);
    }

    private static void Track(List<Task> passes, Task pass)
    {
        lock (passes)
        {
            passes.RemoveAll(done => done.IsCompleted);
            passes.Add(pass);
        }
    }

    // A lost connection costs only latency: the poll goes on, and listening starts again a poll interval later.
    private async Task ListenAsync(IRunnableWakeSource source, Action wake, CancellationToken stoppingToken)
    {
        while (true)
        {
            var connected = _coordination.Observe(out var changed);
            if (connected)
            {
                try { await changed.WaitAsync(stoppingToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            using var listenStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            try
            {
                var listening = source.ListenAsync(wake, listenStop.Token);
                await Task.WhenAny(listening, changed);
                listenStop.Cancel();
                await listening;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (listenStop.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger.LogWarning(error, "Lost the store's wake-ups; polling every {PollInterval} until they return",
                    _settings.PollInterval);
            }
            try
            {
                using var retry = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                await Task.WhenAny(Task.Delay(_settings.PollInterval, _time, retry.Token), changed);
                retry.Cancel();
                stoppingToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PassAsync(Task pass, CancellationToken stoppingToken)
    {
        try
        {
            await pass;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "Could not scan running workflows for recovery");
        }
    }
}

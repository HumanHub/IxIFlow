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
    IRunnableWakeSource? wakeSource = null, ILogger<SensitiveDiagnostics>? sensitiveDiagnostics = null,
    InspectionRedactionPolicy? inspectionPolicy = null, CoordinationState? coordination = null) : BackgroundService
{
    private readonly HostSettings _settings = settings ?? HostSettings.Default;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly CoordinationState _coordination = coordination ?? new();
    private readonly object _lock = new();
    // Only entries actually taken by a worker are reserved across overlapping passes.
    private readonly Dictionary<string, object> _recovering = new(StringComparer.Ordinal);
    // Dictionary insertion order is not an eviction contract; keep oldest-first explicitly.
    private readonly Dictionary<string, DateTime> _refused = new(StringComparer.Ordinal);
    private readonly Queue<string> _refusalOrder = new();
    internal const int RefusedCapacity = 1024;
    internal static readonly TimeSpan RefusalInterval = TimeSpan.FromSeconds(1);
    private readonly Channel<bool> _wakes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true
    });
    private readonly Channel<bool> _refusalChanges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true
    });
    private readonly Dictionary<string, (DateTime Until, int Times)> _deferred = new(StringComparer.Ordinal);
    // Bound contention work even when discovery keeps finding more due instances.
    internal const int MaxCandidatesPerPass = 256;
    internal const int RefillPageSize = 8;

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
        var handedOff = false;
        try
        {
            var pass = await ReserveAsync(cancellationToken);
            handedOff = true;
            return CompletePassAsync(pass);
        }
        finally
        {
            // Discovery can fail before there is any reserved work to clean up.
            if (!handedOff) _coordination.CompleteClaimPass();
        }
    }

    private async Task CompletePassAsync(Task pass)
    {
        try { await pass; }
        finally { _coordination.CompleteClaimPass(); }
    }

    private async Task<Task> ReserveAsync(CancellationToken cancellationToken)
    {
        lock (_lock) ExpireRefusals();
        // A host claims only what it has slots for (D-10); the rest waits for the next pass or another host.
        int limit;
        using (var probe = scopes.CreateScope())
            limit = Math.Min(MaxCandidatesPerPass,
                probe.ServiceProvider.GetService<WorkflowHost>()?.FreeSlots ?? _settings.Slots);
        if (limit == 0)
        {
            return Task.CompletedTask;
        }
        var owner = new object();
        lock (_lock)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var stale in _deferred.Where(item => now - item.Value.Until > TimeSpan.FromMinutes(10))
                         .Select(item => item.Key).ToArray())
                _deferred.Remove(stale);
        }
        ClaimQuery query;
        using (var probe = scopes.CreateScope())
            query = new ClaimQuery(_settings.Id, await _settings.GetCapabilitiesAsync(
                probe.ServiceProvider.GetRequiredService<IWorkflowCatalog>()), limit);
        var first = await ReadCandidatesAsync(query, limit, [], cancellationToken);
        return RecoverCandidatesAsync(first, query, limit, owner, cancellationToken);
    }

    private Task<IReadOnlyList<RunnableInstance>> ReadCandidatesAsync(ClaimQuery query, int limit,
        IEnumerable<string> attempted, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            ExpireRefusals();
            query = query.Excluding(_refused.Keys.Concat(attempted), limit);
        }
        return repository.GetRunnableInstancesAsync(query);
    }

    // Called only under _lock. Expiries are fixed at refusal and therefore oldest-first.
    private void ExpireRefusals()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        while (_refusalOrder.TryPeek(out var id) && _refused[id] <= now)
        {
            _refusalOrder.Dequeue();
            _refused.Remove(id);
        }
    }

    private void Refuse(string id)
    {
        bool schedule;
        lock (_lock)
        {
            ExpireRefusals();
            if (_refused.ContainsKey(id)) return;
            schedule = _refused.Count == 0;
            if (_refused.Count == RefusedCapacity) _refused.Remove(_refusalOrder.Dequeue());
            _refusalOrder.Enqueue(id);
            _refused.Add(id, _time.GetUtcNow().UtcDateTime +
                (_settings.PollInterval < RefusalInterval ? _settings.PollInterval : RefusalInterval));
        }
        // A refusal can arrive while ExecuteAsync is already waiting on a ten-second poll.
        if (schedule) _refusalChanges.Writer.TryWrite(true);
    }

    private async Task RecoverCandidatesAsync(IReadOnlyList<RunnableInstance> first, ClaimQuery query, int slots,
        object owner, CancellationToken cancellationToken)
    {
        using var discovery = new SemaphoreSlim(1, 1);
        var page = new Queue<RunnableInstance>(first);
        var seen = new HashSet<string>(first.Select(candidate => candidate.InstanceId), StringComparer.Ordinal);
        var remaining = MaxCandidatesPerPass - first.Count;
        var exhausted = first.Count < slots;
        int refusalsAtStart;
        lock (_lock) refusalsAtStart = _refused.Count;
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, slots), new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = cancellationToken
            }, async (_, token) =>
            {
                while (true)
                {
                    RunnableInstance? instance = null;
                    try
                    {
                        await discovery.WaitAsync(token);
                        try
                        {
                            while (instance == null)
                            {
                                if (page.Count == 0)
                                {
                                    if (remaining == 0)
                                    {
                                        // Fill the bounded refusal set across immediate passes, before
                                        // the poll could expire it. Once full, eviction cannot busy-loop.
                                        lock (_lock)
                                            if (_refused.Count > refusalsAtStart) _wakes.Writer.TryWrite(true);
                                        return;
                                    }
                                    if (exhausted) return;
                                    var requested = Math.Min(RefillPageSize, remaining);
                                    var next = await ReadCandidatesAsync(query, requested, seen, token);
                                    remaining -= next.Count;
                                    exhausted = next.Count < requested;
                                    foreach (var candidate in next)
                                        if (seen.Add(candidate.InstanceId)) page.Enqueue(candidate);
                                    if (page.Count == 0 && exhausted) return;
                                }
                                var candidateToTake = page.Dequeue();
                                lock (_lock)
                                    if ((!_deferred.TryGetValue(candidateToTake.InstanceId, out var deferred) ||
                                         deferred.Until <= _time.GetUtcNow().UtcDateTime) &&
                                        (!_refused.TryGetValue(candidateToTake.InstanceId, out var expiry) ||
                                         expiry <= _time.GetUtcNow().UtcDateTime) &&
                                        _recovering.TryAdd(candidateToTake.InstanceId, owner))
                                        instance = candidateToTake;
                            }
                        }
                        finally { discovery.Release(); }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        (inspectionPolicy ?? InspectionRedactionPolicy.Default).Log(logger, sensitiveDiagnostics,
                            LogLevel.Error, error, "Could not refill workflows for recovery");
                        return;
                    }
                    // A no-grant result did not consume this slot; refill while siblings run.
                    if (await RecoverAsync(instance, owner, token)) return;
                }
            });
        }
        finally
        {
            lock (_lock)
                foreach (var id in _recovering.Where(item => ReferenceEquals(item.Value, owner)).Select(item => item.Key).ToArray())
                    _recovering.Remove(id);
        }
    }

    // Running is the client's ordinary busy/no-grant result; other results consumed this attempt's slot.
    private async Task<bool> RecoverAsync(RunnableInstance instance, object owner, CancellationToken token)
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
                    instance.InstanceId, InspectionRedactionPolicy.Default.ResultReason(result));
            else if (result.Status == WorkflowExecutionStatus.NeedsResolution && result.ErrorMessage != null)
                logger.LogWarning("Workflow {InstanceId} needs an operator: {Reason}",
                    instance.InstanceId, InspectionRedactionPolicy.Default.ResultReason(result));
            // A row this host rejected, or left stopped, may still be due; let others go first.
            if (result.Status is WorkflowExecutionStatus.Faulted or WorkflowExecutionStatus.NeedsResolution or WorkflowExecutionStatus.Paused)
                Defer(instance.InstanceId);
            else
            {
                lock (_lock)
                {
                    _deferred.Remove(instance.InstanceId);
                }
                if (result.Status == WorkflowExecutionStatus.Running) Refuse(instance.InstanceId);
            }
            return result.Status != WorkflowExecutionStatus.Running;
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
            (inspectionPolicy ?? InspectionRedactionPolicy.Default).Log(logger, sensitiveDiagnostics, LogLevel.Error, error, "Could not recover workflow {InstanceId}", instance.InstanceId);
            // A row whose recovery throws, such as unreadable data, must not hold the poll.
            Defer(instance.InstanceId);
        }
        finally
        {
            lock (_lock)
                if (_recovering.TryGetValue(instance.InstanceId, out var reservedBy) && ReferenceEquals(reservedBy, owner))
                    _recovering.Remove(instance.InstanceId);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var passes = new List<Task>();
        void Wake() => _wakes.Writer.TryWrite(true);
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
                { (inspectionPolicy ?? InspectionRedactionPolicy.Default).Log(logger, sensitiveDiagnostics, LogLevel.Error, error, "Could not scan running workflows for recovery"); }
                await WaitForPassAsync(stoppingToken);
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

    private async Task WaitForPassAsync(CancellationToken stoppingToken)
    {
        var pollAt = _time.GetUtcNow() + (_coordination.Connected ? _coordination.PollInterval : _settings.PollInterval);
        while (true)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var delay = pollAt - _time.GetUtcNow();
            lock (_lock)
                if (_refusalOrder.TryPeek(out var id))
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks, (_refused[id] - _time.GetUtcNow().UtcDateTime).Ticks));
            var tick = Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, _time, wait.Token);
            var wake = _wakes.Reader.ReadAsync(wait.Token).AsTask();
            var changed = _refusalChanges.Reader.ReadAsync(wait.Token).AsTask();
            await Task.WhenAny(wake, tick, changed);
            wait.Cancel();
            stoppingToken.ThrowIfCancellationRequested();
            if (wake.IsCompletedSuccessfully || tick.IsCompletedSuccessfully) return;
            // A late refusal changes the expiry timer, without starting an overlapping pass.
        }
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
                (inspectionPolicy ?? InspectionRedactionPolicy.Default).Log(logger, sensitiveDiagnostics, LogLevel.Warning, error, "Lost the store's wake-ups; polling every {PollInterval} until they return",
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
            (inspectionPolicy ?? InspectionRedactionPolicy.Default).Log(logger, sensitiveDiagnostics, LogLevel.Error, error, "Could not scan running workflows for recovery");
        }
    }
}

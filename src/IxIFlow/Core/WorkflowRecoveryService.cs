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
    // Recent refusals are an ordering hint, never an exclusion (including on a wake).
    private readonly Dictionary<string, long> _refused = new(StringComparer.Ordinal);
    private long _passNumber;
    private long _scanOffset;
    private readonly SemaphoreSlim _scan = new(1, 1);
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
    private async Task<Task> ClaimAsync(CancellationToken cancellationToken, bool wakeDriven = false)
    {
        var handedOff = false;
        try
        {
            var pass = await ReserveAsync(cancellationToken, wakeDriven);
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

    private async Task<Task> ReserveAsync(CancellationToken cancellationToken, bool wakeDriven)
    {
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
            var passNumber = ++_passNumber;
            foreach (var expired in _refused.Where(item => item.Value < passNumber).Select(item => item.Key).ToArray())
                _refused.Remove(expired);
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var stale in _deferred.Where(item => now - item.Value.Until > TimeSpan.FromMinutes(10))
                         .Select(item => item.Key).ToArray())
                _deferred.Remove(stale);
        }
        ClaimQuery query;
        using (var probe = scopes.CreateScope())
            query = new ClaimQuery(_settings.Id, await _settings.GetCapabilitiesAsync(
                probe.ServiceProvider.GetRequiredService<IWorkflowCatalog>()), limit);
        // A wake probes the head immediately without resetting the continuing scan. Its small
        // page leaves budget for scan progress even under a continuous stream of wakes.
        var first = await ReadCandidatesAsync(query, wakeDriven ? Math.Min(RefillPageSize, limit) : limit,
            cancellationToken, head: wakeDriven);
        return RecoverCandidatesAsync(first, query, limit, owner, cancellationToken);
    }

    private async Task<IReadOnlyList<RunnableInstance>> ReadCandidatesAsync(ClaimQuery query, int limit,
        CancellationToken cancellationToken, bool head = false)
    {
        // Continue the ordered scan across passes, even if every earlier candidate keeps refusing.
        // Serialize cursor advancement, not recovery. Changes in the runnable set are picked up
        // on wraparound. A page is only a prefetch; it owns no recovery reservations.
        await _scan.WaitAsync(cancellationToken);
        try
        {
            var candidates = await repository.GetRunnableInstancesAsync(query.Continue(head ? 0 : _scanOffset, limit));
            if (!head && candidates.Count == 0 && _scanOffset != 0)
            {
                _scanOffset = 0;
                candidates = await repository.GetRunnableInstancesAsync(query.Continue(0, limit));
            }
            if (!head) _scanOffset = candidates.Count < limit ? 0 : _scanOffset + candidates.Count;
            lock (_lock)
                return candidates.OrderBy(candidate => _refused.ContainsKey(candidate.InstanceId)).ToArray();
        }
        finally { _scan.Release(); }
    }

    private async Task RecoverCandidatesAsync(IReadOnlyList<RunnableInstance> first, ClaimQuery query, int slots,
        object owner, CancellationToken cancellationToken)
    {
        using var discovery = new SemaphoreSlim(1, 1);
        var page = new Queue<RunnableInstance>(first);
        var seen = new HashSet<string>(first.Select(candidate => candidate.InstanceId), StringComparer.Ordinal);
        var remaining = MaxCandidatesPerPass - first.Count;
        var exhausted = first.Count == 0;
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
                                    if (remaining == 0 || exhausted) return;
                                    var next = await ReadCandidatesAsync(query, Math.Min(RefillPageSize, remaining), token);
                                    remaining -= next.Count;
                                    foreach (var candidate in next)
                                        if (seen.Add(candidate.InstanceId)) page.Enqueue(candidate);
                                    // A wrapped page with no new entries ends this pass.
                                    if (page.Count == 0) { exhausted = true; return; }
                                }
                                var candidateToTake = page.Dequeue();
                                lock (_lock)
                                    if ((!_deferred.TryGetValue(candidateToTake.InstanceId, out var deferred) ||
                                         deferred.Until <= _time.GetUtcNow().UtcDateTime) &&
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
                lock (_lock)
                {
                    _deferred.Remove(instance.InstanceId);
                    if (result.Status == WorkflowExecutionStatus.Running)
                        _refused[instance.InstanceId] = _passNumber + 1;
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
        var wakeDriven = false;
        try
        {
            do
            {
                try { Track(passes, PassAsync(await ClaimAsync(stoppingToken, wakeDriven), stoppingToken)); }
                catch (Exception error) when (error is not OperationCanceledException)
                { (inspectionPolicy ?? InspectionRedactionPolicy.Default).Log(logger, sensitiveDiagnostics, LogLevel.Error, error, "Could not scan running workflows for recovery"); }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var tick = Task.Delay(_coordination.Connected ? _coordination.PollInterval : _settings.PollInterval,
                    _time, wait.Token);
                var wake = wakes.Reader.ReadAsync(wait.Token).AsTask();
                await Task.WhenAny(wake, tick);
                wakeDriven = wake.IsCompletedSuccessfully;
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

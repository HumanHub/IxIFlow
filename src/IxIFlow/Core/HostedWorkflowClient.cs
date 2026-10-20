using JsonSerializer = IxIFlow.Core.Runtime.WorkflowJson;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>
/// The client every application uses: calls become work for the <see cref="WorkflowHost"/>, which runs
/// them in slots. Reads and cancellation requests take no slot.
/// </summary>
internal sealed class HostedWorkflowClient(WorkflowHost host, TimeProvider time, IWorkflowCatalog catalog,
    IWorkflowStateRepository repository, CoordinationState? coordination = null) : IWorkflowClient
{
    public Task<UnschedulablePage> GetUnschedulableAsync(int limit = 100, UnschedulableCursor? cursor = null,
        CancellationToken cancellationToken = default) => host.ReadAsync(client => client.GetUnschedulableAsync(limit, cursor, cancellationToken));

    // A finite backstop also covers completions whose writer crashed before publishing a hint.
    private static readonly TimeSpan AwaitPollInterval = TimeSpan.FromSeconds(1);
    private TimeSpan CurrentAwaitPollInterval => coordination?.Connected == true
        ? coordination.PollInterval : AwaitPollInterval;

    private async Task WaitForChangeAsync(Task settled, CancellationToken token)
    {
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(token);
        var changed = coordination != null ? ChangeTask(coordination) : Task.Delay(Timeout.Infinite, poll.Token);
        await Task.WhenAny(settled, changed, Task.Delay(CurrentAwaitPollInterval, time, poll.Token));
        poll.Cancel();
        token.ThrowIfCancellationRequested();
    }

    private static Task ChangeTask(CoordinationState state) { state.Observe(out var changed); return changed; }

    public Task<WorkflowExecutionResult> RunAsync<TWorkflowData>(WorkflowDefinition definition,
        TWorkflowData workflowData, WorkflowOptions? options = null, CancellationToken cancellationToken = default)
        where TWorkflowData : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(workflowData);
        return UntilIdleAsync(host.RunAsync(options?.InstanceId,
            (client, _, token) => client.RunAsync(definition, workflowData, options, token), cancellationToken),
            cancellationToken);
    }

    public async Task<string> StartAsync<TWorkflowData>(string name, int? version, TWorkflowData workflowData,
        WorkflowOptions? options = null, CancellationToken cancellationToken = default) where TWorkflowData : class
    {
        WorkflowCatalogRules.ValidateName(name);
        var versionOmitted = version == null;
        if (version == null && options?.InstanceId is { } instanceId &&
            await repository.GetWorkflowInstanceSummaryAsync(instanceId) is { } existing)
        {
            if (existing.WorkflowName != name)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"Workflow instance '{instanceId}' belongs to a different definition");
            version = existing.WorkflowVersion;
        }
        var row = version is { } number ? await catalog.GetWorkflowVersionAsync(name, number)
            : await catalog.GetLatestWorkflowVersionAsync(name);
        if (row == null)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Workflow version not found: {name}");
        using var lease = await catalog.AcquireWorkflowDefinitionAsync(name, row.Version, row.ArtifactDigest, cancellationToken);
        var definition = lease.Definition;
        using var types = lease.EnterTypes();
        object data = workflowData;
        if (!definition.WorkflowDataType!.IsInstanceOfType(data))
        {
            try
            {
                data = JsonSerializer.Deserialize(JsonSerializer.Serialize(data),
                    definition.WorkflowDataType, System.Text.Json.JsonSerializerOptions.Web)
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Workflow data is empty");
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or NotSupportedException)
            { throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Workflow data does not match the declared data contract", error); }
        }
        return await StartDefinitionAsync(definition, data, options, cancellationToken, versionOmitted, lease);
    }

    public Task<string> StartAsync<TWorkflowData>(string name, TWorkflowData workflowData,
        WorkflowOptions? options = null, CancellationToken cancellationToken = default) where TWorkflowData : class =>
        StartAsync(name, null, workflowData, options, cancellationToken);

    public Task<string> StartAsync<TWorkflowData>(WorkflowDefinition definition,
        TWorkflowData workflowData, WorkflowOptions? options = null, CancellationToken cancellationToken = default)
        where TWorkflowData : class => StartDefinitionAsync(definition, workflowData, options, cancellationToken, false);

    private async Task<string> StartDefinitionAsync<TWorkflowData>(WorkflowDefinition definition,
        TWorkflowData workflowData, WorkflowOptions? options, CancellationToken cancellationToken, bool versionOmitted,
        WorkflowDefinitionLease? definitionLease = null)
        where TWorkflowData : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(workflowData);
        options ??= new WorkflowOptions();
        // Inside a slot the start runs inline, so it cannot return before the run ends. The host
        // decides inline or queued once, so a run that ends meanwhile cannot change the choice.
        if (host.TryRunInline((client, _, token) => client.RunAsync(definition, workflowData, options, token, versionOmitted),
                cancellationToken, out var inline))
            return (await inline).InstanceId;
        var accepted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Once accepted the run belongs to the host; the caller's token only stops the wait.
        var queuedLease = definitionLease?.Retain();
        Task<WorkflowExecutionResult> run;
        try
        {
            run = host.RunAsync(options.InstanceId, (client, control, token) =>
            {
                control.OnAccepted(instanceId => accepted.TrySetResult(instanceId));
                return client.RunAsync(definition, workflowData, options, token, versionOmitted);
            }, CancellationToken.None);
        }
        catch { queuedLease?.Dispose(); throw; }
        using (ExecutionContextFlow.SuppressIfNeeded())
            _ = run.ContinueWith(finished => { queuedLease?.Dispose(); _ = finished.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (await Task.WhenAny(accepted.Task, run).WaitAsync(cancellationToken) == accepted.Task)
            return await accepted.Task;
        // The run ended without saving a new start: a repeated start of an existing instance is
        // accepted; anything else, such as another host owning the ID, is not.
        var result = await run;
        var saved = await host.ReadAsync(client => client.ReadAsync(result.InstanceId, cancellationToken));
        if (saved != null && result.Status != WorkflowExecutionStatus.Running)
            return result.InstanceId;
        throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
            $"The start was not saved: {result.ErrorMessage ?? result.Status.ToString()}").At(result.InstanceId);
    }

    public Task<WorkflowExecutionResult> ResumeAsync<TEventData>(string instanceId, TEventData @event,
        CancellationToken cancellationToken = default) where TEventData : class =>
        SendAsync(instanceId, null, @event, Guid.NewGuid().ToString("N"), cancellationToken);

    public Task<WorkflowExecutionResult> ResumeAsync<TEventData>(string instanceId, string key,
        TEventData @event, CancellationToken cancellationToken = default) where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return SendAsync(instanceId, key, @event, Guid.NewGuid().ToString("N"), cancellationToken);
    }

    public Task<WorkflowExecutionResult> ResumeAsync<TEventData>(string instanceId, string? key,
        TEventData @event, string eventId, CancellationToken cancellationToken = default)
        where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        return SendAsync(instanceId, key, @event, eventId, cancellationToken);
    }

    // The event is stored without a slot, so a busy owner on this host takes it at its next pass (D-32);
    // then the instance runs in a slot. When another host runs the instance, its owner decides the
    // event, and the caller waits for that decision.
    private Task<WorkflowExecutionResult> SendAsync<TEventData>(string instanceId, string? key,
        TEventData @event, string eventId, CancellationToken cancellationToken) where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(@event);
        return DecideAsync(instanceId, eventId,
            client => client.DeliverAsync(instanceId, key, @event, eventId), cancellationToken);
    }

    public Task<WorkflowExecutionResult> ReplyAsync<TReply>(string token, TReply reply,
        CancellationToken cancellationToken = default) where TReply : class =>
        ReplyAsync(token, reply, Guid.NewGuid().ToString("N"), cancellationToken);

    public Task<WorkflowExecutionResult> ReplyAsync<TReply>(string token, TReply reply, string eventId,
        CancellationToken cancellationToken = default) where TReply : class
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (!ReplyToken.TryRead(token, out var instanceId, out var waitId, out var subject))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Unknown reply token");
        return DecideAsync(instanceId, eventId,
            client => client.DeliverReplyAsync(instanceId, waitId, reply, eventId, subject), cancellationToken);
    }

    public async Task<WorkflowReadResult<OpenWait>> GetOpenWaitsAsync(WaitQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        // Only waits without a request are listed: a request's answer goes to the token the request
        // sent, and a child's waits shown on its parent are listed on the child (D-33). The store
        // applies the filter, so paging by wait reference never skips a listed wait.
        if (query.InstanceId is { } id && await GetStatusAsync(id) == WorkflowExecutionStatus.Removed)
            return new([], isRemoved: true);
        if (query.Kind is not (null or WaitKind.Event))
            return new([]);
        var waits = await host.ReadAsync(client => client.GetOpenWaitsAsync(query with { Kind = WaitKind.Event }, cancellationToken));
        var removed = query.InstanceId is { } instanceId &&
            await GetStatusAsync(instanceId) == WorkflowExecutionStatus.Removed;
        return new(removed ? [] : waits, removed);
    }

    private async Task<WorkflowExecutionResult> DecideAsync(string instanceId, string eventId,
        Func<WorkflowHost.SlotClient, Task<EventAppendResult>> deliver, CancellationToken cancellationToken)
    {
        var appended = await host.ReadAsync(deliver);
        if (await host.ReadAsync(client => client.AnswerAsync(instanceId, eventId, appended, cancellationToken)) is { } answered)
            return answered;
        var result = await UntilIdleAsync(host.RunAsync(instanceId,
            (client, _, token) => client.TakeEventAsync(instanceId, eventId, token),
            cancellationToken), cancellationToken);
        // The caller gets the instance as it is once the event is decided and the instance is idle. An
        // event stays undecided while the instance is stopped for an operator.
        var receipt = result.Event;
        var status = result.Status;
        while (receipt?.Outcome == EventOutcome.Pending
                   ? status is WorkflowExecutionStatus.Running or WorkflowExecutionStatus.Scheduled or WorkflowExecutionStatus.Suspended
                   : status is WorkflowExecutionStatus.Running or WorkflowExecutionStatus.Scheduled)
        {
            using var watch = host.WatchSettled(instanceId, out var settled);
            await host.ConfirmSubscriptionAsync(instanceId, cancellationToken);
            receipt = await host.ReadAsync(client => client.ReadEventAsync(instanceId, eventId));
            status = await host.ReadAsync(client => client.ReadStatusAsync(instanceId)) ?? status;
            if (receipt?.Outcome != EventOutcome.Pending && status != WorkflowExecutionStatus.Running)
            {
                result = await AwaitAsync(instanceId, WorkflowAwaitCondition.Idle, cancellationToken);
                break;
            }
            await WaitForChangeAsync(settled, cancellationToken);
        }
        result.Event = receipt;
        return result;
    }

    public async Task<IReadOnlyList<EventDelivery>> PublishAsync<TEventData>(string key, TEventData @event,
        string eventId, CancellationToken cancellationToken = default) where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        var deliveries = new List<EventDelivery>();
        var reached = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var waits = await host.ReadAsync(client => client.GetOpenWaitsAsync(
                new WaitQuery(Key: key, Kind: WaitKind.Event, AfterWaitRef: after, Limit: PublishPageSize), cancellationToken));
            foreach (var instanceId in waits.Select(wait => wait.InstanceId).Where(reached.Add))
            {
                var appended = await host.ReadAsync(client => client.DeliverAsync(instanceId, key, @event, eventId));
                deliveries.Add(new EventDelivery(instanceId, appended.Status, appended.Rejection));
                // The owner, or a host that claims the instance, decides it at its next pass.
                if (appended.Status == EventAppendStatus.Stored)
                    host.WakeNow(instanceId);
            }
            if (waits.Count < PublishPageSize)
                return deliveries;
            after = WaitReference.Of(waits[^1].InstanceId, waits[^1].WaitId);
        }
    }

    private const int PublishPageSize = 100;

    // A run parked on a retry gave its slot back; a caller that asked to run until idle waits for
    // the instance without a slot while the host resumes it on time (C-03).
    private async Task<WorkflowExecutionResult> UntilIdleAsync(Task<WorkflowExecutionResult> run,
        CancellationToken cancellationToken)
    {
        var result = await run;
        if (result.Status != WorkflowExecutionStatus.Scheduled)
            return result;
        var idle = await AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Idle, cancellationToken);
        // The event was decided before the park; the later result must still say so.
        idle.Event = result.Event;
        return idle;
    }

    /// <summary>
    /// Continues an interrupted or due instance. A run that parks on a retry returns Scheduled; the
    /// host resumes it on time.
    /// </summary>
    public Task<WorkflowExecutionResult> RecoverAsync(string instanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return host.RunAsync(instanceId,
            (client, _, token) => client.RecoverAsync(instanceId, token), cancellationToken);
    }

    public Task<WorkflowExecutionResult> ReleaseAsync(string instanceId, string? actor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return UntilIdleAsync(host.RunAsync(instanceId,
            (client, _, token) => client.ReleaseAsync(instanceId, actor, token), cancellationToken),
            cancellationToken);
    }

    // The request is saved without a slot, so cancellation works while every slot is busy.
    // Only an idle instance takes a slot, to unwind its cancellation handlers.
    public async Task<WorkflowExecutionResult> CancelAsync(string instanceId, CancellationReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        var requested = await host.ReadAsync(client =>
            client.RequestCancellationAsync(instanceId, reason, cancellationToken));
        if (requested == null)
            return await UntilIdleAsync(RecoverAsync(instanceId, cancellationToken), cancellationToken);
        // A running instance may be parked with nobody to notice the request; resume it now. If an
        // owner holds it, the recovery answers busy and the owner unwinds.
        if (requested.Status == WorkflowExecutionStatus.Running)
            host.WakeNow(instanceId);
        return requested;
    }

    public Task<WorkflowExecutionResult> ResolveAsync(string instanceId, string invocationId,
        ActivityResolution resolution, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return UntilIdleAsync(host.RunAsync(instanceId,
            (client, _, token) => client.ResolveAsync(instanceId, invocationId, resolution, token),
            cancellationToken), cancellationToken);
    }

    public Task<WorkflowExecutionStatus?> GetStatusAsync(string instanceId) =>
        host.ReadAsync(client => client.ReadStatusAsync(instanceId));

    public async Task<WorkflowReadResult<PendingActivityInfo>> GetPendingActivitiesAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var items = await host.ReadAsync(client => client.GetPendingActivitiesAsync(instanceId, cancellationToken));
        var removed = await GetStatusAsync(instanceId) == WorkflowExecutionStatus.Removed;
        return new(removed ? [] : items, removed);
    }

    public async Task<WorkflowExecutionSnapshot?> GetSnapshotAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var snapshot = await host.ReadAsync(client => client.GetSnapshotAsync(instanceId, cancellationToken));
        return await GetStatusAsync(instanceId) == WorkflowExecutionStatus.Removed
            ? new WorkflowExecutionSnapshot { InstanceId = instanceId, IsRemoved = true } : snapshot;
    }

    public async Task<WorkflowReadResult<ExecutionTraceEntry>> GetTraceAsync(string instanceId)
    {
        var items = await host.ReadAsync(client => client.GetTraceAsync(instanceId));
        var removed = await GetStatusAsync(instanceId) == WorkflowExecutionStatus.Removed;
        return new(removed ? [] : items, removed);
    }

    public async Task<WorkflowExecutionResult> AwaitAsync(string instanceId,
        WorkflowAwaitCondition until = WorkflowAwaitCondition.Idle, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        while (true)
        {
            // Watch before reading, so work that finishes in between is not missed. Only work on
            // this instance wakes the caller; work elsewhere is seen by the poll.
            using var watch = host.WatchSettled(instanceId, out var settled);
            await host.ConfirmSubscriptionAsync(instanceId, cancellationToken);
            // Waiting reads only the status; the workflow data is read once, when the wait is over.
            var status = await host.ReadAsync(client => client.ReadStatusAsync(instanceId))
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"Workflow instance '{instanceId}' does not exist").At(instanceId);
            if (Satisfies(status, until) &&
                await host.ReadAsync(client => client.ReadAsync(instanceId, cancellationToken)) is { } result &&
                Satisfies(result.Status, until))
                return result;
            await WaitForChangeAsync(settled, cancellationToken);
        }
    }

    private static bool Satisfies(WorkflowExecutionStatus status, WorkflowAwaitCondition until) =>
        until == WorkflowAwaitCondition.Idle
            ? status != WorkflowExecutionStatus.Running
            : status is WorkflowExecutionStatus.Success or WorkflowExecutionStatus.Faulted or
                WorkflowExecutionStatus.Failed or WorkflowExecutionStatus.Cancelled or
                WorkflowExecutionStatus.TimedOut or WorkflowExecutionStatus.Removed or WorkflowExecutionStatus.Terminated;
}

using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>
/// The client every application uses (D-02): calls become work for the local host, which runs
/// them in slots. Reads and cancellation requests take no slot.
/// </summary>
internal sealed class HostedWorkflowClient(LocalWorkflowHost host, TimeProvider time) : IWorkflowClient
{
    // AwaitAsync also notices work finished on another host; until the store can notify (D-05,
    // D-12), it checks the saved state at this interval.
    private static readonly TimeSpan AwaitPollInterval = TimeSpan.FromSeconds(1);

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

    public async Task<string> StartAsync<TWorkflowData>(WorkflowDefinition definition,
        TWorkflowData workflowData, WorkflowOptions? options = null, CancellationToken cancellationToken = default)
        where TWorkflowData : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(workflowData);
        options ??= new WorkflowOptions();
        if (!options.PersistState)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                "A workflow started without waiting must persist its state");
        // Inside a slot the start runs inline, so it cannot return before the run ends. The host
        // decides inline or queued once, so a run that ends meanwhile cannot change the choice.
        if (host.TryRunInline((client, _, token) => client.RunAsync(definition, workflowData, options, token),
                cancellationToken, out var inline))
            return (await inline).InstanceId;
        var accepted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Once accepted the run belongs to the host; the caller's token only stops the wait.
        var run = host.RunAsync(options.InstanceId, (client, control, token) =>
        {
            control.OnAccepted(instanceId => accepted.TrySetResult(instanceId));
            return client.RunAsync(definition, workflowData, options, token);
        }, CancellationToken.None);
        _ = run.ContinueWith(finished => _ = finished.Exception, TaskContinuationOptions.OnlyOnFaulted);
        if (await Task.WhenAny(accepted.Task, run).WaitAsync(cancellationToken) == accepted.Task)
            return await accepted.Task;
        // The run ended without saving a new start: a repeated start of an existing instance is
        // accepted; anything else, such as another host owning the ID, is not.
        var result = await run;
        var saved = await host.ReadAsync(client => client.ReadAsync(result.InstanceId));
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
    private async Task<WorkflowExecutionResult> SendAsync<TEventData>(string instanceId, string? key,
        TEventData @event, string eventId, CancellationToken cancellationToken) where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(@event);
        var appended = await host.ReadAsync(client => client.DeliverAsync(instanceId, key, @event, eventId));
        if (await host.ReadAsync(client => client.AnswerAsync(instanceId, eventId, appended)) is { } answered)
            return answered;
        var result = await UntilIdleAsync(host.RunAsync(instanceId,
            (client, _, token) => client.TakeEventAsync(instanceId, eventId, token),
            cancellationToken), cancellationToken);
        while (result.Event is { Outcome: EventOutcome.Pending } &&
               result.Status is WorkflowExecutionStatus.Running or WorkflowExecutionStatus.Suspended)
        {
            using var watch = host.WatchSettled(instanceId, out var settled);
            var receipt = await host.ReadAsync(client => client.ReadEventAsync(instanceId, eventId));
            if (receipt?.Outcome != EventOutcome.Pending)
            {
                result = await AwaitAsync(instanceId, WorkflowAwaitCondition.Idle, cancellationToken);
                result.Event = receipt;
                break;
            }
            using var poll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAny(settled, Task.Delay(AwaitPollInterval, time, poll.Token));
            poll.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            result.Status = await host.ReadAsync(client => client.ReadStatusAsync(instanceId)) ?? result.Status;
        }
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
                new WaitQuery(Key: key, Kind: WaitKind.Event, AfterWaitId: after, Limit: PublishPageSize)));
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
            after = waits[^1].WaitId;
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

    public Task<WorkflowExecutionResult> ReleaseAsync(string instanceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return UntilIdleAsync(host.RunAsync(instanceId,
            (client, _, token) => client.ReleaseAsync(instanceId, token), cancellationToken),
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

    public Task<IReadOnlyList<PendingActivityInfo>> GetPendingActivitiesAsync(string instanceId) =>
        host.ReadAsync(client => client.GetPendingActivitiesAsync(instanceId));

    public async Task<WorkflowExecutionResult> AwaitAsync(string instanceId,
        WorkflowAwaitCondition until = WorkflowAwaitCondition.Idle, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        while (true)
        {
            // Watch before reading, so work that finishes in between is not missed. Only work on
            // this instance wakes the caller; work elsewhere is seen by the poll.
            using var watch = host.WatchSettled(instanceId, out var settled);
            // Waiting reads only the status; the workflow data is read once, when the wait is over.
            var status = await host.ReadAsync(client => client.ReadStatusAsync(instanceId))
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"Workflow instance '{instanceId}' does not exist").At(instanceId);
            if (Satisfies(status, until) &&
                await host.ReadAsync(client => client.ReadAsync(instanceId)) is { } result &&
                Satisfies(result.Status, until))
                return result;
            using var poll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAny(settled, Task.Delay(AwaitPollInterval, time, poll.Token));
            poll.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static bool Satisfies(WorkflowExecutionStatus status, WorkflowAwaitCondition until) =>
        until == WorkflowAwaitCondition.Idle
            ? status != WorkflowExecutionStatus.Running
            : status is WorkflowExecutionStatus.Success or WorkflowExecutionStatus.Faulted or
                WorkflowExecutionStatus.Failed or WorkflowExecutionStatus.Cancelled or
                WorkflowExecutionStatus.TimedOut;
}

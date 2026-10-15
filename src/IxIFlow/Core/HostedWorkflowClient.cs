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
        CancellationToken cancellationToken = default) where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return UntilIdleAsync(host.RunAsync(instanceId,
            (client, _, token) => client.ResumeAsync(instanceId, @event, token), cancellationToken),
            cancellationToken);
    }

    public Task<WorkflowExecutionResult> ResumeAsync<TEventData>(string instanceId, string key,
        TEventData @event, CancellationToken cancellationToken = default) where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return UntilIdleAsync(host.RunAsync(instanceId,
            (client, _, token) => client.ResumeAsync(instanceId, key, @event, token), cancellationToken),
            cancellationToken);
    }

    public Task<WorkflowExecutionResult> ResumeAsync<TEventData>(string instanceId, string? key,
        TEventData @event, string deliveryId, CancellationToken cancellationToken = default)
        where TEventData : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        return UntilIdleAsync(host.RunAsync(instanceId,
            (client, _, token) => client.ResumeAsync(instanceId, key, @event, deliveryId, token),
            cancellationToken), cancellationToken);
    }

    // A run parked on a retry gave its slot back; a caller that asked to run until idle waits for
    // the instance without a slot while the host resumes it on time (C-03).
    private async Task<WorkflowExecutionResult> UntilIdleAsync(Task<WorkflowExecutionResult> run,
        CancellationToken cancellationToken)
    {
        var result = await run;
        if (result.Status != WorkflowExecutionStatus.Scheduled)
            return result;
        var idle = await AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Idle, cancellationToken);
        // The event was taken before the park; the later result must still say so.
        idle.EventAccepted = result.EventAccepted;
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
            host.Schedule(instanceId, DateTime.MinValue);
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
            var result = await host.ReadAsync(client => client.ReadAsync(instanceId))
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"Workflow instance '{instanceId}' does not exist").At(instanceId);
            if (Satisfies(result.Status, until))
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

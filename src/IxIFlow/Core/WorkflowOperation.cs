using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>A durably accepted decision. Completion runs under the host's lifetime, independently of its caller.</summary>
public sealed class WorkflowOperation
{
    internal WorkflowOperation(Task<WorkflowOperationResult> completion)
    {
        Completion = completion;
        _ = completion.ContinueWith(done => _ = done.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task<WorkflowOperationResult> Completion { get; }
}

/// <summary>A public status projection, with no workflow data, trace or execution state.</summary>
public sealed record WorkflowOperationResult(string InstanceId, WorkflowExecutionStatus Status, string? ErrorMessage = null)
{
    public InspectionError? Error { get; init; }
    public WorkflowErrorKind? ErrorKind { get; init; }
}

internal sealed partial class HostedWorkflowOperations
{
    public async Task<WorkflowOperation> SubmitResolutionAsync(string instanceId, string invocationId, ActivityResolution resolution, Guid? expectedIncarnation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentNullException.ThrowIfNull(resolution);
        var incarnation = await IncarnationAsync(instanceId, expectedIncarnation);
        return await SubmitRunAsync(instanceId, (client, token) => client.ResolveAsync(instanceId, invocationId, resolution, token, incarnation));
    }

    public async Task<WorkflowOperation> SubmitReleaseAsync(string instanceId, string actor, Guid? expectedIncarnation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var incarnation = await IncarnationAsync(instanceId, expectedIncarnation);
        return await SubmitRunAsync(instanceId, (client, token) => client.ReleaseAsync(instanceId, actor, token, incarnation));
    }

    private async Task<WorkflowOperation> SubmitRunAsync(string instanceId,
        Func<WorkflowHost.SlotClient, CancellationToken, Task<WorkflowExecutionResult>> execute)
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = host.RunAsync(instanceId, (client, control, token) =>
        {
            control.OnAccepted(_ => accepted.TrySetResult());
            return execute(client, token);
        }, CancellationToken.None);
        await Task.WhenAny(accepted.Task, run);
        if (!accepted.Task.IsCompletedSuccessfully)
        {
            await run; // Preserve typed engine failures; an unaccepted busy/rejected result is a conflict.
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The operation was not accepted");
        }
        return new WorkflowOperation(CompleteAsync(run));
    }

    public async Task<WorkflowOperation> SubmitCancellationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        var incarnation = await IncarnationAsync(instanceId, expectedIncarnation);
        var requested = await host.ReadAsync(client => client.RequestCancellationAsync(instanceId, reason, CancellationToken.None, incarnation));
        if (requested?.Status == WorkflowExecutionStatus.Running) host.WakeNow(instanceId);
        return new WorkflowOperation(CompleteAsync(requested == null
            ? host.RunAsync(instanceId, (client, _, token) => client.RecoverAsync(instanceId, token, incarnation), CancellationToken.None)
            : Task.FromResult(requested)));
    }

    public async Task<WorkflowOperation> SubmitTerminationAsync(string instanceId, string reasonCode, string? description, string actor, Guid? expectedIncarnation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var incarnation = await IncarnationAsync(instanceId, expectedIncarnation);
        var accepted = await host.ReadAsync(client => client.RequestTerminationAsync(instanceId, reasonCode, description, actor, incarnation));
        if (!accepted.Accepted)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Termination was not accepted");
        return new WorkflowOperation(FinishAsync());

        async Task<WorkflowOperationResult> FinishAsync()
        {
            var summary = await AwaitTerminationAsync(instanceId, accepted.Incarnation, CancellationToken.None);
            return new(summary.InstanceId, WorkflowExecutionStatus.Terminated);
        }
    }

    private async Task<Guid> IncarnationAsync(string instanceId, Guid? expected) => expected ??
        (await GetAsync(instanceId))?.Incarnation ?? throw new WorkflowEngineException(
            WorkflowErrorKind.InvalidState, "The workflow instance does not exist");

    private async Task<WorkflowOperationResult> CompleteAsync(Task<WorkflowExecutionResult> run)
    {
        var result = await run;
        if (result.Status is WorkflowExecutionStatus.Scheduled or WorkflowExecutionStatus.Running)
            result = await new HostedWorkflowClient(host, time, catalog, repository, coordination).AwaitAsync(result.InstanceId);
        var error = result.ErrorMessage == null ? null : InspectionRedactionPolicy.Default.ResultError(result);
        return new(result.InstanceId, result.Status, result.ErrorMessage == null ? null : InspectionRedactionPolicy.Default.ResultReason(result))
            { Error = error, ErrorKind = result.ErrorKind };
    }
}

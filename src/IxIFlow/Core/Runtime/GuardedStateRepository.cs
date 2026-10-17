namespace IxIFlow.Core.Runtime;

/// <summary>
/// The state store as the engine sees it. Any store failure becomes an
/// <see cref="WorkflowErrorKind.Infrastructure"/> engine failure, so it interrupts the
/// instance instead of being handled as a workflow failure.
/// </summary>
internal sealed class GuardedStateRepository : IWorkflowStateRepository, IWorkflowInbox
{
    private readonly IWorkflowStateRepository _store;

    private GuardedStateRepository(IWorkflowStateRepository store) => _store = store;

    public static IWorkflowStateRepository Wrap(IWorkflowStateRepository store) =>
        store as GuardedStateRepository ?? new GuardedStateRepository(store);

    public Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason) =>
        Guard(() => _store.RequestCancellationAsync(instanceId, reason),
            $"Requesting cancellation of workflow instance '{instanceId}'");

    public Task<CancellationReason?> GetCancellationRequestAsync(string instanceId) =>
        Guard(() => _store.GetCancellationRequestAsync(instanceId),
            $"Reading the cancellation request of workflow instance '{instanceId}'");

    public Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
        IReadOnlyCollection<string> instanceIds) =>
        Guard(() => _store.GetCancellationRequestsAsync(instanceIds),
            $"Reading the cancellation requests of {instanceIds.Count} workflow instances");

    public Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(int limit) =>
        Guard(() => _store.GetRunnableInstancesAsync(limit), "Listing runnable workflow instances");

    private IWorkflowInbox Inbox => _store as IWorkflowInbox
        ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
            "The workflow state store has no inbox");

    public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent) =>
        Guard(() => Inbox.AppendEventAsync(workflowEvent),
            $"Storing event '{workflowEvent.EventId}' for workflow instance '{workflowEvent.InstanceId}'");

    public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit) =>
        Guard(() => Inbox.GetPendingEventsAsync(instanceId, afterSequence, limit),
            $"Reading the inbox of workflow instance '{instanceId}'");

    public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId) =>
        Guard(() => Inbox.GetEventAsync(instanceId, eventId),
            $"Reading event '{eventId}' of workflow instance '{instanceId}'");

    public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId) =>
        Guard(() => Inbox.GetWaitAsync(instanceId, waitId),
            $"Reading wait '{waitId}' of workflow instance '{instanceId}'");

    public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query) =>
        Guard(() => Inbox.GetOpenWaitsAsync(query), "Listing open workflow waits");

    public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(IReadOnlyCollection<string> instanceIds) =>
        Guard(() => Inbox.GetInboxSequencesAsync(instanceIds), "Reading workflow inbox sequences");

    public Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit) =>
        Guard(() => _store.CommitWorkflowInstanceAsync(commit),
            $"Saving workflow instance '{commit.InstanceId}'");

    public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration) =>
        Guard(() => _store.TryAcquireExecutionLeaseAsync(instanceId, token, duration),
            $"Acquiring the execution lease of workflow instance '{instanceId}'");

    public Task<bool> RenewExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
        Guard(() => _store.RenewExecutionLeaseAsync(instanceId, token, duration),
            $"Renewing the execution lease of workflow instance '{instanceId}'");

    public Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token) =>
        Guard(() => _store.ReleaseExecutionLeaseAsync(instanceId, token),
            $"Releasing the execution lease of workflow instance '{instanceId}'");

    public Task<IEnumerable<WorkflowInstance>> GetChildInstancesAsync(string parentInstanceId) =>
        Guard(() => _store.GetChildInstancesAsync(parentInstanceId),
            $"Reading the children of workflow instance '{parentInstanceId}'");

    public Task SaveWorkflowInstanceAsync(WorkflowInstance instance) =>
        Guard(() => _store.SaveWorkflowInstanceAsync(instance),
            $"Saving workflow instance '{instance.InstanceId}'");

    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId) =>
        Guard(() => _store.GetWorkflowInstanceAsync(instanceId),
            $"Reading workflow instance '{instanceId}'");

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
        Guard(() => _store.GetWorkflowInstancesByNameAsync(workflowName),
            $"Listing instances of workflow '{workflowName}'");

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
        Guard(() => _store.GetWorkflowInstancesByStatusAsync(status),
            $"Listing {status} workflow instances");

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
        Guard(() => _store.GetWorkflowInstancesByCorrelationIdAsync(correlationId),
            $"Listing workflow instances correlated by '{correlationId}'");

    public Task DeleteWorkflowInstanceAsync(string instanceId) =>
        Guard(() => _store.DeleteWorkflowInstanceAsync(instanceId),
            $"Deleting workflow instance '{instanceId}'");

    private static async Task<T> Guard<T>(Func<Task<T>> call, string operation)
    {
        try
        {
            return await call();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw Failure(operation, error);
        }
    }

    private static async Task Guard(Func<Task> call, string operation)
    {
        try
        {
            await call();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw Failure(operation, error);
        }
    }

    private static WorkflowEngineException Failure(string operation, Exception error) =>
        new(WorkflowErrorKind.Infrastructure, $"{operation} failed: {error.Message}", error);
}

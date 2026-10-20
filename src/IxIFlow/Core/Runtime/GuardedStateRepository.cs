namespace IxIFlow.Core.Runtime;

/// <summary>
/// The state store as the engine sees it. Any store failure becomes an
/// <see cref="WorkflowErrorKind.Infrastructure"/> engine failure, so it interrupts the
/// instance instead of being handled as a workflow failure.
/// </summary>
internal sealed class GuardedStateRepository : IWorkflowStateRepository
{
    public IPayloadStore PayloadStore => _store.PayloadStore;
    public Task<UnschedulablePage> GetUnschedulableAsync(int limit = 100, UnschedulableCursor? cursor = null,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetUnschedulableAsync(limit, cursor, cancellationToken), "Reading unschedulable work");
    public Task<bool> CanScheduleAsync(IReadOnlyList<WorkflowDemand> demands, CancellationToken cancellationToken = default) =>
        Guard(() => _store.CanScheduleAsync(demands, cancellationToken), "Checking live host eligibility");
    public Task<WorkflowDraft?> GetDefinitionAsync(string name, int version) =>
        Guard(() => _store.GetDefinitionAsync(name, version), "Accessing workflow catalog or artifacts");
    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision) =>
        Guard(() => _store.SaveDefinitionAsync(definition, expectedRevision), "Accessing workflow catalog or artifacts");
    public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null) =>
        Guard(() => _store.RegisterWorkflowVersionAsync(version, artifacts), "Accessing workflow catalog or artifacts");
    public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) =>
        Guard(() => _store.GetWorkflowVersionAsync(name, version), "Accessing workflow catalog or artifacts");
    public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) =>
        Guard(() => _store.GetWorkflowVersionsAsync(name), "Accessing workflow catalog or artifacts");
    public Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, IArtifactStore? artifacts = null) =>
        Guard(() => _store.SetWorkflowVersionStateAsync(name, version, state, artifacts), "Accessing workflow catalog or artifacts");
    public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore? artifacts = null) =>
        Guard(() => _store.PublishDefinitionAsync(draftId, version, artifacts), "Accessing workflow catalog or artifacts");
    public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) =>
        Guard(() => _store.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken), "Accessing workflow catalog or artifacts");
    public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) =>
        await Guard(() => _store.PutAsync(kind, content, cancellationToken).AsTask(), "Accessing workflow catalog or artifacts");
    public async ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        await Guard(() => _store.OpenAsync(digest, cancellationToken).AsTask(), "Accessing workflow catalog or artifacts");
    public async ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        await Guard(() => _store.ExistsAsync(digest, cancellationToken).AsTask(), "Accessing workflow catalog or artifacts");
    public async ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) =>
        await Guard(() => _store.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken).AsTask(), "Collecting artifacts");
    public async ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        await Guard(() => _store.DeleteAsync(digest, cancellationToken).AsTask(), "Accessing workflow catalog or artifacts");

    public Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default) =>
        Guard(() => _store.RegisterMemberAsync(member, cancellationToken), "Registering store member");
    public Task<bool> HeartbeatAsync(Guid memberId, IReadOnlyDictionary<string, string> capabilities,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.HeartbeatAsync(memberId, capabilities, cancellationToken), "Renewing store member heartbeat");
    public Task UnregisterMemberAsync(Guid memberId, CancellationToken cancellationToken = default) =>
        Guard(() => _store.UnregisterMemberAsync(memberId, cancellationToken), "Unregistering store member");
    public Task<IReadOnlyList<WorkflowDemand>> GetWorkflowDemandsAsync(string instanceId) =>
        Guard(() => _store.GetWorkflowDemandsAsync(instanceId), "Reading workflow demands");

    public Task<IReadOnlyList<StoreMember>> GetLiveMembersAsync(CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetLiveMembersAsync(cancellationToken), "Reading live store members");

    public Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false) =>
        Guard(() => _store.RegisterPublisherAsync(name, eventTypes, replayRetained), "Accessing workflow outbox");
    public Task UnregisterPublisherAsync(string name, string actor = "host") =>
        Guard(() => _store.UnregisterPublisherAsync(name, actor), "Unregistering workflow outbox publisher");
    public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query) =>
        Guard(() => _store.ListDeliveriesAsync(query), "Listing workflow outbox deliveries");
    public Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor) =>
        Guard(() => _store.RetryDeliveryAsync(eventId, publisherName, actor), "Retrying workflow outbox delivery");
    public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync() =>
        Guard(() => _store.ListPublishersAsync(), "Listing workflow outbox publishers");
    public Task<bool> EnablePublisherAsync(string publisherName, string actor) =>
        Guard(() => _store.EnablePublisherAsync(publisherName, actor), "Enabling workflow outbox publisher");
    public Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(string publisherName, string token, TimeSpan leaseDuration, int limit) =>
        Guard(() => _store.ClaimDeliveriesAsync(publisherName, token, leaseDuration, limit), "Accessing workflow outbox");
    public Task<bool> RenewDeliveryLeaseAsync(string eventId, string publisherName, string token, TimeSpan leaseDuration) =>
        Guard(() => _store.RenewDeliveryLeaseAsync(eventId, publisherName, token, leaseDuration), "Accessing workflow outbox");
    public Task<bool> SettleDeliveryAsync(string eventId, string publisherName, string token, OutboxPublishResult result) =>
        Guard(() => _store.SettleDeliveryAsync(eventId, publisherName, token, result), "Accessing workflow outbox");
    public Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName) =>
        Guard(() => _store.GetDeliveryAsync(eventId, publisherName), "Accessing workflow outbox");
    public Task<OutboxPublisherState?> GetPublisherAsync(string publisherName) =>
        Guard(() => _store.GetPublisherAsync(publisherName), "Accessing workflow outbox");

    public Task<WorkflowControlRequests> GetControlRequestsAsync(IReadOnlyCollection<string> ids) =>
        Guard(() => _store.GetControlRequestsAsync(ids), "Reading execution controls");
    public Task<DebugCommandResult> RequestDebugCommandAsync(string instanceId, DebugCommandRequest command) =>
        Guard(() => _store.RequestDebugCommandAsync(instanceId, command), "Requesting debug command");
    public Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> ids) =>
        Guard(() => _store.GetDebugStatesAsync(ids), "Reading debug commands");
    private readonly IWorkflowStateRepository _store;

    private GuardedStateRepository(IWorkflowStateRepository store) => _store = store;

    public static IWorkflowStateRepository Wrap(IWorkflowStateRepository store) =>
        store as GuardedStateRepository ?? new GuardedStateRepository(store);

    public Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention, int batchSize,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.PurgeAsync(finishedRetention, tombstoneRetention, batchSize, cancellationToken),
            "Purging retained workflows");

    public Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId) =>
        Guard(() => _store.GetTombstoneAsync(instanceId), $"Reading retention tombstone for '{instanceId}'");

    public Task<CancellationRequestResult> RequestCancellationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null) =>
        Guard(() => _store.RequestCancellationAsync(instanceId, reason, expectedIncarnation),
            $"Requesting cancellation of workflow instance '{instanceId}'");

    public Task<CancellationReason?> GetCancellationRequestAsync(string instanceId) =>
        Guard(() => _store.GetCancellationRequestAsync(instanceId),
            $"Reading the cancellation request of workflow instance '{instanceId}'");

    public Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(ClaimQuery query) =>
        Guard(() => _store.GetRunnableInstancesAsync(query), "Listing runnable workflow instances");

    public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent) =>
        Guard(() => _store.AppendEventAsync(workflowEvent),
            $"Storing event '{workflowEvent.EventId}' for workflow instance '{workflowEvent.InstanceId}'");

    public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit) =>
        Guard(() => _store.GetPendingEventsAsync(instanceId, afterSequence, limit),
            $"Reading the inbox of workflow instance '{instanceId}'");

    public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId) =>
        Guard(() => _store.GetEventAsync(instanceId, eventId),
            $"Reading event '{eventId}' of workflow instance '{instanceId}'");

    public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId) =>
        Guard(() => _store.GetWaitAsync(instanceId, waitId),
            $"Reading wait reference '{WaitReference.Of(instanceId, waitId)}' of workflow instance '{instanceId}'");

    public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query) =>
        Guard(() => _store.GetOpenWaitsAsync(query), "Listing open workflow waits");

    public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(IReadOnlyCollection<string> instanceIds) =>
        Guard(() => _store.GetInboxSequencesAsync(instanceIds), "Reading workflow inbox sequences");

    public Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit) =>
        Guard(() => _store.CommitWorkflowInstanceAsync(commit),
            $"Saving workflow instance '{commit.InstanceId}'", preserveArtifactUnavailable: true);

    public Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission) =>
        Guard(() => _store.AdmitWorkflowInstanceAsync(commit, admission),
            $"Admitting workflow instance '{commit.InstanceId}'", preserveArtifactUnavailable: true);

    public Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId) =>
        Guard(() => _store.GetWorkflowOwnershipAsync(instanceId), $"Reading ownership of '{instanceId}'");

    public Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key) =>
        Guard(() => _store.GetWorkflowAdmissionAsync(sender, trigger, key), "Reading workflow admission");

    public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration, ClaimQuery query, ExecutionClaimKind kind = ExecutionClaimKind.Runnable) =>
        Guard(() => _store.TryAcquireExecutionLeaseAsync(instanceId, token, duration, query, kind),
            $"Acquiring the execution lease of workflow instance '{instanceId}'");

    public Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(IReadOnlyList<ExecutionLeaseRenewal> leases) =>
        Guard(() => _store.RenewExecutionLeasesAsync(leases), "Renewing workflow execution leases");

    public Task<ExecutionLeaseReleaseResult> ReleaseExecutionLeaseAsync(string instanceId, string token) =>
        Guard(() => _store.ReleaseExecutionLeaseAsync(instanceId, token),
            $"Releasing the execution lease of workflow instance '{instanceId}'");

    public Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsForIncarnationAsync(string instanceId, Guid incarnation) =>
        Guard(() => _store.GetDiagnosticsForIncarnationAsync(instanceId, incarnation), "reading incarnation-bound workflow diagnostics");

    public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
        int limit = 1000) =>
        Guard(() => _store.GetTraceAsync(instanceId, afterSequence, limit),
            $"Reading the trace of workflow instance '{instanceId}'");


    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId) =>
        Guard(() => _store.GetWorkflowInstanceAsync(instanceId),
            $"Reading workflow instance '{instanceId}'");

    public Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId) =>
        Guard(() => _store.GetWorkflowInstanceSummaryAsync(instanceId),
            $"Reading workflow instance '{instanceId}'");

    public Task<InstancePage> QueryInstancesAsync(InstanceQuery query) =>
        Guard(() => _store.QueryInstancesAsync(query), "Searching workflow instances");

    public Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null) =>
        Guard(() => _store.RequestTerminationAsync(instanceId, reason, expectedIncarnation), "Requesting workflow termination");



    public Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null) =>
        Guard(() => _store.DeleteWorkflowInstanceAsync(instanceId, tombstoneRetention),
            $"Deleting workflow instance '{instanceId}'");

    private static async Task<T> Guard<T>(Func<Task<T>> call, string operation, bool preserveArtifactUnavailable = false)
    {
        try
        {
            return await call();
        }
        catch (Exception error) when (error is not OperationCanceledException &&
            error is not WorkflowEngineException { Kind: WorkflowErrorKind.InstanceRemoved or WorkflowErrorKind.ArtifactMismatch } &&
            !(preserveArtifactUnavailable && error is WorkflowEngineException { Kind: WorkflowErrorKind.ArtifactUnavailable or WorkflowErrorKind.ArtifactMismatch }))
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
        catch (Exception error) when (error is not OperationCanceledException &&
            error is not WorkflowEngineException { Kind: WorkflowErrorKind.InstanceRemoved })
        {
            throw Failure(operation, error);
        }
    }

    private static WorkflowEngineException Failure(string operation, Exception error) =>
        new(WorkflowErrorKind.Infrastructure, $"{operation} failed: {error.Message}", error);
}

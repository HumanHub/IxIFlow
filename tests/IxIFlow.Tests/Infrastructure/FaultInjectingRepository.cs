using IxIFlow.Core;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// An in-memory store that fails chosen reads or commits on demand, to test how the engine
/// treats infrastructure failures.
/// </summary>
public class FaultInjectingRepository : IWorkflowStateRepository
{
    public Task<CatalogPage<CatalogWorkflow>>
        ListWorkflowsAsync(CatalogWorkflowQuery query, CancellationToken cancellationToken = default) =>
        _inner.ListWorkflowsAsync(query, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogWorkflowVersion>> ListWorkflowVersionsAsync(string name, CatalogVersionQuery query,
        CancellationToken cancellationToken = default) =>
        _inner.ListWorkflowVersionsAsync(name, query, cancellationToken: cancellationToken);

    public Task<CatalogWorkflowVersionDetail?> GetWorkflowVersionDetailAsync(string name, int version, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        _inner.GetWorkflowVersionDetailAsync(name, version, scope, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogTemplate>>
        ListTemplatesAsync(CatalogTemplateQuery query, CancellationToken cancellationToken = default) =>
        _inner.ListTemplatesAsync(query, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogTemplate>> ListTemplateVersionsAsync(string key, CatalogVersionQuery query,
        CancellationToken cancellationToken = default) =>
        _inner.ListTemplateVersionsAsync(key, query, cancellationToken: cancellationToken);

    public Task<CatalogTemplateDetail?> GetTemplateDetailAsync(string key, int version, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        _inner.GetTemplateDetailAsync(key, version, scope, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<CatalogStepDescriptor>> ListStepDescriptorsAsync(CatalogScope scope,
        CancellationToken cancellationToken = default) => _inner.ListStepDescriptorsAsync(scope, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogActivity>>
        ListActivitiesAsync(CatalogActivityQuery query, CancellationToken cancellationToken = default) =>
        _inner.ListActivitiesAsync(query, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogActivity>> ListActivityVersionsAsync(string key, CatalogVersionQuery query,
        CancellationToken cancellationToken = default) =>
        _inner.ListActivityVersionsAsync(key, query, cancellationToken: cancellationToken);

    public Task<CatalogActivityDetail?> GetActivityAsync(string key, string version, ActivityProviderSelector provider, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        _inner.GetActivityAsync(key, version, provider, scope, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogPackage>> ListPackagesAsync(CatalogPackageQuery query, CancellationToken cancellationToken = default) =>
        _inner.ListPackagesAsync(query, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogPackage>> ListPackageVersionsAsync(string id, CatalogVersionQuery query,
        CancellationToken cancellationToken = default) => _inner.ListPackageVersionsAsync(id, query, cancellationToken: cancellationToken);

    public Task<CatalogPackageDetail?> GetPackageDetailAsync(string id, string version, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        _inner.GetPackageDetailAsync(id, version, scope, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogActivity>> GetPackageActivitiesAsync(string id, string version, CatalogVersionQuery query,
        CancellationToken cancellationToken = default) =>
        _inner.GetPackageActivitiesAsync(id, version, query, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowPackage>> GetPendingPackageDependentsAsync(string missingPackageId, string? afterIdentity, int limit,
        CancellationToken cancellationToken = default) =>
        _inner.GetPendingPackageDependentsAsync(missingPackageId, afterIdentity, limit, cancellationToken: cancellationToken);

    public Task<IxIFlow.Templates.TemplateVersionEntry> RegisterTemplateVersionAsync(IxIFlow.Templates.TemplateVersion version,
        IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default) =>
        _inner.RegisterTemplateVersionAsync(version, artifacts, actor, cancellationToken: cancellationToken);

    public Task<IxIFlow.Templates.TemplateVersionEntry?> GetTemplateVersionAsync(string key, int version,
        CancellationToken cancellationToken = default) =>
        _inner.GetTemplateVersionAsync(key, version, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<IxIFlow.Templates.TemplateVersionEntry>> GetTemplateVersionsAsync(string? key = null,
        CancellationToken cancellationToken = default) => _inner.GetTemplateVersionsAsync(key, cancellationToken: cancellationToken);

    public Task<IxIFlow.Templates.TemplateVersionEntry> PublishTemplateDefinitionAsync(WorkflowDraftId draftId,
        IxIFlow.Templates.TemplateVersion version, IArtifactStore artifacts, string? actor = null,
        CancellationToken cancellationToken = default) =>
        _inner.PublishTemplateDefinitionAsync(draftId, version, artifacts, actor, cancellationToken: cancellationToken);

    public Task<PackageHolders> GetPackageHoldersAsync(string id, string version, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        _inner.GetPackageHoldersAsync(id, version, scope, cancellationToken: cancellationToken);

    public Task<PackageRegistration> PublishPackageDefinitionAsync(WorkflowDraftId draftId, WorkflowPackage package,
        PackageActivities activities, IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default) =>
        _inner.PublishPackageDefinitionAsync(draftId, package, activities, artifacts, actor, cancellationToken: cancellationToken);

    public Task<WorkflowPackage> SetPackageWithdrawnAsync(string id, string version, bool withdrawn, CatalogMutationToken expected,
        string? actor = null, CancellationToken cancellationToken = default) =>
        _inner.SetPackageWithdrawnAsync(id, version, withdrawn, expected, actor, cancellationToken: cancellationToken);

    public Task<PackageRegistration> RegisterPackageAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts,
        string? actor = null, CancellationToken cancellationToken = default) =>
        _inner.RegisterPackageAsync(package, activities, artifacts, actor, cancellationToken: cancellationToken);

    public Task<PackageRegistration> CompletePackageExtractionAsync(string id, string version, ArtifactDigest expectedDigest,
        PackageActivities activities, IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default) =>
        _inner.CompletePackageExtractionAsync(id, version, expectedDigest, activities, artifacts, actor,
            cancellationToken: cancellationToken);

    public IArtifactStore? CatalogArtifacts
    {
        get => _inner.CatalogArtifacts;
        set => _inner.CatalogArtifacts = value;
    }

    public Task<IReadOnlyList<WorkflowPackage>> GetPackageExtractionPageAsync(PackageExtraction extraction, string? afterIdentity,
        int limit, CancellationToken cancellationToken = default) =>
        _inner.GetPackageExtractionPageAsync(extraction, afterIdentity, limit, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<InstalledActivityPackage>> GetInstalledPackagesAsync(string? id = null,
        CancellationToken cancellationToken = default) => _inner.GetInstalledPackagesAsync(id, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null, CancellationToken cancellationToken = default) =>
        _inner.GetPackagesAsync(id, cancellationToken: cancellationToken);

    public Task<WorkflowPackage?> GetPackageAsync(string id, string version, CancellationToken cancellationToken = default) =>
        _inner.GetPackageAsync(id, version, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit,
        CancellationToken cancellationToken = default) =>
        _inner.GetPackagePageAsync(id, afterVersion, limit, cancellationToken: cancellationToken);

    public IPayloadStore PayloadStore => _inner.PayloadStore;

    public Task<bool> DeleteDefinitionAsync(WorkflowDraftId expected, CancellationToken cancellationToken = default) =>
        _inner.DeleteDefinitionAsync(expected, cancellationToken: cancellationToken);

    public Task<CatalogPage<WorkflowDraftSummary>> GetDefinitionsAsync(WorkflowDraftQuery query,
        CancellationToken cancellationToken = default) => _inner.GetDefinitionsAsync(query, cancellationToken: cancellationToken);

    public Task<RetirementBlockers> GetRetirementBlockersAsync(string name, int version, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        _inner.GetRetirementBlockersAsync(name, version, scope, cancellationToken: cancellationToken);

    public Task<WorkflowPublication> BeginPublicationAsync(WorkflowDraftId draftId, Guid hostId, string? actor, TimeSpan staleAfter,
        CancellationToken cancellationToken = default) =>
        _inner.BeginPublicationAsync(draftId, hostId, actor, staleAfter, cancellationToken: cancellationToken);

    public Task<bool> HeartbeatPublicationAsync(Guid id, Guid hostId, CancellationToken cancellationToken = default) =>
        _inner.HeartbeatPublicationAsync(id, hostId, cancellationToken: cancellationToken);

    public Task<WorkflowPublication> CompletePublicationAsync(Guid id, Guid hostId, WorkflowPublicationOutcome outcome,
        CancellationToken cancellationToken = default) =>
        _inner.CompletePublicationAsync(id, hostId, outcome, cancellationToken: cancellationToken);

    public Task<WorkflowPublication?> GetPublicationAsync(Guid id, CatalogScope scope, CancellationToken cancellationToken = default) =>
        _inner.GetPublicationAsync(id, scope, cancellationToken: cancellationToken);

    public Task<WorkflowPublication?> GetLatestPublicationAsync(string kind, string name, int version, Guid generation, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        _inner.GetLatestPublicationAsync(kind, name, version, generation, scope, cancellationToken);

    public Task<CatalogPage<WorkflowPublication>> GetPublicationsAsync(WorkflowPublicationQuery query,
        CancellationToken cancellationToken = default) => _inner.GetPublicationsAsync(query, cancellationToken: cancellationToken);

    public Task<int> PurgePublicationsAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) =>
        _inner.PurgePublicationsAsync(retention, batchSize, cancellationToken);

    public Task<WorkflowDraft?> GetDefinitionAsync(string kind, string name, int version, CancellationToken cancellationToken = default) =>
        _inner.GetDefinitionAsync(kind, name, version, cancellationToken: cancellationToken);

    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, WorkflowDraftId? expected, string? actor = null,
        CancellationToken cancellationToken = default) =>
        _inner.SaveDefinitionAsync(definition, expected, actor, cancellationToken: cancellationToken);

    public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null,
        CancellationToken cancellationToken = default) =>
        _inner.RegisterWorkflowVersionAsync(version, artifacts, cancellationToken: cancellationToken);

    public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version, CancellationToken cancellationToken = default) =>
        _inner.GetWorkflowVersionAsync(name, version, cancellationToken: cancellationToken);

    public Task<long> GetCatalogRevisionAsync(CancellationToken cancellationToken = default) =>
        _inner.GetCatalogRevisionAsync(cancellationToken: cancellationToken);

    public Task<WorkflowCatalogChanges> GetWorkflowVersionChangesAsync(long afterRevision, CancellationToken cancellationToken = default) =>
        _inner.GetWorkflowVersionChangesAsync(afterRevision, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowVersion>>
        GetWorkflowVersionsAsync(string? name = null, CancellationToken cancellationToken = default) =>
        _inner.GetWorkflowVersionsAsync(name, cancellationToken: cancellationToken);

    public Task<WorkflowVersion> SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state,
        CatalogMutationToken expected, IArtifactStore? artifacts = null, string? actor = null,
        CancellationToken cancellationToken = default) =>
        _inner.SetWorkflowVersionStateAsync(name, version, state, expected, artifacts, actor, cancellationToken: cancellationToken);

    public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore artifacts,
        string? actor = null, CancellationToken cancellationToken = default) =>
        _inner.PublishDefinitionAsync(draftId, version, artifacts, actor, cancellationToken: cancellationToken);

    public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default) =>
        _inner.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);

    public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) =>
        _inner.PutAsync(kind, content, cancellationToken);

    public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        _inner.OpenAsync(digest, cancellationToken);

    public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        _inner.ExistsAsync(digest, cancellationToken);

    public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize,
        CancellationToken cancellationToken = default) =>
        _inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);

    public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(digest, cancellationToken);

    public Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default) =>
        _inner.RegisterMemberAsync(member, cancellationToken);

    public Task<bool> HeartbeatAsync(Guid memberId, IReadOnlyDictionary<string, string> capabilities,
        CancellationToken cancellationToken = default) =>
        _inner.HeartbeatAsync(memberId, capabilities, cancellationToken);

    public Task UnregisterMemberAsync(Guid memberId, CancellationToken cancellationToken = default) =>
        _inner.UnregisterMemberAsync(memberId, cancellationToken);

    public Task<IReadOnlyList<WorkflowDemand>> GetWorkflowDemandsAsync(string instanceId, CancellationToken cancellationToken = default) =>
        _inner.GetWorkflowDemandsAsync(instanceId, cancellationToken: cancellationToken);

    public Task<UnschedulablePage> GetUnschedulableAsync(int limit = 100, UnschedulableCursor? cursor = null,
        CancellationToken cancellationToken = default) =>
        _inner.GetUnschedulableAsync(limit, cursor, cancellationToken);

    public Task<bool> CanScheduleAsync(IReadOnlyList<WorkflowDemand> demands, CancellationToken cancellationToken = default) =>
        _inner.CanScheduleAsync(demands, cancellationToken);

    public Task<IReadOnlyList<StoreMember>> GetLiveMembersAsync(CancellationToken cancellationToken = default) =>
        _inner.GetLiveMembersAsync(cancellationToken);

    public Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention, int batchSize,
        CancellationToken cancellationToken = default) =>
        _inner.PurgeAsync(finishedRetention, tombstoneRetention, batchSize, cancellationToken);

    public Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId, CancellationToken cancellationToken = default) =>
        _inner.GetTombstoneAsync(instanceId, cancellationToken: cancellationToken);

    public Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission) =>
        _inner.AdmitWorkflowInstanceAsync(commit, admission);

    public Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId) =>
        _inner.GetWorkflowOwnershipAsync(instanceId);

    public Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key) =>
        _inner.GetWorkflowAdmissionAsync(sender, trigger, key);

    public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default) =>
        _inner.AppendEventAsync(workflowEvent, cancellationToken: cancellationToken);

    public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit,
        CancellationToken cancellationToken = default) =>
        _inner.GetPendingEventsAsync(instanceId, afterSequence, limit, cancellationToken: cancellationToken);

    public virtual Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId,
        CancellationToken cancellationToken = default) =>
        _inner.GetEventAsync(instanceId, eventId, cancellationToken: cancellationToken);

    public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId, CancellationToken cancellationToken = default) =>
        _inner.GetWaitAsync(instanceId, waitId, cancellationToken: cancellationToken);

    public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(
        IReadOnlyCollection<string> instanceIds, CancellationToken cancellationToken = default) =>
        _inner.GetInboxSequencesAsync(instanceIds, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query, CancellationToken cancellationToken = default) =>
        _inner.GetOpenWaitsAsync(query, cancellationToken: cancellationToken);

    private readonly IWorkflowStateRepository _inner;

    public FaultInjectingRepository() : this(TimeProvider.System)
    {
    }

    /// <summary>A store whose clock is <paramref name="time"/>, as a test that drives the engine's clock needs.</summary>
    public FaultInjectingRepository(TimeProvider time) => _inner = new InMemoryWorkflowStateRepository(time);

    public FaultInjectingRepository(IWorkflowStateRepository inner) => _inner = inner;
    private int _failedReads;
    private int _failedCommits;

    /// <summary>Instance reads that match fail with an IOException while this returns true.</summary>
    public Func<string, bool> FailRead { get; set; } = _ => false;

    /// <summary>Commits that match fail with an IOException while this returns true.</summary>
    public Func<WorkflowInstance, bool> FailCommit { get; set; } = NeverFail;

    private static readonly Func<WorkflowInstance, bool> NeverFail = _ => false;

    public int FailedReads => Volatile.Read(ref _failedReads);
    public int FailedCommits => Volatile.Read(ref _failedCommits);

    public Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId, CancellationToken cancellationToken = default) =>
        _inner.GetWorkflowInstanceSummaryAsync(instanceId, cancellationToken: cancellationToken);

    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (!FailRead(instanceId))
            return _inner.GetWorkflowInstanceAsync(instanceId, cancellationToken: cancellationToken);

        Interlocked.Increment(ref _failedReads);

        throw new IOException($"Injected read failure for '{instanceId}'");
    }

    public virtual async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit,
        CancellationToken cancellationToken = default)
    {
        if (ReferenceEquals(FailCommit, NeverFail))
            return await _inner.CommitWorkflowInstanceAsync(commit, cancellationToken: cancellationToken);

        // The whole instance the commit saves: the commit applied to the instance it was based on.
        var instance = commit.ReadInstance(commit.ReplacesParts
            ? null
            : await _inner.GetWorkflowInstanceAsync(commit.InstanceId, cancellationToken: cancellationToken));

        if (!FailCommit(instance))
            return await _inner.CommitWorkflowInstanceAsync(commit, cancellationToken: cancellationToken);

        Interlocked.Increment(ref _failedCommits);

        throw new IOException($"Injected commit failure for '{instance.InstanceId}'");
    }

    public Task<CancellationRequestResult> RequestCancellationAsync(string instanceId, CancellationReason reason,
        Guid? expectedIncarnation = null, CancellationToken cancellationToken = default) =>
        _inner.RequestCancellationAsync(instanceId, reason, expectedIncarnation, cancellationToken: cancellationToken);

    public virtual Task<CancellationReason?>
        GetCancellationRequestAsync(string instanceId, CancellationToken cancellationToken = default) =>
        _inner.GetCancellationRequestAsync(instanceId, cancellationToken: cancellationToken);

    public virtual Task<WorkflowControlRequests> GetControlRequestsAsync(IReadOnlyCollection<string> instanceIds) =>
        _inner.GetControlRequestsAsync(instanceIds);

    public Task<DebugCommandResult> RequestDebugCommandAsync(string instanceId, DebugCommandRequest command) =>
        _inner.RequestDebugCommandAsync(instanceId, command);

    public Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> instanceIds) =>
        _inner.GetDebugStatesAsync(instanceIds);

    public Task<IReadOnlyList<RunnableInstance>>
        GetRunnableInstancesAsync(ClaimQuery query, CancellationToken cancellationToken = default) =>
        _inner.GetRunnableInstancesAsync(query, cancellationToken: cancellationToken);

    public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token, TimeSpan duration, ClaimQuery query,
        ExecutionClaimKind kind = ExecutionClaimKind.Runnable, CancellationToken cancellationToken = default) =>
        _inner.TryAcquireExecutionLeaseAsync(instanceId, token, duration, query, kind);

    public virtual Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(IReadOnlyList<ExecutionLeaseRenewal> leases,
        CancellationToken cancellationToken = default) =>
        _inner.RenewExecutionLeasesAsync(leases, cancellationToken: cancellationToken);

    public Task<ExecutionLeaseReleaseResult> ReleaseExecutionLeaseAsync(string instanceId, string token,
        CancellationToken cancellationToken = default) =>
        _inner.ReleaseExecutionLeaseAsync(instanceId, token);

    public Task<InstancePage> QueryInstancesAsync(InstanceQuery query, CancellationToken cancellationToken = default) =>
        _inner.QueryInstancesAsync(query, cancellationToken: cancellationToken);

    public Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason,
        Guid? expectedIncarnation = null, CancellationToken cancellationToken = default) =>
        _inner.RequestTerminationAsync(instanceId, reason, expectedIncarnation, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsForIncarnationAsync(string instanceId, Guid incarnation,
        CancellationToken cancellationToken = default) =>
        _inner.GetDiagnosticsForIncarnationAsync(instanceId, incarnation, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
        int limit = 1000, CancellationToken cancellationToken = default) =>
        _inner.GetTraceAsync(instanceId, afterSequence, limit, cancellationToken: cancellationToken);

    public Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null,
        CancellationToken cancellationToken = default) =>
        _inner.DeleteWorkflowInstanceAsync(instanceId, tombstoneRetention, cancellationToken: cancellationToken);

    public Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false,
        CancellationToken cancellationToken = default) =>
        _inner.RegisterPublisherAsync(name, eventTypes, replayRetained, cancellationToken: cancellationToken);

    public Task UnregisterPublisherAsync(string name, string actor = "host", CancellationToken cancellationToken = default) =>
        _inner.UnregisterPublisherAsync(name, actor, cancellationToken: cancellationToken);

    public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query, CancellationToken cancellationToken = default) =>
        _inner.ListDeliveriesAsync(query, cancellationToken: cancellationToken);

    public Task<bool>
        RetryDeliveryAsync(string eventId, string publisherName, string actor, CancellationToken cancellationToken = default) =>
        _inner.RetryDeliveryAsync(eventId, publisherName, actor, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync(CancellationToken cancellationToken = default) =>
        _inner.ListPublishersAsync(cancellationToken: cancellationToken);

    public Task<bool> EnablePublisherAsync(string publisherName, string actor, CancellationToken cancellationToken = default) =>
        _inner.EnablePublisherAsync(publisherName, actor, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(
        string publisherName, string token, TimeSpan leaseDuration, int limit, CancellationToken cancellationToken = default) =>
        _inner.ClaimDeliveriesAsync(publisherName, token, leaseDuration, limit);

    public Task<bool> RenewDeliveryLeaseAsync(
        string eventId, string publisherName, string token, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
        _inner.RenewDeliveryLeaseAsync(eventId, publisherName, token, leaseDuration);

    public Task<bool> SettleDeliveryAsync(
        string eventId, string publisherName, string token, OutboxPublishResult result, CancellationToken cancellationToken = default) =>
        _inner.SettleDeliveryAsync(eventId, publisherName, token, result);

    public Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName, CancellationToken cancellationToken = default) =>
        _inner.GetDeliveryAsync(eventId, publisherName, cancellationToken: cancellationToken);

    public Task<OutboxPublisherState?> GetPublisherAsync(string publisherName, CancellationToken cancellationToken = default) =>
        _inner.GetPublisherAsync(publisherName, cancellationToken: cancellationToken);
}
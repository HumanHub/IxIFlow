using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// A profile repository with injected faults: a commit that fails before it is applied, as a host
/// dying would, or one that is applied but whose acknowledgement is lost, once.
/// </summary>
internal sealed class FaultRepository(IWorkflowStateRepository inner) : IWorkflowStateRepository
{
    public Task<CatalogPage<CatalogWorkflow>> ListWorkflowsAsync(CatalogWorkflowQuery query) => inner.ListWorkflowsAsync(query);
    public Task<CatalogPage<CatalogWorkflowVersion>> ListWorkflowVersionsAsync(string name, CatalogVersionQuery query) => inner.ListWorkflowVersionsAsync(name, query);
    public Task<CatalogWorkflowVersionDetail?> GetWorkflowVersionDetailAsync(string name, int version, CatalogScope scope) => inner.GetWorkflowVersionDetailAsync(name, version, scope);
    public Task<CatalogPage<CatalogActivity>> ListActivitiesAsync(CatalogActivityQuery query) => inner.ListActivitiesAsync(query);
    public Task<CatalogPage<CatalogActivity>> ListActivityVersionsAsync(string key, CatalogVersionQuery query) => inner.ListActivityVersionsAsync(key, query);
    public Task<CatalogActivityDetail?> GetActivityAsync(string key, string version, ActivityProviderSelector provider, CatalogScope scope) => inner.GetActivityAsync(key, version, provider, scope);
    public Task<CatalogPage<CatalogPackage>> ListPackagesAsync(CatalogPackageQuery query) => inner.ListPackagesAsync(query);
    public Task<CatalogPage<CatalogPackage>> ListPackageVersionsAsync(string id, CatalogVersionQuery query) => inner.ListPackageVersionsAsync(id, query);
    public Task<CatalogPackageDetail?> GetPackageDetailAsync(string id, string version, CatalogScope scope) => inner.GetPackageDetailAsync(id, version, scope);
    public Task<CatalogPage<CatalogActivity>> GetPackageActivitiesAsync(string id, string version, CatalogVersionQuery query) => inner.GetPackageActivitiesAsync(id, version, query);
    public Task<IReadOnlyList<WorkflowPackage>> GetPendingPackageDependentsAsync(string missingPackageId, string? afterIdentity, int limit) => inner.GetPendingPackageDependentsAsync(missingPackageId, afterIdentity, limit);
    public Task<IxIFlow.Templates.TemplateVersionEntry> RegisterTemplateVersionAsync(IxIFlow.Templates.TemplateVersion version, IArtifactStore artifacts, string? actor = null) => inner.RegisterTemplateVersionAsync(version, artifacts, actor);
    public Task<IxIFlow.Templates.TemplateVersionEntry?> GetTemplateVersionAsync(string key, int version) => inner.GetTemplateVersionAsync(key, version);
    public Task<IReadOnlyList<IxIFlow.Templates.TemplateVersionEntry>> GetTemplateVersionsAsync(string? key = null) => inner.GetTemplateVersionsAsync(key);
    public Task<IxIFlow.Templates.TemplateVersionEntry> PublishTemplateDefinitionAsync(WorkflowDraftId draftId, IxIFlow.Templates.TemplateVersion version, IArtifactStore artifacts, string? actor = null) => inner.PublishTemplateDefinitionAsync(draftId, version, artifacts, actor);
    public Task<PackageHolders> GetPackageHoldersAsync(string id, string version, CatalogScope scope) => inner.GetPackageHoldersAsync(id, version, scope);
    public Task<PackageRegistration> PublishPackageDefinitionAsync(WorkflowDraftId draftId, WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => inner.PublishPackageDefinitionAsync(draftId, package, activities, artifacts, actor);
    public Task<WorkflowPackage> SetPackageWithdrawnAsync(string id, string version, bool withdrawn, CatalogMutationToken expected, string? actor = null) => inner.SetPackageWithdrawnAsync(id, version, withdrawn, expected, actor);
    public Task<PackageRegistration> RegisterPackageAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => inner.RegisterPackageAsync(package, activities, artifacts, actor);
    public Task<PackageRegistration> CompletePackageExtractionAsync(string id, string version, ArtifactDigest expectedDigest, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => inner.CompletePackageExtractionAsync(id, version, expectedDigest, activities, artifacts, actor);
    public IArtifactStore? CatalogArtifacts { get => inner.CatalogArtifacts; set => inner.CatalogArtifacts = value; }
    public Task<IReadOnlyList<WorkflowPackage>> GetPackageExtractionPageAsync(PackageExtraction extraction, string? afterIdentity, int limit) => inner.GetPackageExtractionPageAsync(extraction, afterIdentity, limit);
    public Task<IReadOnlyList<InstalledActivityPackage>> GetInstalledPackagesAsync(string? id = null) => inner.GetInstalledPackagesAsync(id);
    public Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null) => inner.GetPackagesAsync(id);
    public Task<WorkflowPackage?> GetPackageAsync(string id, string version) => inner.GetPackageAsync(id, version);
    public Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit) => inner.GetPackagePageAsync(id, afterVersion, limit);
    public IPayloadStore PayloadStore => inner.PayloadStore;
    public Task<bool> DeleteDefinitionAsync(WorkflowDraftId expected) => inner.DeleteDefinitionAsync(expected);
    public Task<CatalogPage<WorkflowDraftSummary>> GetDefinitionsAsync(WorkflowDraftQuery query) => inner.GetDefinitionsAsync(query);
    public Task<RetirementBlockers> GetRetirementBlockersAsync(string name, int version, CatalogScope scope) => inner.GetRetirementBlockersAsync(name, version, scope);
    public Task<WorkflowPublication> BeginPublicationAsync(WorkflowDraftId draftId, Guid hostId, string? actor, TimeSpan staleAfter) => inner.BeginPublicationAsync(draftId, hostId, actor, staleAfter);
    public Task<bool> HeartbeatPublicationAsync(Guid id, Guid hostId) => inner.HeartbeatPublicationAsync(id, hostId);
    public Task<WorkflowPublication> CompletePublicationAsync(Guid id, Guid hostId, WorkflowPublicationOutcome outcome) => inner.CompletePublicationAsync(id, hostId, outcome);
    public Task<WorkflowPublication?> GetPublicationAsync(Guid id, CatalogScope scope) => inner.GetPublicationAsync(id, scope);
    public Task<CatalogPage<WorkflowPublication>> GetPublicationsAsync(WorkflowPublicationQuery query) => inner.GetPublicationsAsync(query);
    public Task<int> PurgePublicationsAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) => inner.PurgePublicationsAsync(retention, batchSize, cancellationToken);
    public Task<WorkflowDraft?> GetDefinitionAsync(string kind, string name, int version) =>
        inner.GetDefinitionAsync(kind, name, version);
    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, WorkflowDraftId? expected, string? actor = null) =>
        inner.SaveDefinitionAsync(definition, expected, actor);
    public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null) =>
        inner.RegisterWorkflowVersionAsync(version, artifacts);
    public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) =>
        inner.GetWorkflowVersionAsync(name, version);
    public Task<long> GetCatalogRevisionAsync() => inner.GetCatalogRevisionAsync();
    public Task<WorkflowCatalogChanges> GetWorkflowVersionChangesAsync(long afterRevision) => inner.GetWorkflowVersionChangesAsync(afterRevision);
    public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) =>
        inner.GetWorkflowVersionsAsync(name);
    public Task<WorkflowVersion> SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, CatalogMutationToken expected, IArtifactStore? artifacts = null, string? actor = null) =>
        inner.SetWorkflowVersionStateAsync(name, version, state, expected, artifacts, actor);
    public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore artifacts, string? actor = null) =>
        inner.PublishDefinitionAsync(draftId, version, artifacts, actor);
    public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) =>
        inner.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);
    public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) =>
        inner.PutAsync(kind, content, cancellationToken);
    public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        inner.OpenAsync(digest, cancellationToken);
    public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        inner.ExistsAsync(digest, cancellationToken);
    public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) =>
        inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(digest, cancellationToken);

    public Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default) =>
        inner.RegisterMemberAsync(member, cancellationToken);
    public Task<bool> HeartbeatAsync(Guid memberId, IReadOnlyDictionary<string, string> capabilities,
        CancellationToken cancellationToken = default) =>
        inner.HeartbeatAsync(memberId, capabilities, cancellationToken);
    public Task UnregisterMemberAsync(Guid memberId, CancellationToken cancellationToken = default) =>
        inner.UnregisterMemberAsync(memberId, cancellationToken);
    public Task<IReadOnlyList<WorkflowDemand>> GetWorkflowDemandsAsync(string instanceId) =>
        inner.GetWorkflowDemandsAsync(instanceId);

    public Task<UnschedulablePage> GetUnschedulableAsync(int limit = 100, UnschedulableCursor? cursor = null,
        CancellationToken cancellationToken = default) =>
        inner.GetUnschedulableAsync(limit, cursor, cancellationToken);
    public Task<bool> CanScheduleAsync(IReadOnlyList<WorkflowDemand> demands, CancellationToken cancellationToken = default) =>
        inner.CanScheduleAsync(demands, cancellationToken);

    public Task<IReadOnlyList<StoreMember>> GetLiveMembersAsync(CancellationToken cancellationToken = default) =>
        inner.GetLiveMembersAsync(cancellationToken);

    public Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention, int batchSize,
        CancellationToken cancellationToken = default) =>
        inner.PurgeAsync(finishedRetention, tombstoneRetention, batchSize, cancellationToken);
    public Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId) =>
        inner.GetTombstoneAsync(instanceId);
    public Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission) =>
        inner.AdmitWorkflowInstanceAsync(commit, admission);
    public Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId) =>
        inner.GetWorkflowOwnershipAsync(instanceId);
    public Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key) =>
        inner.GetWorkflowAdmissionAsync(sender, trigger, key);

    public Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false) =>
        inner.RegisterPublisherAsync(name, eventTypes, replayRetained);
    public Task UnregisterPublisherAsync(string name, string actor = "host") => inner.UnregisterPublisherAsync(name, actor);
    public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query) => inner.ListDeliveriesAsync(query);
    public Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor) => inner.RetryDeliveryAsync(eventId, publisherName, actor);
    public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync() => inner.ListPublishersAsync();
    public Task<bool> EnablePublisherAsync(string publisherName, string actor) => inner.EnablePublisherAsync(publisherName, actor);
    public Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(string publisherName, string token, TimeSpan leaseDuration, int limit) =>
        inner.ClaimDeliveriesAsync(publisherName, token, leaseDuration, limit);
    public Task<bool> RenewDeliveryLeaseAsync(string eventId, string publisherName, string token, TimeSpan leaseDuration) =>
        inner.RenewDeliveryLeaseAsync(eventId, publisherName, token, leaseDuration);
    public Task<bool> SettleDeliveryAsync(string eventId, string publisherName, string token, OutboxPublishResult result) =>
        inner.SettleDeliveryAsync(eventId, publisherName, token, result);
    public Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName) =>
        inner.GetDeliveryAsync(eventId, publisherName);
    public Task<OutboxPublisherState?> GetPublisherAsync(string publisherName) =>
        inner.GetPublisherAsync(publisherName);

    public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent) =>
        inner.AppendEventAsync(workflowEvent);
    public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit) =>
        inner.GetPendingEventsAsync(instanceId, afterSequence, limit);
    public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId) =>
        inner.GetEventAsync(instanceId, eventId);
    public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId) =>
        inner.GetWaitAsync(instanceId, waitId);
    public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(
        IReadOnlyCollection<string> instanceIds) =>
        inner.GetInboxSequencesAsync(instanceIds);
    public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query) =>
        inner.GetOpenWaitsAsync(query);

    private int _acknowledgementsLost;

    /// <summary>Commits matching this fail without being applied.</summary>
    public Func<WorkflowInstance, bool> FailCommit { get; set; } = _ => false;

    /// <summary>The first commit matching this is applied, then reported as failed.</summary>
    public Func<WorkflowInstance, bool> LoseAcknowledgement { get; init; } = _ => false;

    public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
    {
        // The whole instance the commit saves: the commit applied to the instance it was based on.
        var instance = commit.ReadInstance(commit.ReplacesParts ? null : await inner.GetWorkflowInstanceAsync(commit.InstanceId));
        if (FailCommit(instance))
            throw new IOException($"Injected commit failure for '{instance.InstanceId}'");
        var result = await inner.CommitWorkflowInstanceAsync(commit);
        if (result.Status == WorkflowCommitStatus.Applied && LoseAcknowledgement(instance) &&
            Interlocked.Exchange(ref _acknowledgementsLost, 1) == 0)
            throw new IOException($"Injected lost acknowledgement for '{instance.InstanceId}'");
        return result;
    }

    public Task<CancellationRequestResult> RequestCancellationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null) =>
        inner.RequestCancellationAsync(instanceId, reason, expectedIncarnation);
    public Task<CancellationReason?> GetCancellationRequestAsync(string instanceId) =>
        inner.GetCancellationRequestAsync(instanceId);
    public Task<WorkflowControlRequests> GetControlRequestsAsync(IReadOnlyCollection<string> instanceIds) =>
        inner.GetControlRequestsAsync(instanceIds);
    public Task<DebugCommandResult> RequestDebugCommandAsync(string instanceId, DebugCommandRequest command) =>
        inner.RequestDebugCommandAsync(instanceId, command);
    public Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> instanceIds) =>
        inner.GetDebugStatesAsync(instanceIds);
    public Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(ClaimQuery query) =>
        inner.GetRunnableInstancesAsync(query);
    public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration, ClaimQuery query, ExecutionClaimKind kind = ExecutionClaimKind.Runnable) => inner.TryAcquireExecutionLeaseAsync(instanceId, token, duration, query, kind);
    public Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(IReadOnlyList<ExecutionLeaseRenewal> leases) =>
        inner.RenewExecutionLeasesAsync(leases);
    public Task<ExecutionLeaseReleaseResult> ReleaseExecutionLeaseAsync(string instanceId, string token) =>
        inner.ReleaseExecutionLeaseAsync(instanceId, token);

    public Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId) =>
        inner.GetWorkflowInstanceSummaryAsync(instanceId);

    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId) =>
        inner.GetWorkflowInstanceAsync(instanceId);
    public Task<InstancePage> QueryInstancesAsync(InstanceQuery query) =>
        inner.QueryInstancesAsync(query);

    public Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null) =>
        inner.RequestTerminationAsync(instanceId, reason, expectedIncarnation);
    public Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsForIncarnationAsync(string instanceId, Guid incarnation) =>
        inner.GetDiagnosticsForIncarnationAsync(instanceId, incarnation);
    public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
        int limit = 1000) =>
        inner.GetTraceAsync(instanceId, afterSequence, limit);
    public Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null) => inner.DeleteWorkflowInstanceAsync(instanceId, tombstoneRetention);
}

using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>Operator access on the host pipeline. Applications enforce authorization at their boundary.</summary>
public interface IWorkflowOperations
{
    /// <summary>Due, unleased work that no live host can run. Applications control access.</summary>
    Task<UnschedulablePage> GetUnschedulableAsync(int limit = 100, UnschedulableCursor? cursor = null,
        CancellationToken cancellationToken = default);
    Task<WorkflowOperation> SubmitResolutionAsync(string instanceId, string invocationId, ActivityResolution resolution, Guid? expectedIncarnation = null);
    Task<WorkflowOperation> SubmitReleaseAsync(string instanceId, string actor, Guid? expectedIncarnation = null);
    Task<WorkflowOperation> SubmitCancellationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null);
    Task<WorkflowOperation> SubmitTerminationAsync(string instanceId, string reasonCode, string? description, string actor, Guid? expectedIncarnation = null);
    Task<DeliverySummary?> GetDeliveryAsync(string eventId, string publisherName, string actor, CancellationToken cancellationToken = default);
    Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query, string actor, CancellationToken cancellationToken = default);
    Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync(string actor, CancellationToken cancellationToken = default);
    Task<bool> EnablePublisherAsync(string publisherName, string actor, CancellationToken cancellationToken = default);
    Task UnregisterPublisherAsync(string publisherName, string actor, CancellationToken cancellationToken = default);
    Task<InstancePage> SearchAsync(InstanceQuery query, CancellationToken cancellationToken = default);
    Task<InstanceSummary?> GetAsync(string instanceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingWait>> GetWaitsAsync(IReadOnlyCollection<string> instanceIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0, int limit = 1000,
        CancellationToken cancellationToken = default);
    Task<InstanceSummary> TerminateAsync(string instanceId, string reasonCode, string? description = null,
        string? actor = null, CancellationToken cancellationToken = default);
}

internal sealed partial class HostedWorkflowOperations(WorkflowHost host, TimeProvider time,
    IWorkflowCatalog catalog, IWorkflowStateRepository repository, CoordinationState coordination) : IWorkflowOperations
{
    public Task<UnschedulablePage> GetUnschedulableAsync(int limit = 100, UnschedulableCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        UnschedulableWork.Validate(limit, cursor);
        return host.ReadAsync(client => client.GetUnschedulableAsync(limit, cursor, cancellationToken));
    }

    public Task<DeliverySummary?> GetDeliveryAsync(string eventId, string publisherName, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherName);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        cancellationToken.ThrowIfCancellationRequested();
        return host.ReadAsync(client => client.GetDeliveryAsync(eventId, publisherName));
    }

    public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        cancellationToken.ThrowIfCancellationRequested();
        return host.ReadAsync(client => client.ListDeliveriesAsync(query));
    }

    public Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        cancellationToken.ThrowIfCancellationRequested();
        return host.ReadAsync(client => client.RetryDeliveryAsync(eventId, publisherName, actor));
    }

    public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync(string actor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        cancellationToken.ThrowIfCancellationRequested();
        return host.ReadAsync(client => client.ListPublishersAsync());
    }

    public Task<bool> EnablePublisherAsync(string publisherName, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        cancellationToken.ThrowIfCancellationRequested();
        return host.ReadAsync(client => client.EnablePublisherAsync(publisherName, actor));
    }

    public Task UnregisterPublisherAsync(string publisherName, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        cancellationToken.ThrowIfCancellationRequested();
        return host.ReadAsync(async client => { await client.UnregisterPublisherAsync(publisherName, actor); return true; });
    }

    public Task<InstancePage> SearchAsync(InstanceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        if (query.Limit > 1000) throw new ArgumentOutOfRangeException(nameof(query), "Operations pages contain at most 1000 instances");
        cancellationToken.ThrowIfCancellationRequested();
        return host.ReadAsync(client => client.SearchInstancesAsync(query));
    }

    public async Task<InstanceSummary?> GetAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return (await SearchAsync(new InstanceQuery(InstanceId: instanceId, Limit: 1), cancellationToken)).Items.SingleOrDefault();
    }

    public Task<IReadOnlyList<PendingWait>> GetWaitsAsync(IReadOnlyCollection<string> instanceIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        if (instanceIds.Count > 1000) throw new ArgumentException("At most 1000 instance IDs", nameof(instanceIds));
        cancellationToken.ThrowIfCancellationRequested();
        return host.ReadAsync(async client => (IReadOnlyList<PendingWait>)(await client.GetOpenWaitsAsync(
            new WaitQuery(Kind: WaitKind.Event, Limit: int.MaxValue, InstanceIds: instanceIds), cancellationToken))
            .Select(wait => new PendingWait(wait.InstanceId, wait.Key, wait.EventType, wait.DefaultPayloadJson,
                wait.DeadlineUtc, wait.OpenedAtUtc)).ToArray());
    }

    public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0, int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        cancellationToken.ThrowIfCancellationRequested();
        return host.ReadAsync(client => client.GetTracePageAsync(instanceId, afterSequence, limit));
    }

    public async Task<InstanceSummary> TerminateAsync(string instanceId, string reasonCode, string? description = null,
        string? actor = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        cancellationToken.ThrowIfCancellationRequested();
        var incarnation = await IncarnationAsync(instanceId, null);
        var accepted = await host.ReadAsync(client => client.RequestTerminationAsync(instanceId, reasonCode, description, actor, incarnation));
        if (!accepted.Accepted)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Termination lost: workflow instance '{instanceId}' is absent or already finished");
        return await AwaitTerminationAsync(instanceId, accepted.Incarnation, cancellationToken);
    }

    private async Task<InstanceSummary> AwaitTerminationAsync(string instanceId, Guid incarnation, CancellationToken cancellationToken)
    {
        // The caller only cancels this wait. The accepted command survives it and recovery completes it.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await host.RunAsync(null, (client, _, _) => client.FinishTerminationAsync(instanceId, incarnation), cancellationToken);
            var current = await GetAsync(instanceId, cancellationToken)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The terminated instance was removed");
            if (current.Incarnation != incarnation)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The instance was removed and its ID reused");
            if (current.Status == WorkflowStatus.Terminated) return current;
            if (WorkflowStoreRules.IsTerminal(current.Status))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, $"Termination lost to {current.Status}");
            await Task.Delay(TimeSpan.FromMilliseconds(100), time, cancellationToken);
        }
    }
}

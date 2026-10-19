using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>Operator access on the host pipeline. Applications enforce authorization at their boundary.</summary>
public interface IWorkflowOperations
{
    Task<InstancePage> SearchAsync(InstanceQuery query, CancellationToken cancellationToken = default);
    Task<InstanceSummary?> GetAsync(string instanceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingWait>> GetWaitsAsync(IReadOnlyCollection<string> instanceIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0, int limit = 1000,
        CancellationToken cancellationToken = default);
    Task<InstanceSummary> TerminateAsync(string instanceId, string reasonCode, string? description = null,
        string? actor = null, CancellationToken cancellationToken = default);
}

internal sealed class HostedWorkflowOperations(WorkflowHost host, TimeProvider time) : IWorkflowOperations
{
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
            new WaitQuery(Kind: WaitKind.Event, Limit: int.MaxValue, InstanceIds: instanceIds)))
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
        var accepted = await host.ReadAsync(client => client.RequestTerminationAsync(instanceId, reasonCode, description, actor));
        if (!accepted.Accepted)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Termination lost: workflow instance '{instanceId}' is absent or already finished");
        // The caller only cancels this wait. The accepted command survives it and recovery completes it.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await host.ReadAsync(client => client.FinishTerminationAsync(instanceId, accepted.Incarnation));
            var current = await GetAsync(instanceId, cancellationToken)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The terminated instance was removed");
            if (current.Incarnation != accepted.Incarnation)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The instance was removed and its ID reused");
            if (current.Status == WorkflowStatus.Terminated) return current;
            if (WorkflowStoreRules.IsTerminal(current.Status))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, $"Termination lost to {current.Status}");
            await Task.Delay(TimeSpan.FromMilliseconds(100), time, cancellationToken);
        }
    }
}

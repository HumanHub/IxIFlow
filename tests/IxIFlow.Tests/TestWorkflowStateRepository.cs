using IxIFlow.Core;

namespace IxIFlow.Tests;

/// <summary>Test-side repository adapter for interface-based fault injection.</summary>
public class TestWorkflowStateRepository : InMemoryWorkflowStateRepository, IWorkflowStateRepository
{
    public TestWorkflowStateRepository()
    {
    }

    public TestWorkflowStateRepository(TimeProvider time, OutboxSettings? outboxSettings = null,
        PayloadStoreSettings? payloadSettings = null) : base(time, outboxSettings, payloadSettings)
    {
    }

    private IPayloadStore? _originalPayloadStore;
    public new virtual IPayloadStore PayloadStore => _originalPayloadStore ??= base.PayloadStore;

    public new virtual async Task<WorkflowControlRequests> GetControlRequestsAsync(IReadOnlyCollection<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        InstanceIds.ValidateAll(instanceIds);
        var ids = instanceIds.Distinct(StringComparer.Ordinal).ToArray();
        var cancellations = new Dictionary<string, CancellationReason>(StringComparer.Ordinal);
        var failures = new List<Exception>();

        foreach (var id in ids)
        {
            try
            {
                if (await GetCancellationRequestAsync(id) is { } reason)
                    cancellations[id] = reason;
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
        }

        if (ids.Length > 0 && failures.Count == ids.Length)
            throw new AggregateException("No cancellation request could be read", failures);

        return new(cancellations, await GetDebugStatesAsync(ids));
    }

    public new virtual Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> instanceIds) =>
        base.GetDebugStatesAsync(instanceIds);

    public new virtual Task<CancellationReason?> GetCancellationRequestAsync(string instanceId,
        CancellationToken cancellationToken = default) =>
        base.GetCancellationRequestAsync(instanceId, cancellationToken);

    public new virtual Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(
        IReadOnlyList<ExecutionLeaseRenewal> leases, CancellationToken cancellationToken = default) =>
        base.RenewExecutionLeasesAsync(leases, cancellationToken);

    public new virtual Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit,
        CancellationToken cancellationToken = default)
    {
        if (commit.PayloadReferences.Count > 0 || commit.PayloadBodies.Count > 0 ||
            commit.ChildStarts.Any(child => child.PayloadReferences.Count > 0))
        {
            _originalPayloadStore ??= base.PayloadStore;
            _payloadStore = PayloadStore;
        }

        return base.CommitWorkflowInstanceAsync(commit, cancellationToken);
    }
}
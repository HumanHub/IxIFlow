using System.Text.Json;

namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    // The terminal parent commit holds _claimLock. A finished child may have left detached work
    // after the host discovered it; follow its retained relation before acknowledging the parent.
    private void TerminateDescendant(string instanceId, CancellationReason reason, DateTime now,
        ICollection<string> madeRunnable, HashSet<string> visited)
    {
        if (!visited.Add(instanceId) || !_instances.TryGetValue(instanceId, out var instance)) return;
        if (WorkflowStoreRules.IsTerminal(instance.Status))
        {
            foreach (var child in _instances.Values.Where(child => child.ParentInstanceId == instanceId).ToArray())
                TerminateDescendant(child.InstanceId, reason, now, madeRunnable, visited);
        }
        else if (RequestCancellation(instanceId, reason, now)) madeRunnable.Add(instanceId);
    }

    public Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null)
    {
        InstanceIds.Validate(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        lock (_claimLock)
        {
            var now = Now;
            var command = JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(reason))!;
            command.Mode = WorkflowControlMode.Terminate;
            var accepted = (expectedIncarnation == null ||
                _instances.GetValueOrDefault(instanceId)?.Incarnation == expectedIncarnation) &&
                RequestCancellation(instanceId, command, now);
            return Task.FromResult(new WorkflowControlReceipt(accepted,
                accepted ? _cancellations[instanceId].CommandVersion : 0,
                _instances.GetValueOrDefault(instanceId)?.Incarnation ?? Guid.Empty)
            { Notice = accepted && Notice(instanceId, now) is { Claimable: true } notice ? notice : null });
        }
    }
}

using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>Turns a running instance into the saved form that the host commits.</summary>
internal static class ExecutionCheckpointWriter
{
    public static async Task CommitAsync(IExecutionHost host, WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, object workflowData, WorkflowScopeCatalog scopes, DateTime now,
        bool preserveSavedWorkflowData = false)
    {
        if (!host.PersistsState)
            return;
        Prepare(instance, checkpoint, workflowData, scopes, now, preserveSavedWorkflowData);
        await host.CommitAsync(instance);
    }

    public static void Prepare(WorkflowInstance instance, ExecutionCheckpoint checkpoint,
        object workflowData, WorkflowScopeCatalog scopes, DateTime now, bool preserveSavedWorkflowData = false)
    {
        try
        {
            ContinuationTransitions.PrepareCheckpoint(checkpoint, scopes);
            var dataJson = preserveSavedWorkflowData
                ? instance.WorkflowDataJson
                : JsonSerializer.Serialize(workflowData, workflowData.GetType());
            var stateJson = JsonSerializer.Serialize(checkpoint);
            var snapshot = ExecutionSnapshotFactory.Create(instance, checkpoint, scopes, now);
            instance.WorkflowDataJson = dataJson;
            instance.ExecutionStateJson = stateJson;
            instance.ExecutionSnapshot = snapshot;
            instance.NextDueAtUtc = instance.Status is WorkflowStatus.Running or
                    WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution
                ? new[] { checkpoint.CancellationRequested ? null : checkpoint.ExecutionDeadlineUtc }
                    .Concat(checkpoint.Waits.Select(wait => wait.DeadlineUtc))
                    .Concat(checkpoint.Waits.Any(wait => wait.ChildWorkflowId != null)
                        ? [now.AddSeconds(5)] : [])
                    .Where(due => due != null).Min()
                : null;
        }
        catch (Exception ex)
        {
            throw new CheckpointDataException(ex);
        }
    }

    public sealed class CheckpointDataException(Exception inner) : Exception(
        $"The workflow state could not be checkpointed: {inner.Message}", inner);
}

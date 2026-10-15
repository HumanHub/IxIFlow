using System.Text;
using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Turns a running instance into its saved form and sends it to the host as a fenced commit.
/// </summary>
internal static class ExecutionCheckpointWriter
{
    private const int MaxAttempts = 3;

    public static async Task CommitAsync(IExecutionHost host, WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, object workflowData, WorkflowScopeCatalog scopes, DateTime now,
        bool preserveSavedWorkflowData = false)
    {
        if (!host.PersistsState)
            return;
        var lastCommitted = SavedForm.Of(instance);
        Prepare(instance, checkpoint, workflowData, scopes, now, preserveSavedWorkflowData);
        var size = CheckpointBytes(instance);
        if (size > host.MaxCheckpointBytes)
        {
            // The instance keeps the last checkpoint that was committed.
            lastCommitted.RestoreTo(instance);
            throw new CheckpointLimitExceededException(size, host.MaxCheckpointBytes);
        }

        var commit = CheckpointCommit.Create(instance, host.Epoch, instance.Revision);
        for (var attempt = 1; ; attempt++)
        {
            var reply = await host.CommitAsync(commit);
            switch (reply.Kind)
            {
                case CheckpointReplyKind.Acknowledged:
                    instance.Revision = reply.Revision;
                    return;
                case CheckpointReplyKind.Conflict:
                    throw new ExecutionOwnershipLostException(reply.Error!);
                case CheckpointReplyKind.Retry or CheckpointReplyKind.Unknown when attempt < MaxAttempts:
                    // The commit is immutable, so sending it again cannot apply a different payload.
                    continue;
                default:
                    throw reply.Error!;
            }
        }
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
            // An instance stopped at a limit stays put until an operator acts on it.
            instance.NextDueAtUtc = checkpoint.ResourceStop == null &&
                    instance.Status is WorkflowStatus.Running or WorkflowStatus.Suspended or
                        WorkflowStatus.NeedsResolution
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

    /// <summary>The size a checkpoint limit applies to: the workflow data and the execution state.</summary>
    public static long CheckpointBytes(WorkflowInstance instance) =>
        Encoding.UTF8.GetByteCount(instance.WorkflowDataJson) +
        Encoding.UTF8.GetByteCount(instance.ExecutionStateJson);

    public sealed class CheckpointDataException(Exception inner) : Exception(
        $"The workflow state could not be checkpointed: {inner.Message}", inner);

    private sealed record SavedForm(string WorkflowDataJson, string ExecutionStateJson,
        WorkflowExecutionSnapshot? ExecutionSnapshot, DateTime? NextDueAtUtc)
    {
        public static SavedForm Of(WorkflowInstance instance) => new(instance.WorkflowDataJson,
            instance.ExecutionStateJson, instance.ExecutionSnapshot, instance.NextDueAtUtc);

        public void RestoreTo(WorkflowInstance instance)
        {
            instance.WorkflowDataJson = WorkflowDataJson;
            instance.ExecutionStateJson = ExecutionStateJson;
            instance.ExecutionSnapshot = ExecutionSnapshot;
            instance.NextDueAtUtc = NextDueAtUtc;
        }
    }
}

/// <summary>A checkpoint would exceed the configured size limit and was not committed (D-28).</summary>
internal sealed class CheckpointLimitExceededException(long size, long limit) : Exception(
    $"The checkpoint is {size} bytes, over the configured limit of {limit} bytes");

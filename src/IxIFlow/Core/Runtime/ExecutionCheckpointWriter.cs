using System.Text;
using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Turns a running instance into its saved form and sends it to the host as a fenced commit.
/// </summary>
internal static class ExecutionCheckpointWriter
{
    private const int MaxAttempts = 3;

    /// <summary>How often an idle parent checks on its children when nothing else wakes it.</summary>
    public static readonly TimeSpan ChildCheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Commits the run's state. <paramref name="workflowData"/> may be null only with
    /// <paramref name="preserveSavedWorkflowData"/>. <paramref name="clearsCrashCount"/> is false for a
    /// commit that records no progress: claiming an activity or child before it starts, or only
    /// rescheduling a check (B-08). With <paramref name="parkFor"/> the run stops that long and the
    /// store releases its lease (C-03); returns true when it did. <paramref name="now"/> is the host's time.
    /// </summary>
    public static async Task<bool> CommitAsync(IExecutionHost host, WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, object? workflowData, WorkflowScopeCatalog scopes, DateTime now,
        bool preserveSavedWorkflowData = false, bool enforceLimit = true, bool clearsCrashCount = true,
        TimeSpan? parkFor = null)
    {
        if (!host.PersistsState)
            return false;
        var lastCommitted = SavedForm.Of(instance);
        checkpoint.CloseEndedRequests();
        Prepare(instance, checkpoint, workflowData, scopes, now, preserveSavedWorkflowData);
        var size = CheckpointBytes(instance);
        if (enforceLimit && size > host.Limits.MaxCheckpointBytes)
        {
            // The instance keeps the last checkpoint that was committed.
            lastCommitted.RestoreTo(instance);
            throw new CheckpointLimitExceededException(size, host.Limits.MaxCheckpointBytes);
        }

        // The commit carries the run's inbox decisions and the waits it opened, armed and closed (D-33).
        // It says how long, not until when: the store stamps deadlines and the runnable time (D-35).
        var (opened, armed, closed) = checkpoint.Inbox.WaitChanges(checkpoint);
        var idle = instance.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution;
        var checksChildren = idle && checkpoint.Waits.Any(wait => wait.ChildWorkflowId != null);
        var commit = CheckpointCommit.Create(instance, host.Epoch, instance.Revision,
            clearsCrashCount: clearsCrashCount, parks: parkFor != null,
            settlements: checkpoint.Inbox.Settlements, inboxReadThrough: checkpoint.Inbox.ReadThrough,
            openedWaits: opened, closedWaits: closed, armedWaits: armed,
            wakeAfter: parkFor ?? (checksChildren ? ChildCheckInterval : null),
            stopped: checkpoint.OperatorStop != null);
        for (var attempt = 1; ; attempt++)
        {
            var reply = await host.CommitAsync(commit);
            switch (reply.Kind)
            {
                case CheckpointReplyKind.Acknowledged:
                    instance.Revision = reply.Revision;
                    checkpoint.Inbox.Acknowledged(checkpoint, reply.UnreadEvents);
                    EstimateDeadlines(checkpoint, opened, armed, now);
                    // The host looks again when the store will next make the instance runnable; an idle
                    // instance with arrivals it has not read is due now, and the store keeps it so.
                    checkpoint.LocalWakeUtc =
                        reply.UnreadEvents && instance.Status == WorkflowStatus.Suspended ? now
                        : checkpoint.OperatorStop != null ? null
                        : instance.Status == WorkflowStatus.Running ? now + parkFor ?? now
                        : idle ? new[] { checkpoint.NextDeadlineLocal(), now + commit.WakeAfter }.Min()
                        : null;
                    if (reply.PendingCancellation != null)
                        host.ObserveCancellation(reply.PendingCancellation);
                    return reply.Parked;
                // Only the owner settles its pending events, so a settled one means another owner ran.
                case CheckpointReplyKind.Conflict or CheckpointReplyKind.EventsChanged:
                    throw new ExecutionOwnershipLostException(reply.Error!);
                case CheckpointReplyKind.Retry or CheckpointReplyKind.Unknown when attempt < MaxAttempts:
                    // The commit is immutable, so sending it again cannot apply a different payload.
                    continue;
                default:
                    throw reply.Error!;
            }
        }
    }

    // Until the run reads the deadlines the store stamped, it takes them to be its commit's time on the
    // store's clock plus each timeout (D-35).
    private static void EstimateDeadlines(ExecutionCheckpoint checkpoint, IReadOnlyList<OpenedWait> opened,
        IReadOnlyList<ArmedWait> armed, DateTime now)
    {
        var storeNow = checkpoint.Clock.ToStore(now);
        var timeouts = opened.Where(wait => wait.Timeout != null)
            .Select(wait => (wait.WaitId, Timeout: wait.Timeout!.Value))
            .Concat(armed.Select(wait => (wait.WaitId, wait.Timeout)))
            .ToDictionary(item => item.WaitId, item => item.Timeout, StringComparer.Ordinal);
        if (timeouts.Count == 0)
            return;
        foreach (var wait in checkpoint.Waits)
            if (timeouts.TryGetValue(wait.Id, out var timeout))
                wait.DeadlineUtc = storeNow + timeout;
        if (checkpoint.ExecutionTimerId is { } timer && timeouts.TryGetValue(timer, out var execution))
            checkpoint.ExecutionDeadlineUtc = storeNow + execution;
    }

    public static void Prepare(WorkflowInstance instance, ExecutionCheckpoint checkpoint,
        object? workflowData, WorkflowScopeCatalog scopes, DateTime now, bool preserveSavedWorkflowData = false)
    {
        try
        {
            ContinuationTransitions.PrepareCheckpoint(checkpoint, scopes);
            checkpoint.RecordBranchChanges();
            var dataJson = preserveSavedWorkflowData
                ? instance.WorkflowDataJson
                : JsonSerializer.Serialize(workflowData ?? throw new ArgumentNullException(nameof(workflowData)),
                    workflowData.GetType());
            var stateJson = JsonSerializer.Serialize(checkpoint);
            var snapshot = ExecutionSnapshotFactory.Create(instance, checkpoint, scopes, now);
            instance.WorkflowDataJson = dataJson;
            instance.ExecutionStateJson = stateJson;
            instance.ExecutionSnapshot = snapshot;
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
        WorkflowExecutionSnapshot? ExecutionSnapshot)
    {
        public static SavedForm Of(WorkflowInstance instance) => new(instance.WorkflowDataJson,
            instance.ExecutionStateJson, instance.ExecutionSnapshot);

        public void RestoreTo(WorkflowInstance instance)
        {
            instance.WorkflowDataJson = WorkflowDataJson;
            instance.ExecutionStateJson = ExecutionStateJson;
            instance.ExecutionSnapshot = ExecutionSnapshot;
        }
    }
}

/// <summary>A checkpoint would exceed the configured size limit and was not committed (D-28).</summary>
internal sealed class CheckpointLimitExceededException(long size, long limit) : ResourceLimitExceededException(
    $"The checkpoint is {size} bytes, over the configured limit of {limit} bytes");

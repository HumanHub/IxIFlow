using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Turns a running instance into its saved form and sends it to the host as a fenced commit.
/// </summary>
internal static class ExecutionCheckpointWriter
{
    private const int MaxAttempts = 3;

    /// <summary>
    /// Commits the run's state. <paramref name="workflowData"/> may be null only with
    /// <paramref name="preserveSavedWorkflowData"/>. <paramref name="clearsCrashCount"/> is false for a
    /// commit that records no progress, such as claiming an activity before it starts. With <paramref name="parkFor"/> the run stops that long and the
    /// store releases its lease; returns true when it did. <paramref name="now"/> is the host's time.
    /// </summary>
    public static async Task<bool> CommitAsync(IExecutionHost host, WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, object? workflowData, WorkflowScopeCatalog scopes, DateTime now,
        bool preserveSavedWorkflowData = false, bool enforceLimit = true, bool clearsCrashCount = true,
        TimeSpan? parkFor = null)
    {
        checkpoint.Trace.Configure(instance, checkpoint, host.Limits, null, scopes);
        var lastCommitted = SavedForm.Of(instance);
        checkpoint.CloseEndedRequests();
        OperatorTrace.ChildCancellations(instance, checkpoint, now);
        StoredCheckpoint stored;
        try
        {
            stored = Prepare(instance, checkpoint, workflowData, scopes, now, preserveSavedWorkflowData,
                host.Limits.VerifyCheckpointParts);
        }
        catch (CheckpointVerificationException)
        {
            lastCommitted.RestoreTo(instance);
            checkpoint.Trace.DiscardPending();
            throw;
        }
        var size = stored.Bytes;
        if (enforceLimit && size > host.Limits.MaxCheckpointBytes)
        {
            // The instance keeps the last checkpoint that was committed, and the trace only what that holds; what
            // changed since stays marked.
            lastCommitted.RestoreTo(instance);
            checkpoint.RestoreMarks(stored.Marks);
            checkpoint.Trace.DiscardPending();
            throw new CheckpointLimitExceededException(size, host.Limits.MaxCheckpointBytes);
        }

        // The commit carries the run's inbox decisions and the waits it opened, armed and closed (D-33).
        // It says how long, not until when: the store stamps deadlines and the runnable time (D-35).
        var (opened, armed, closed) = checkpoint.Inbox.WaitChanges(checkpoint);
        var idle = instance.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution;
        // Children start, and are asked to cancel, in the commit that records the parent's waits for
        // them; a child's terminal commit tells its parent (D-17).
        var commit = CheckpointCommit.Create(instance, host.Epoch, instance.Revision,
            clearsCrashCount: clearsCrashCount, parks: parkFor != null,
            settlements: checkpoint.Inbox.Settlements, inboxReadThrough: checkpoint.Inbox.ReadThrough,
            openedWaits: opened, closedWaits: closed, armedWaits: armed, wakeAfter: parkFor,
            stopped: checkpoint.OperatorStop != null, childStarts: checkpoint.ChildStarts.ToArray(),
            childCancellations: checkpoint.ChildCancellations.ToArray(),
            parentNotice: ChildCompleted.NoticeOf(instance, checkpoint), traceEntries: checkpoint.Trace.Pending);
        using var span = Telemetry.Source.StartActivity("commit");
        Telemetry.Tag(span, instance);
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            var reply = await host.CommitAsync(commit);
            switch (reply.Kind)
            {
                case CheckpointReplyKind.Acknowledged:
                    var tags = Telemetry.Workflow(instance);
                    Telemetry.CommitDuration.Record(
                        System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalSeconds, tags);
                    Telemetry.CommitSize.Record(size, tags);
                    instance.Revision = reply.Revision;
                    instance.Saved(commit.StoredParts, reply.Revision);
                    checkpoint.AcknowledgeTrace();
                    checkpoint.ChildStarts.Clear();
                    checkpoint.ChildCancellations.Clear();
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

    /// <summary>
    /// Puts the instance in the form it is stored in: its data, and its state's trunk, with the parts both are
    /// stored in as <see cref="WorkflowInstance.Stored"/>.
    /// </summary>
    public static StoredCheckpoint Prepare(WorkflowInstance instance, ExecutionCheckpoint checkpoint,
        object? workflowData, WorkflowScopeCatalog scopes, DateTime now, bool preserveSavedWorkflowData = false,
        bool verify = false)
    {
        try
        {
            ContinuationTransitions.PrepareCheckpoint(checkpoint, scopes);
            checkpoint.RecordBranchChanges();
            var dataJson = preserveSavedWorkflowData
                ? instance.WorkflowDataJson
                : BranchData.Snapshot(workflowData ?? throw new ArgumentNullException(nameof(workflowData)));
            var stored = StoredCheckpoint.Split(checkpoint, dataJson, instance.PartsAt(instance.Revision),
                checkpoint.TakeMarks(),
                verify);
            instance.WorkflowDataJson = dataJson;
            instance.ExecutionStateJson = stored.Trunk;
            instance.Stored = stored;
            return stored;
        }
        catch (Exception ex) when (ex is not CheckpointVerificationException)
        {
            throw new CheckpointDataException(ex);
        }
    }

    public sealed class CheckpointDataException(Exception inner) : Exception(
        $"The workflow state could not be checkpointed: {inner.Message}", inner);

    private sealed record SavedForm(string WorkflowDataJson, string ExecutionStateJson, StoredCheckpoint? Stored)
    {
        public static SavedForm Of(WorkflowInstance instance) => new(instance.WorkflowDataJson,
            instance.ExecutionStateJson, instance.Stored);

        public void RestoreTo(WorkflowInstance instance)
        {
            instance.WorkflowDataJson = WorkflowDataJson;
            instance.ExecutionStateJson = ExecutionStateJson;
            instance.Stored = Stored;
        }
    }
}

/// <summary>A checkpoint would exceed the configured size limit and was not committed.</summary>
internal sealed class CheckpointLimitExceededException(long size, long limit) : ResourceLimitExceededException(
    $"The checkpoint is {size} bytes, over the configured limit of {limit} bytes");

namespace IxIFlow.Core.Runtime;

internal sealed record ClientActor(string? Name);

/// <summary>Operator facts live only in typed payloads.</summary>
internal static class OperatorTrace
{
    public static CancellationReason WithActor(CancellationReason reason, string? defaultActor, DateTime now) =>
        (!string.IsNullOrEmpty(reason.RequestedBy) || defaultActor == null) && reason.RequestedAt != default
            ? reason
            : new CancellationReason
            {
                ReasonCode = reason.ReasonCode,
                Description = reason.Description,
                RequestedAt = reason.RequestedAt == default ? now : reason.RequestedAt,
                RequestedBy = string.IsNullOrEmpty(reason.RequestedBy) ? defaultActor ?? "" : reason.RequestedBy,
                Metadata = reason.Metadata
            };

    public static void ApplyCancellation(WorkflowInstance instance, ExecutionCheckpoint checkpoint,
        CancellationReason reason, DateTime now)
    {
        reason = WithActor(reason, null, now);
        Cancellation(checkpoint, reason, now);
        instance.CancellationReason = reason;
        checkpoint.CancellationRequested = true;
        checkpoint.Debug = checkpoint.Debug with { Evaluation = null };
        checkpoint.UnhandledError = null;
        checkpoint.RuntimeUnhandledError = null;
        var completedPause = instance.Status == WorkflowStatus.Paused &&
            checkpoint.Continuations[0].Status == ContinuationStatus.Completed;
        ContinuationTransitions.CancelTree(checkpoint, checkpoint.Continuations[0]);
        if (completedPause) checkpoint.Continuations[0].Status = ContinuationStatus.Cancelled;
    }

    public static void Cancellation(ExecutionCheckpoint checkpoint, CancellationReason reason, DateTime now)
    {
        if (checkpoint.Trace.Accepts(TraceEntryType.CancellationRequested, now))
            checkpoint.Trace.Add(new ExecutionTraceEntry { Timestamp = now,
                EntryType = TraceEntryType.CancellationRequested, Payload = Payload(reason, now) });
    }

    public static void CompletedCancellation(WorkflowInstance instance, ExecutionCheckpoint checkpoint, DateTime now)
    {
        if (checkpoint.Trace.Accepts(TraceEntryType.WorkflowCancelled, now))
            checkpoint.Trace.Add(new ExecutionTraceEntry { Timestamp = now,
                EntryType = TraceEntryType.WorkflowCancelled, Payload = Payload(instance.CancellationReason!, now) });
    }

    public static void ChildCancellations(WorkflowInstance instance, ExecutionCheckpoint checkpoint, DateTime now)
    {
        for (var index = 0; index < checkpoint.ChildCancellations.Count; index++)
        {
            var command = checkpoint.ChildCancellations[index];
            var reason = WithActor(command.Reason, instance.CancellationReason?.RequestedBy,
                instance.CancellationReason?.RequestedAt ?? now);
            checkpoint.ChildCancellations[index] = command with { Reason = reason };
            if (checkpoint.Trace.Accepts(TraceEntryType.ChildCancellationRequested, now))
                checkpoint.Trace.Add(new ExecutionTraceEntry { Timestamp = now,
                    EntryType = TraceEntryType.ChildCancellationRequested, Payload = Payload(reason, now) with
                    { ChildInstanceId = command.ChildInstanceId,
                        WaitRef = WaitReference.Of(instance.InstanceId, command.ParentWaitId) } });
        }
    }

    private static CancellationTrace Payload(CancellationReason reason, DateTime now) =>
        new(string.IsNullOrEmpty(reason.RequestedBy) ? null : reason.RequestedBy,
            reason.RequestedAt == default ? now : reason.RequestedAt, reason.ReasonCode);

    public static void Resolved(ExecutionCheckpoint checkpoint, ActivityInvocationState pending,
        ActivityResolutionState decision, DateTime now)
    {
        if (checkpoint.Trace.Accepts(TraceEntryType.ActivityResolved, now))
            checkpoint.Trace.Add(new ExecutionTraceEntry { Timestamp = now, EntryType = TraceEntryType.ActivityResolved,
                Payload = new ActivityResolutionTrace(pending.Id, pending.StepId, decision.Kind, decision.DecidedBy) });
    }

    public static void Released(ExecutionCheckpoint checkpoint, string? actor, DateTime now)
    {
        if (checkpoint.Trace.Accepts(TraceEntryType.Released, now))
            checkpoint.Trace.Add(new ExecutionTraceEntry { Timestamp = now, EntryType = TraceEntryType.Released,
                Payload = new ReleaseTrace(string.IsNullOrEmpty(actor) ? null : actor) });
    }
}

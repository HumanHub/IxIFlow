namespace IxIFlow.Core.Runtime;

/// <summary>The client's default actor (D-38), from <c>IxIFlowOptions.Actor</c>; none when it names nobody.</summary>
internal sealed record ClientActor(string? Name);

/// <summary>
/// Trace entries for what operators do to an instance, with who did it (D-38). The actor is a plain string the caller
/// supplies; the engine records it and never checks it.
/// </summary>
internal static class OperatorTrace
{
    /// <summary>The request, naming <paramref name="defaultActor"/> when it names nobody, without changing the caller's.</summary>
    public static CancellationReason WithActor(CancellationReason reason, string? defaultActor) =>
        !string.IsNullOrEmpty(reason.RequestedBy) || defaultActor == null
            ? reason
            : new CancellationReason
            {
                ReasonCode = reason.ReasonCode,
                Description = reason.Description,
                RequestedAt = reason.RequestedAt,
                RequestedBy = defaultActor,
                Metadata = reason.Metadata
            };

    /// <summary>
    /// Applies a cancellation request to a run: it is traced with who made it, recorded on the instance, and the root
    /// unwinds. Every path that applies a request goes through here, so each is traced once.
    /// </summary>
    public static void ApplyCancellation(WorkflowInstance instance, ExecutionCheckpoint checkpoint,
        CancellationReason reason, DateTime now)
    {
        Cancellation(instance, reason, now);
        instance.CancellationReason = reason;
        checkpoint.CancellationRequested = true;
        checkpoint.UnhandledError = null;
        checkpoint.RuntimeUnhandledError = null;
        ContinuationTransitions.CancelTree(checkpoint, checkpoint.Continuations[0]);
    }

    public static void Cancellation(WorkflowInstance instance, CancellationReason reason, DateTime now)
    {
        var entry = Entry(TraceEntryType.CancellationRequested, reason.RequestedBy, now);
        entry.Metadata["ReasonCode"] = reason.ReasonCode;
        Add(instance, entry);
    }

    public static void Released(WorkflowInstance instance, string? actor, DateTime now) =>
        Add(instance, Entry(TraceEntryType.Released, actor, now));

    private static ExecutionTraceEntry Entry(TraceEntryType type, string? actor, DateTime now)
    {
        var entry = new ExecutionTraceEntry { Timestamp = now, EntryType = type };
        if (!string.IsNullOrEmpty(actor))
            entry.Metadata["Actor"] = actor;
        return entry;
    }

    private static void Add(WorkflowInstance instance, ExecutionTraceEntry entry)
    {
        if (instance.TracingEnabled)
            instance.Trace.Add(entry);
    }
}

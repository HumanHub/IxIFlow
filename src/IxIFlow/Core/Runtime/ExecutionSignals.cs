namespace IxIFlow.Core.Runtime;

/// <summary>One assignment's coalesced control messages.</summary>
internal sealed class ExecutionSignals(Guid assignmentId, Action<Guid> close) : IDisposable
{
    private TaskCompletionSource _changed = NewSignal();
    private CancellationReason? _cancellation;
    private long _sequence;
    private int _runCancelled;
    private int _ownershipLost;
    private int _recycle;
    private WorkflowDebugState? _debug;
    public WorkflowDebugState? Debug => Volatile.Read(ref _debug);
    public void Deliver(DebugCommands message)
    {
        if (message.AssignmentId != AssignmentId) return;
        while (true)
        {
            var previous = Debug;
            if (previous != null && previous.Version >= message.State.Version && previous.PauseGeneration >= message.State.PauseGeneration) return;
            if (Interlocked.CompareExchange(ref _debug, message.State, previous) != previous) continue;
            Pulse();
            return;
        }
    }

    public Guid AssignmentId { get; } = assignmentId;
    public void Dispose() => close(AssignmentId);
    public Task Changed => Volatile.Read(ref _changed).Task;
    public CancellationReason? Cancellation => Volatile.Read(ref _cancellation);
    public long InboxSequence => Interlocked.Read(ref _sequence);
    public bool RunCancelled => Volatile.Read(ref _runCancelled) != 0;
    public bool Recycle => Volatile.Read(ref _recycle) != 0;

    public void Deliver(Cancel message)
    {
        if (message.AssignmentId != AssignmentId) return;
        var changed = message.Kind switch
        {
            CancelKind.Ownership => Interlocked.Exchange(ref _ownershipLost, 1) == 0,
            CancelKind.Run => Interlocked.Exchange(ref _runCancelled, 1) == 0,
            CancelKind.Workflow when message.Reason != null =>
                Interlocked.CompareExchange(ref _cancellation, message.Reason, null) == null,
            _ => false
        };
        if (changed) Pulse();
    }

    public void Deliver(Arrivals message)
    {
        if (message.AssignmentId != AssignmentId) return;
        while (true)
        {
            var previous = InboxSequence;
            if (message.InboxSequence <= previous) return;
            if (Interlocked.CompareExchange(ref _sequence, message.InboxSequence, previous) != previous) continue;
            Pulse();
            return;
        }
    }

    public void Deliver(Recycle message)
    {
        if (message.AssignmentId != AssignmentId) return;
        if (Interlocked.Exchange(ref _recycle, 1) == 0) Pulse();
    }

    public void ThrowIfOwnershipLost()
    {
        if (Volatile.Read(ref _ownershipLost) != 0)
            throw new ExecutionOwnershipLostException(new InvalidOperationException("The execution lease was lost"));
    }

    public void ThrowIfStopped()
    {
        ThrowIfOwnershipLost();
        if (RunCancelled) throw new RunCancelledException();
    }

    private void Pulse() => Interlocked.Exchange(ref _changed, NewSignal()).TrySetResult();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class RunCancelledException : Exception;

internal sealed class ExecutionOwnershipLostException(Exception cause) : Exception(
    "Workflow execution ownership was lost", cause);

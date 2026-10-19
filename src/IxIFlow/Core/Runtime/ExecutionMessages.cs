namespace IxIFlow.Core.Runtime;

internal enum CancelKind { Workflow, Run, Ownership }

// A fresh ID is allocated before acquiring the lease, including for nested runs in one slot.
internal sealed record Cancel(Guid RunId, CancelKind Kind, CancellationReason? Reason = null);
internal sealed record Arrivals(Guid RunId, long InboxSequence);
internal sealed record Recycle(Guid RunId);

internal enum AssignmentOutcomeKind { Idle, Drained, Parked, OwnershipLost, RunCancelled }

internal sealed record AssignmentOutcome(AssignmentOutcomeKind Kind,
    string? ErrorMessage = null, string? ErrorType = null, string? ErrorStackTrace = null);

/// <summary>One run's coalesced messages. Delivery only uses atomic operations and asynchronous wakes.</summary>
internal sealed class ExecutionSignals(Guid runId, Action<Guid> close) : IDisposable
{
    private TaskCompletionSource _changed = NewSignal();
    private CancellationReason? _cancellation;
    private long _sequence;
    private int _runCancelled;
    private int _ownershipLost;
    private int _recycle;

    public Guid RunId { get; } = runId;
    public void Dispose() => close(RunId);
    public Task Changed => Volatile.Read(ref _changed).Task;
    public CancellationReason? Cancellation => Volatile.Read(ref _cancellation);
    public long InboxSequence => Interlocked.Read(ref _sequence);
    public bool RunCancelled => Volatile.Read(ref _runCancelled) != 0;
    public bool Recycle => Volatile.Read(ref _recycle) != 0;

    public void Deliver(Cancel message)
    {
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

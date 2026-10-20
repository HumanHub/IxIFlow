namespace IxIFlow.Core.Runtime;

/// <summary>One assignment's coalesced control messages.</summary>
internal sealed class ExecutionSignals : IDisposable
{
    private readonly Action<Guid> _close;
    internal ExecutionSignals(Guid assignmentId, Action<Guid> close)
    {
        AssignmentId = assignmentId;
        _close = close;
        AcquisitionToken = _acquisitionStop.Token;
    }
    private TaskCompletionSource _changed = NewSignal();
    private CancellationReason? _cancellation;
    private long _sequence;
    private int _runCancelled;
    private int _ownershipLost;
    private int _recycle;
    private readonly CancellationTokenSource _acquisitionStop = new();
    private readonly object _stopGate = new();
    private bool _closed;

    internal CancellationToken AcquisitionToken { get; }
    internal void Close()
    {
        lock (_stopGate)
        {
            if (_closed) return;
            _closed = true;
            _acquisitionStop.Dispose();
        }
    }

    private void StopAcquisition()
    {
        lock (_stopGate) { if (!_closed) _acquisitionStop.Cancel(); }
    }

    public Guid AssignmentId { get; }
    public void Dispose() => _close(AssignmentId);
    public Task Changed => Volatile.Read(ref _changed).Task;
    public CancellationReason? Cancellation => Volatile.Read(ref _cancellation);
    public long InboxSequence => Interlocked.Read(ref _sequence);
    public bool RunCancelled => Volatile.Read(ref _runCancelled) != 0;
    internal bool OwnershipLost => Volatile.Read(ref _ownershipLost) != 0;
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
        if (message.Kind is CancelKind.Run or CancelKind.Ownership) StopAcquisition();
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
        if (OwnershipLost)
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

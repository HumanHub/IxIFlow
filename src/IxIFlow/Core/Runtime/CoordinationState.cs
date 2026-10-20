namespace IxIFlow.Core.Runtime;

// Transport-neutral scheduling state. Only a fully initialized, trusted link may contribute.
internal sealed class CoordinationState
{
    private readonly object _gate = new();
    private int _links;
    private TaskCompletionSource _changed = NewSignal();
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);
    internal event Action? Wake;
    internal event Action? ClaimPassCompleted;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Observe(out Task changed)
    {
        lock (_gate) { changed = _changed.Task; return _links > 0; }
    }
    internal bool Connected => Observe(out _);
    internal void AddLink() => Change(1);
    internal void RemoveLink() => Change(-1);
    private void Change(int delta)
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            var wasConnected = _links > 0;
            _links += delta;
            if (_links < 0) throw new InvalidOperationException("Unbalanced coordinator links");
            if (wasConnected == (_links > 0)) return;
            previous = _changed;
            _changed = NewSignal();
        }
        previous.TrySetResult();
        RequestPass();
    }
    internal void RequestPass() => Wake?.Invoke();
    internal void CompleteClaimPass() => ClaimPassCompleted?.Invoke();
}

namespace IxIFlow.Core;

public sealed partial class WorkflowHost
{
    private event Action<string>? Changed;

    internal IDisposable WatchChanges(string instanceId, Action changed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(changed);
        void OnChanged(string id) { if (id == instanceId) changed(); }
        Changed += OnChanged;
        return new ChangeSubscription(() => Changed -= OnChanged);
    }

    internal void NotifyChange(string instanceId)
    {
        // Subscribers only wake readers. A failed observer must never affect a committed workflow.
        foreach (var handler in Changed?.GetInvocationList() ?? [])
            try { ((Action<string>)handler)(instanceId); } catch { }
    }

    private sealed class ChangeSubscription(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}

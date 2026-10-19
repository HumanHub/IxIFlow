using System.Collections.Concurrent;

namespace IxIFlow.Core.Runtime;

/// <summary>Signals between the host and the runs in one work item's scope.</summary>
internal sealed class RunControl
{
    private Action<string>? _accepted;
    private Action<string>? _instanceKnown;
    private Action<string, DateTime>? _scheduled;

    /// <summary>Called whenever a run in this scope saves an instance that is runnable at a later time.</summary>
    public void OnScheduled(Action<string, DateTime> scheduled) => _scheduled = scheduled;

    public void Schedule(string instanceId, DateTime dueUtc) => _scheduled?.Invoke(instanceId, dueUtc);

    private Action<string>? _unscheduled;

    /// <summary>Called when a run in this scope finishes an instance, so nothing is due for it.</summary>
    public void OnUnscheduled(Action<string> unscheduled) => _unscheduled = unscheduled;

    public void Unschedule(string instanceId) => _unscheduled?.Invoke(instanceId);

    private Action<string>? _runnable;

    /// <summary>
    /// Called for each other instance a commit in this scope made runnable now, such as a child it started
    /// or a parent its completion woke, so the host runs it without waiting for its poll.
    /// </summary>
    public void OnRunnable(Action<string> runnable) => _runnable = runnable;

    public void Runnable(string instanceId) => _runnable?.Invoke(instanceId);

    private readonly ConcurrentDictionary<Guid, Action<Recycle>> _runs = new();
    private int _recycling;

    public IDisposable Register(Guid runId, Action<Recycle> deliver)
    {
        _runs[runId] = deliver;
        if (Volatile.Read(ref _recycling) != 0) deliver(new Recycle(runId));
        return new Registration(() => _runs.TryRemove(runId, out _));
    }

    public void Recycle()
    {
        Interlocked.Exchange(ref _recycling, 1);
        foreach (var (runId, deliver) in _runs) deliver(new Recycle(runId));
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }

    /// <summary>Called once, with the instance ID, when a start's initial checkpoint is committed.</summary>
    public void OnAccepted(Action<string> accepted) => _accepted = accepted;

    public void OnInstanceKnown(Action<string> instanceKnown) => _instanceKnown = instanceKnown;

    // The first start in a scope is the work item's own; children start later in the same scope.
    public void Accept(string instanceId)
    {
        Interlocked.Exchange(ref _instanceKnown, null)?.Invoke(instanceId);
        Interlocked.Exchange(ref _accepted, null)?.Invoke(instanceId);
    }
}

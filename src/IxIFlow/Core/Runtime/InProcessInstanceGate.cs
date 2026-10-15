using System.Collections.Concurrent;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Serializes mutations of one workflow instance within a process.
/// </summary>
internal sealed class InProcessInstanceGate
{
    public static InProcessInstanceGate Shared { get; } = new();

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public async ValueTask<IDisposable> EnterAsync(string instanceId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(instanceId, _ => new Entry());
            lock (entry.Sync)
            {
                if (entry.Retired)
                    continue;
                entry.Users++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken);
                return new Lease(this, instanceId, entry);
            }
            catch
            {
                Leave(instanceId, entry, acquired: false);
                throw;
            }
        }
    }

    private void Leave(string instanceId, Entry entry, bool acquired)
    {
        if (acquired)
            entry.Semaphore.Release();

        var remove = false;
        lock (entry.Sync)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                entry.Retired = true;
                remove = true;
            }
        }

        if (remove)
            ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(new(instanceId, entry));
    }

    private sealed class Entry
    {
        public object Sync { get; } = new();
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class Lease(InProcessInstanceGate owner, string instanceId, Entry entry) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner.Leave(instanceId, entry, acquired: true);
        }
    }
}

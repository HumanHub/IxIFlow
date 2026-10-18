namespace IxIFlow.Core;

/// <summary>
/// A store that tells hosts when an instance may have become claimable, so they claim it before their next poll.
/// A host keeps polling while it listens: a wake-up only adds a pass, and a lost one costs latency, not work.
/// </summary>
public interface IRunnableWakeSource
{
    /// <summary>
    /// Listens until <paramref name="cancellationToken"/> is cancelled, calling <paramref name="wake"/> each time an
    /// instance may have become claimable, and once when listening starts, for what became claimable before.
    /// Throws when the listening connection is lost; the host polls until it listens again.
    /// </summary>
    Task ListenAsync(Action wake, CancellationToken cancellationToken);
}

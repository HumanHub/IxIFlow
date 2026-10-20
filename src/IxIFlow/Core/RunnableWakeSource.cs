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

/// <summary>A committed instance change, containing only scheduling and host identities.</summary>
/// <param name="InstanceId">The instance that changed.</param>
/// <param name="DemandSetId">The immutable demand set saved for the instance.</param>
/// <param name="Pin">The saved host pin, including its expiry, if any.</param>
/// <param name="LastHostId">The last host to own the instance.</param>
/// <param name="Claimable">The write left the instance due with no live owner.</param>
/// <param name="Idle">The write left the instance idle or terminal.</param>
public sealed record RunnableNotice(string InstanceId, string DemandSetId, WorkflowDemand? Pin,
    Guid? LastHostId, bool Claimable, bool Idle);

/// <summary>
/// Receives disposable hints after store transactions commit. Delivery is bounded and best effort;
/// hosts keep polling when hints are lost. Register a singleton implementation to receive them.
/// </summary>
public interface IRunnableNotifier
{
    /// <summary>Sends a committed change. Observe cancellation to allow delivery to time out.</summary>
    Task NotifyAsync(RunnableNotice notice, CancellationToken cancellationToken);
}

internal sealed class NullRunnableNotifier : IRunnableNotifier
{
    public Task NotifyAsync(RunnableNotice notice, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The committed acknowledgement of a cancellation request.</summary>
public sealed record CancellationRequestResult(bool Accepted, RunnableNotice? Notice = null)
{
    /// <summary>Whether this write made the instance claimable.</summary>
    public bool Claimable => Notice?.Claimable == true;
}

/// <summary>The committed acknowledgement of an execution lease release.</summary>
public sealed record ExecutionLeaseReleaseResult(bool Released, RunnableNotice? Notice = null)
{
    /// <summary>Whether this release made the instance claimable.</summary>
    public bool Claimable => Notice?.Claimable == true;
}

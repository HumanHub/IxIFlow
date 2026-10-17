namespace IxIFlow.Core.Runtime;

/// <summary>
/// What the executor needs from the host for one run: committing checkpoints, observing
/// cancellation and ownership, and preparing the children its commits start. The executor never
/// touches the state store, the lease or another instance directly.
/// </summary>
internal interface IExecutionHost
{
    /// <summary>False for a run that keeps no saved state; nothing is committed.</summary>
    bool PersistsState { get; }

    /// <summary>
    /// Cancelled when the host is stopping: start no new steps, let running activities finish and
    /// be committed, then return. The instance continues on recovery.
    /// </summary>
    CancellationToken Drain { get; }

    /// <summary>Completes when the host stops owning the instance.</summary>
    Task? OwnershipLoss { get; }

    /// <summary>Completes when the host observes a cancellation request for the instance.</summary>
    Task<CancellationReason>? CancellationSignal { get; }

    /// <summary>The highest inbox sequence the host has seen for the instance while the run owns it.</summary>
    long ObservedInboxSequence { get; }

    /// <summary>Completes when the host sees the instance's inbox sequence rise (D-32).</summary>
    Task? InboxChanged { get; }

    /// <summary>The cancellation request the host has observed, if any.</summary>
    CancellationReason? ObservedCancellation { get; }

    /// <summary>Throws <see cref="ExecutionOwnershipLostException"/> once ownership is lost.</summary>
    void ThrowIfOwnershipLost();

    /// <summary>The execution epoch of this run's ownership; every commit carries it.</summary>
    long Epoch { get; }

    /// <summary>The limits the host applies to this run.</summary>
    ExecutionLimits Limits { get; }

    /// <summary>
    /// True when the run may park on a timer: it is saved, owned, and not nested inside another run
    /// that holds the slot it would need to resume.
    /// </summary>
    bool CanPark { get; }

    /// <summary>Sends one checkpoint commit. The same commit may be sent again after Retry or Unknown.</summary>
    Task<CheckpointReply> CommitAsync(CheckpointCommit commit);

    /// <summary>A pending cancellation request learned from a commit reply.</summary>
    void ObserveCancellation(CancellationReason reason);

    /// <summary>
    /// The definition an invocation step invokes. When <paramref name="starting"/>, it is checked to be active
    /// and registered, so any host can run the child (D-17); a child already started is read as it is.
    /// </summary>
    Task<WorkflowDefinition> ResolveChildAsync(WorkflowStep step, bool starting);

    /// <summary>
    /// The start of a child of <paramref name="definition"/> with <paramref name="data"/>, for this run's
    /// next commit to carry: its initial instance, as a client start saves one, with
    /// <paramref name="parent"/> and <paramref name="parentWaitId"/> to notify when it ends. Throws
    /// <see cref="CheckpointLimitExceededException"/> for a child over the checkpoint limit.
    /// </summary>
    ChildStart StartChild(WorkflowDefinition definition, object data, WorkflowInstance parent, string childInstanceId,
        string parentWaitId);

    /// <summary>Pending events of this instance after <paramref name="afterSequence"/>, and its open waits.</summary>
    Task<InboxPage> ReadInboxAsync(long afterSequence, int limit);

    /// <summary>One of this instance's waits, open or closed, or null.</summary>
    Task<StoredWait?> ReadWaitAsync(string waitId);
}

/// <summary>
/// The run no longer owns its instance: its lease was lost or another owner committed first.
/// </summary>
internal sealed class ExecutionOwnershipLostException(Exception cause) : Exception(
    "Workflow execution ownership was lost", cause);

internal enum CheckpointReplyKind
{
    /// <summary>The checkpoint is saved; Revision is its stored revision.</summary>
    Acknowledged,

    /// <summary>The run no longer owns the instance: a revision conflict or a stale epoch.</summary>
    Conflict,

    /// <summary>Nothing was written; the same commit can be sent again.</summary>
    Retry,

    /// <summary>The outcome is not known; sending the same commit again settles it.</summary>
    Unknown,

    /// <summary>An inbox event the commit settles is no longer pending; nothing was written.</summary>
    EventsChanged
}

/// <summary>The host's answer to a checkpoint commit. Error explains a reply that was not acknowledged.</summary>
internal sealed record CheckpointReply(CheckpointReplyKind Kind, long Revision = 0, Exception? Error = null,
    CancellationReason? PendingCancellation = null, bool Parked = false, bool UnreadEvents = false);

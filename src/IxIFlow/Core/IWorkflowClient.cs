namespace IxIFlow.Core;

/// <summary>
/// Starts, resumes, cancels, recovers and resolves workflow instances. Writes to a tombstoned ID
/// throw InstanceRemoved. A call that runs an
/// instance returns when the instance is idle: suspended at a wait, stopped for resolution,
/// completed, cancelled, timed out or failed.
/// </summary>
public interface IWorkflowClient
{
    /// <summary>Starts an instance of the definition and runs it until it is idle.</summary>
    Task<WorkflowExecutionResult> RunAsync<TWorkflowData>(
        WorkflowDefinition definition,
        TWorkflowData workflowData,
        WorkflowOptions? options = null,
        CancellationToken cancellationToken = default)
        where TWorkflowData : class;

    /// <summary>
    /// Starts an instance and returns its ID as soon as the start is saved, without waiting for it
    /// to run. The instance must persist its state. The token only stops the wait for the start.
    /// </summary>
    Task<string> StartAsync<TWorkflowData>(
        WorkflowDefinition definition,
        TWorkflowData workflowData,
        WorkflowOptions? options = null,
        CancellationToken cancellationToken = default)
        where TWorkflowData : class;

    /// <summary>
    /// Waits until the instance is idle or finished and returns its saved state. Work finished by
    /// this process is noticed at once; work finished elsewhere within about a second. A terminal
    /// result remains available for FinishedInstanceRetention; while its removal tombstone remains,
    /// this returns a result with status Removed and no workflow data. After expiry the ID is unknown.
    /// </summary>
    Task<WorkflowExecutionResult> AwaitAsync(
        string instanceId,
        WorkflowAwaitCondition until = WorkflowAwaitCondition.Idle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an event to the instance's one open wait of its type, then waits until the event is
    /// decided and the instance is idle. The event is stored before it is acknowledged and may satisfy
    /// only a wait that was open when it arrived; <see cref="WorkflowExecutionResult.Event"/> says
    /// whether a wait took it or why not. It stays pending while the instance is stopped for an
    /// operator.
    /// </summary>
    Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
        string instanceId,
        TEventData @event,
        CancellationToken cancellationToken = default)
        where TEventData : class;

    /// <summary>Sends an event to the open wait with this key, as <see cref="ResumeAsync{T}(string,T,CancellationToken)"/>.</summary>
    Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
        string instanceId,
        string key,
        TEventData @event,
        CancellationToken cancellationToken = default)
        where TEventData : class;

    /// <summary>
    /// Sends an event under an ID of the caller's choosing, so sending it again, for example when a
    /// message is redelivered, is recognized and answered with the first outcome. The same ID with a
    /// different event is refused.
    /// </summary>
    Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
        string instanceId,
        string? key,
        TEventData @event,
        string eventId,
        CancellationToken cancellationToken = default)
        where TEventData : class;

    /// <summary>
    /// Answers the one wait a reply token addresses, then waits until the reply is decided and
    /// the instance is idle. The reply can satisfy only that wait: once it has closed, the reply is
    /// rejected with the reason it closed. A text that is not a token is refused.
    /// </summary>
    Task<WorkflowExecutionResult> ReplyAsync<TReply>(
        string token,
        TReply reply,
        CancellationToken cancellationToken = default)
        where TReply : class;

    /// <summary>
    /// Answers a wait by token under an event ID of the caller's choosing, so sending the same reply
    /// again is recognized.
    /// </summary>
    Task<WorkflowExecutionResult> ReplyAsync<TReply>(
        string token,
        TReply reply,
        string eventId,
        CancellationToken cancellationToken = default)
        where TReply : class;

    /// <summary>
    /// Lists open waits that have no request, across instances, with the token that answers each.
    /// For an instance filter, IsRemoved marks a removed ID. A token is the right to answer, so this listing is for trusted callers such as a task inbox. A
    /// child's waits are listed on the child; a request's answer goes to the token the request sent.
    /// </summary>
    Task<WorkflowReadResult<OpenWait>> GetOpenWaitsAsync(WaitQuery query);

    /// <summary>
    /// Sends one event to every instance with an open wait for <paramref name="key"/>, under one
    /// event ID, and wakes them. Each instance decides its copy at its next pass, including one that is
    /// busy; the result lists what each inbox answered. Sending again with the same ID reaches no
    /// instance twice.
    /// </summary>
    Task<IReadOnlyList<EventDelivery>> PublishAsync<TEventData>(
        string key,
        TEventData @event,
        string eventId,
        CancellationToken cancellationToken = default)
        where TEventData : class;

    /// <summary>
    /// Continues an interrupted or due instance without an external event. An instance stopped for
    /// an operator, at a resource limit or in quarantine, stays stopped and is returned as
    /// NeedsResolution. A finished instance returns its saved result.
    /// </summary>
    Task<WorkflowExecutionResult> RecoverAsync(
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases an instance stopped for an operator, at a resource limit (after raising it) or in
    /// quarantine after repeated crashes, and runs it. Its crash count starts again from zero.
    /// <paramref name="actor"/> is who released it, recorded in the trace; none takes the client's default actor.
    /// </summary>
    Task<WorkflowExecutionResult> ReleaseAsync(
        string instanceId,
        string? actor = null,
        CancellationToken cancellationToken = default);

    /// <summary>Requests durable cancellation and unwinds an idle instance immediately.</summary>
    Task<WorkflowExecutionResult> CancelAsync(
        string instanceId,
        CancellationReason reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves one committed activity start whose external outcome is unknown. The decision is
    /// saved before execution continues.
    /// </summary>
    Task<WorkflowExecutionResult> ResolveAsync(
        string instanceId,
        string invocationId,
        ActivityResolution resolution,
        CancellationToken cancellationToken = default);

    /// <summary>The current status, Removed while tombstoned, or null for an unknown ID.</summary>
    Task<WorkflowExecutionStatus?> GetStatusAsync(string instanceId);

    /// <summary>Lists the saved activity attempts awaiting an operator decision; IsRemoved marks a removed ID.</summary>
    Task<WorkflowReadResult<PendingActivityInfo>> GetPendingActivitiesAsync(string instanceId);

    /// <summary>
    /// Where the instance's branches are and which scopes they are in, computed from its saved checkpoint and
    /// its registered definition; null when the instance does not exist.
    /// </summary>
    Task<WorkflowExecutionSnapshot?> GetSnapshotAsync(string instanceId);

    /// <summary>
    /// The instance's trace: what its runs recorded and committed, in order. A run's result carries the
    /// entries of that run. IsRemoved marks a removed ID.
    /// </summary>
    Task<WorkflowReadResult<ExecutionTraceEntry>> GetTraceAsync(string instanceId);
}

/// <summary>What <see cref="IWorkflowClient.AwaitAsync"/> waits for.</summary>
public enum WorkflowAwaitCondition
{
    /// <summary>Not running: suspended, stopped for resolution, or finished.</summary>
    Idle,

    /// <summary>Completed, failed, cancelled or timed out.</summary>
    Finished
}

/// <summary>What one instance's inbox answered to a published event.</summary>
public sealed record EventDelivery(string InstanceId, EventAppendStatus Status, EventRejection? Rejection = null);

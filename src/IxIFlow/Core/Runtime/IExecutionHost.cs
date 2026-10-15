namespace IxIFlow.Core.Runtime;

/// <summary>
/// What the executor needs from the host for one run: committing checkpoints, observing
/// cancellation and ownership, and reaching child workflows. The executor never touches the
/// state store, the lease or another instance directly.
/// </summary>
internal interface IExecutionHost
{
    /// <summary>False for a run that keeps no saved state; nothing is committed.</summary>
    bool PersistsState { get; }

    /// <summary>True when a child commits its start before it runs, so an unsaved child was never started.</summary>
    bool ChildStartIsCheckpointed { get; }

    /// <summary>Completes when the host stops owning the instance.</summary>
    Task? OwnershipLoss { get; }

    /// <summary>Completes when the host observes a cancellation request for the instance.</summary>
    Task<CancellationReason>? CancellationSignal { get; }

    /// <summary>The cancellation request the host has observed, if any.</summary>
    CancellationReason? ObservedCancellation { get; }

    /// <summary>Throws <see cref="ExecutionOwnershipLostException"/> once ownership is lost.</summary>
    void ThrowIfOwnershipLost();

    /// <summary>Commits a prepared checkpoint of the instance.</summary>
    Task CommitAsync(WorkflowInstance instance);

    Task<CancellationReason?> ReadCancellationRequestAsync();

    Task<WorkflowInstance?> ReadChildAsync(string childInstanceId);

    Task<WorkflowInvocationTransitionResult> InvokeChildAsync(WorkflowStep step, WorkflowDefinition definition,
        WorkflowInstance instance, object workflowData, object? previous, string invocationId,
        CancellationToken cancellationToken);

    Task<WorkflowExecutionResult> RecoverChildAsync(string childInstanceId,
        CancellationToken cancellationToken = default);

    Task<WorkflowExecutionResult> CancelChildAsync(string childInstanceId, CancellationReason reason,
        CancellationToken cancellationToken);

    Task<WorkflowExecutionResult> ResumeChildAsync(string childInstanceId, string? key, object @event,
        string deliveryId, CancellationToken cancellationToken);
}

/// <summary>
/// The run no longer owns its instance: its lease was lost or another owner committed first.
/// </summary>
internal sealed class ExecutionOwnershipLostException(Exception cause) : Exception(
    "Workflow execution ownership was lost", cause);

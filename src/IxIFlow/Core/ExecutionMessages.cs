using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public sealed record Assign(Guid AssignmentId, WorkflowInstance Instance,
    IReadOnlyDictionary<string, StoredJson>? Parts, string StructureHash, long Epoch, long InboxSequence,
    ExecutionLimits Limits, bool CanPark, IReadOnlyList<Breakpoint> Breakpoints,
    RecoveryPreparation? Recovery = null, int ContractVersion = ExecutionContract.Version)
{
    // Incarnation is deliberately excluded from the stored instance JSON, but must cross this boundary.
    public Guid Incarnation { get; init; } = Instance.Incarnation;
    public WorkflowDebugState? DebugState { get; init; }
    public bool DebuggingEnabled { get; init; }
}
public sealed record RecoveryPreparation(IReadOnlyList<string> ReactivatedContinuations,
    CancellationReason? PendingCancellation, bool ClearOperatorStop = false, bool Released = false, string? Actor = null);
public sealed record AcknowledgedCommit(string CommitId, long Revision);
public sealed record Ready(Guid AssignmentId, AssignmentOutcome? Outcome, AcknowledgedCommit? LastAcknowledgedCommit,
    int ContractVersion = ExecutionContract.Version, string? ExecutorName = null,
    IReadOnlyDictionary<string, string>? Capabilities = null);

public enum CancelKind { Workflow, Run, Ownership }

// A fresh ID is allocated before acquiring the lease, including for nested runs in one slot.
public sealed record Cancel(Guid AssignmentId, CancelKind Kind, CancellationReason? Reason = null);
public sealed record DebugCommands(Guid AssignmentId, WorkflowDebugState State);
public sealed record Arrivals(Guid AssignmentId, long InboxSequence);
public sealed record Recycle(Guid AssignmentId);

public enum AssignmentOutcomeKind { Idle, Drained, Parked, OwnershipLost, RunCancelled, Interrupted }

public sealed record AssignmentOutcome(AssignmentOutcomeKind Kind,
    string? ErrorMessage = null, string? ErrorType = null, string? ErrorStackTrace = null,
    string? StepId = null, string? AttemptId = null);


namespace IxIFlow.Core;

/// <summary>The durable command mode. A termination upgrades a graceful cancellation.</summary>
public enum WorkflowControlMode { Cancel, Terminate }

/// <summary>Acceptance of a durable control command. Terminal instances refuse a new command.</summary>
public sealed record WorkflowControlReceipt(bool Accepted, long Version, Guid Incarnation);

internal sealed class TerminationCommittedException : Exception;

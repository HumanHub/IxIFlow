namespace IxIFlow.Core;

/// <summary>One execution token and its requested lease duration.</summary>
public sealed record ExecutionLeaseRenewal(string InstanceId, string Token, TimeSpan Duration);

/// <summary>The outcome for one member of a lease renewal batch.</summary>
public enum ExecutionLeaseRenewalStatus { Renewed, TerminationRequested, Refused }

/// <summary>A termination refusal includes the durable command that the owner must observe.</summary>
public sealed record ExecutionLeaseRenewalResult(ExecutionLeaseRenewalStatus Status,
    CancellationReason? PendingTermination = null);

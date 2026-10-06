namespace IxIFlow.Core;

/// <summary>One invocation that stopped because its external outcome is unknown.</summary>
public sealed record PendingActivityInfo(
    string InvocationId,
    string StepId,
    string ActivityName,
    string? Reason,
    DateTime StartedAtUtc,
    IReadOnlyList<string> RequiredOutputProperties,
    bool CanResolve);

namespace IxIFlow.Core;

/// <summary>An operator's decision about an activity with an unknown external outcome.</summary>
public sealed class ActivityResolution
{
    private ActivityResolution(ActivityResolutionKind kind,
        IReadOnlyDictionary<string, object?> outputProperties, string note,
        string? decidedBy = null, Exception? error = null, object? invocationResult = null, string? expectedPolicyAttemptId = null)
    {
        ExpectedPolicyAttemptId = expectedPolicyAttemptId;
        Kind = kind;
        OutputProperties = outputProperties;
        Note = note;
        DecidedBy = decidedBy;
        Error = error;
        InvocationResult = invocationResult;
    }

    public string? ExpectedPolicyAttemptId { get; }
    public ActivityResolutionKind Kind { get; }
    public IReadOnlyDictionary<string, object?> OutputProperties { get; }
    public string Note { get; }
    public string? DecidedBy { get; }
    public Exception? Error { get; }
    public object? InvocationResult { get; }

    /// <summary>Records an observed success and the activity outputs needed downstream.</summary>
    public static ActivityResolution Completed(
        IReadOnlyDictionary<string, object?> outputProperties, string note = "Observed completed",
        string? decidedBy = null, string? expectedPolicyAttemptId = null)
    {
        ArgumentNullException.ThrowIfNull(outputProperties);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        return new ActivityResolution(ActivityResolutionKind.Completed, outputProperties, note, decidedBy, expectedPolicyAttemptId: expectedPolicyAttemptId);
    }

    /// <summary>Records the observed result of a child workflow invocation.</summary>
    public static ActivityResolution CompletedInvocation(object childWorkflowData,
        string note = "Observed child workflow completed", string? decidedBy = null, string? expectedPolicyAttemptId = null)
    {
        ArgumentNullException.ThrowIfNull(childWorkflowData);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        return new ActivityResolution(ActivityResolutionKind.Completed,
            new Dictionary<string, object?>(), note, decidedBy, invocationResult: childWorkflowData, expectedPolicyAttemptId: expectedPolicyAttemptId);
    }

    /// <summary>Records an observed failure without executing the activity again.</summary>
    public static ActivityResolution Failed(string message, string? decidedBy = null, string? expectedPolicyAttemptId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new ActivityResolution(ActivityResolutionKind.Failed,
            new Dictionary<string, object?>(), message, decidedBy,
            new InvalidOperationException(message), expectedPolicyAttemptId: expectedPolicyAttemptId);
    }

    /// <summary>Records a typed failure for the workflow's matching catch handler.</summary>
    public static ActivityResolution Failed(Exception error, string? decidedBy = null, string? expectedPolicyAttemptId = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ActivityResolution(ActivityResolutionKind.Failed,
            new Dictionary<string, object?>(), error.Message, decidedBy, error, expectedPolicyAttemptId: expectedPolicyAttemptId);
    }
}

public enum ActivityResolutionKind
{
    Completed,
    Failed
}

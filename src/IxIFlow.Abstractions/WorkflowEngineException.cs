namespace IxIFlow.Core;

/// <summary>
/// A failure of the workflow engine itself, as opposed to a failure raised by workflow code.
/// Engine failures never reach <c>Catch</c> blocks or saga error handlers.
/// </summary>
public sealed class WorkflowEngineException : Exception
{
    public WorkflowEngineException(WorkflowErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public WorkflowErrorKind Kind { get; }

    /// <summary>The workflow instance the failure belongs to, when there is one.</summary>
    public string? InstanceId { get; private set; }

    /// <summary>The step that was running, when the failure belongs to one.</summary>
    public string? StepId { get; private set; }

    /// <summary>The activity attempt that was running, when the failure belongs to one.</summary>
    public string? AttemptId { get; private set; }

    /// <summary>True when the instance stays recoverable after this failure.</summary>
    public bool IsInterruption =>
        Kind is WorkflowErrorKind.Infrastructure or WorkflowErrorKind.ArtifactUnavailable or
            WorkflowErrorKind.ResourceExhausted;

    /// <summary>
    /// Set when workflow code threw this exception, for example an activity that called another engine.
    /// It is then that step's failure, not the engine's.
    /// </summary>
    internal bool RaisedByWorkflowCode { get; set; }

    /// <summary>
    /// True for a failure of the engine itself. Such a failure interrupts or fails the instance and never
    /// reaches <c>Catch</c> or saga handlers. Anything raised by workflow code is that step's failure,
    /// whatever its type.
    /// </summary>
    internal static bool IsEngineFailure(Exception error) =>
        error is WorkflowEngineException { RaisedByWorkflowCode: false };

    /// <summary>Marks an exception raised by workflow code as that step's failure.</summary>
    internal static Exception FromWorkflowCode(Exception error)
    {
        if (error is WorkflowEngineException engineError)
            engineError.RaisedByWorkflowCode = true;
        return error;
    }

    public override string Message
    {
        get
        {
            var context = string.Join(", ", new[]
            {
                InstanceId == null ? null : $"instance '{InstanceId}'",
                StepId == null ? null : $"step '{StepId}'",
                AttemptId == null ? null : $"attempt '{AttemptId}'"
            }.Where(part => part != null));
            return context.Length == 0 ? base.Message : $"{base.Message} ({context})";
        }
    }

    /// <summary>Records where the failure happened. Context that is already known is kept.</summary>
    internal WorkflowEngineException At(string? instanceId, string? stepId = null, string? attemptId = null)
    {
        InstanceId ??= instanceId;
        StepId ??= stepId;
        AttemptId ??= attemptId;
        return this;
    }
}

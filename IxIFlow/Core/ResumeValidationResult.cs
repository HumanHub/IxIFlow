namespace IxIFlow.Core;

/// <summary>
/// Result of resume validation and preparation
/// </summary>
public class ResumeValidationResult
{
    /// <summary>
    /// Whether the validation was successful
    /// </summary>
    public bool IsValid { get; private set; }

    /// <summary>
    /// Whether the resume condition was met (true = can resume, false = stay suspended)
    /// </summary>
    public bool IsConditionMet { get; private set; }

    /// <summary>
    /// Error message if validation failed
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// The workflow instance (if validation successful)
    /// </summary>
    public WorkflowInstance? WorkflowInstance { get; private set; }

    /// <summary>
    /// The updated workflow data (if validation successful)
    /// </summary>
    public object? WorkflowData { get; private set; }

    /// <summary>
    /// The resume event (if validation successful)
    /// </summary>
    public object? ResumeEvent { get; private set; }

    private ResumeValidationResult() { }

    /// <summary>
    /// Creates a successful validation result
    /// </summary>
    public static ResumeValidationResult Success(WorkflowInstance instance, object workflowData, object resumeEvent)
    {
        return new ResumeValidationResult
        {
            IsValid = true,
            IsConditionMet = true,
            WorkflowInstance = instance,
            WorkflowData = workflowData,
            ResumeEvent = resumeEvent
        };
    }

    /// <summary>
    /// Creates a failed validation result
    /// </summary>
    public static ResumeValidationResult Failed(string errorMessage)
    {
        return new ResumeValidationResult
        {
            IsValid = false,
            IsConditionMet = false,
            ErrorMessage = errorMessage
        };
    }

    /// <summary>
    /// Creates a result indicating the resume condition was not met
    /// </summary>
    public static ResumeValidationResult ConditionNotMet(string message)
    {
        return new ResumeValidationResult
        {
            IsValid = false,
            IsConditionMet = false,
            ErrorMessage = message
        };
    }
}

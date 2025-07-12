namespace IxIFlow.Core;

/// <summary>
/// Contains information about resuming a saga from a specific point
/// </summary>
public class SagaResumeInfo
{
    /// <summary>
    /// The step index to resume from (0-based). If null, starts from beginning.
    /// </summary>
    public int? ResumeFromStepIndex { get; set; }
    
    /// <summary>
    /// The action to take after resume (ThenRetrySaga, ThenRetryFailedStep, ThenContinue, etc.)
    /// This will be an IErrorHandler instance (RetryErrorHandler, ContinuationErrorHandler, etc.)
    /// </summary>
    public object? PostResumeAction { get; set; }
    
    /// <summary>
    /// The event data that triggered the resume
    /// </summary>
    public object? ResumeEventData { get; set; }
    
    /// <summary>
    /// Previously completed saga steps (for compensation tracking)
    /// </summary>
    public List<SagaExecutionStepInfo>? CompletedSteps { get; set; }
    
    /// <summary>
    /// The index of the step that originally failed (if applicable)
    /// </summary>
    public int? FailedStepIndex { get; set; }
    
    /// <summary>
    /// The original exception that caused the saga to suspend (if applicable)
    /// </summary>
    public Exception? OriginalException { get; set; }
    
    /// <summary>
    /// Additional metadata for resume processing
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
    
    /// <summary>
    /// Creates a SagaResumeInfo for retrying the entire saga
    /// </summary>
    public static SagaResumeInfo CreateForSagaRetry(object postResumeAction, object? resumeEventData = null)
    {
        return new SagaResumeInfo
        {
            ResumeFromStepIndex = 0, // Start from beginning for full saga retry
            PostResumeAction = postResumeAction,
            ResumeEventData = resumeEventData,
            CompletedSteps = new List<SagaExecutionStepInfo>(), // Clear previous progress
            Metadata = new Dictionary<string, object>
            {
                ["ResumeType"] = "SagaRetry"
            }
        };
    }
    
    /// <summary>
    /// Creates a SagaResumeInfo for retrying a specific failed step
    /// </summary>
    public static SagaResumeInfo CreateForStepRetry(
        int failedStepIndex, 
        object postResumeAction, 
        List<SagaExecutionStepInfo>? completedSteps = null,
        object? resumeEventData = null)
    {
        return new SagaResumeInfo
        {
            ResumeFromStepIndex = failedStepIndex,
            FailedStepIndex = failedStepIndex,
            PostResumeAction = postResumeAction,
            ResumeEventData = resumeEventData,
            CompletedSteps = completedSteps ?? new List<SagaExecutionStepInfo>(),
            Metadata = new Dictionary<string, object>
            {
                ["ResumeType"] = "StepRetry"
            }
        };
    }
    
    /// <summary>
    /// Creates a SagaResumeInfo for continuing after a failed step
    /// </summary>
    public static SagaResumeInfo CreateForContinue(
        int failedStepIndex, 
        object postResumeAction,
        List<SagaExecutionStepInfo>? completedSteps = null,
        object? resumeEventData = null)
    {
        return new SagaResumeInfo
        {
            ResumeFromStepIndex = failedStepIndex + 1, // Continue after failed step
            FailedStepIndex = failedStepIndex,
            PostResumeAction = postResumeAction,
            ResumeEventData = resumeEventData,
            CompletedSteps = completedSteps ?? new List<SagaExecutionStepInfo>(),
            Metadata = new Dictionary<string, object>
            {
                ["ResumeType"] = "Continue"
            }
        };
    }
    
    /// <summary>
    /// Creates a SagaResumeInfo from serialized suspension metadata
    /// </summary>
    public static SagaResumeInfo? FromSuspensionMetadata(Dictionary<string, object> suspensionMetadata)
    {
        if (!suspensionMetadata.ContainsKey("IsSagaSuspend") || 
            !suspensionMetadata.ContainsKey("PostResumeAction"))
        {
            return null;
        }
        
        var resumeInfo = new SagaResumeInfo
        {
            PostResumeAction = suspensionMetadata["PostResumeAction"],
            Metadata = new Dictionary<string, object>(suspensionMetadata)
        };
        
        if (suspensionMetadata.ContainsKey("ResumeFromStepIndex"))
        {
            resumeInfo.ResumeFromStepIndex = (int)suspensionMetadata["ResumeFromStepIndex"];
        }
        
        if (suspensionMetadata.ContainsKey("FailedStepIndex"))
        {
            resumeInfo.FailedStepIndex = (int)suspensionMetadata["FailedStepIndex"];
        }
        
        return resumeInfo;
    }
    
    /// <summary>
    /// Converts this SagaResumeInfo to suspension metadata for storage
    /// </summary>
    public Dictionary<string, object> ToSuspensionMetadata()
    {
        var metadata = new Dictionary<string, object>(Metadata)
        {
            ["IsSagaSuspend"] = true,
            ["PostResumeAction"] = PostResumeAction ?? new object()
        };
        
        if (ResumeFromStepIndex.HasValue)
        {
            metadata["ResumeFromStepIndex"] = ResumeFromStepIndex.Value;
        }
        
        if (FailedStepIndex.HasValue)
        {
            metadata["FailedStepIndex"] = FailedStepIndex.Value;
        }
        
        return metadata;
    }
}

using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Compensation error handler
/// </summary>
public class CompensationErrorHandler : IErrorHandler
{
    public CompensationStrategy Strategy { get; set; }
    public Type? CompensationTargetType { get; set; }
    public CompensationActivityInfo? CustomCompensationActivity { get; set; }
    public List<SagaStepInfo> SagaSteps { get; set; } = new();

    public async Task<ErrorHandlingResult> HandleErrorAsync(Exception exception, object context)
    {
        switch (Strategy)
        {
            case CompensationStrategy.CompensateAll:
                await CompensateAllSuccessfulSteps(context);
                break;
            case CompensationStrategy.CompensateUpTo:
                await CompensateUpToStep(context);
                break;
            case CompensationStrategy.CustomActivity:
                await ExecuteCustomCompensation(context);
                break;
            case CompensationStrategy.None:
                break;
        }

        return new ErrorHandlingResult
        {
            Action = ErrorAction.Terminate,
            Message = $"Saga compensation completed for strategy: {Strategy}"
        };
    }

    private async Task CompensateAllSuccessfulSteps(object context)
    {
        // Compensate in reverse order
        var successfulSteps = SagaSteps.Where(s => s.IsSuccess).Reverse().ToList();

        foreach (var step in successfulSteps)
            if (step.CompensationActivity != null)
                await ExecuteCompensationActivity(step.CompensationActivity, context);
    }

    private async Task CompensateUpToStep(object context)
    {
        if (CompensationTargetType == null) return;

        var targetStepIndex = SagaSteps.FindIndex(s => s.ActivityType == CompensationTargetType);
        if (targetStepIndex == -1) return;

        // Compensate steps after the target step in reverse order
        var stepsToCompensate = SagaSteps
            .Take(targetStepIndex + 1)
            .Where(s => s.IsSuccess)
            .Reverse()
            .ToList();

        foreach (var step in stepsToCompensate)
            if (step.CompensationActivity != null)
                await ExecuteCompensationActivity(step.CompensationActivity, context);
    }

    private async Task ExecuteCustomCompensation(object context)
    {
        if (CustomCompensationActivity != null) await ExecuteCompensationActivity(CustomCompensationActivity, context);
    }

    private async Task ExecuteCompensationActivity(CompensationActivityInfo compensationInfo, object context)
    {
        // For now, just log the compensation activity execution
        // Full implementation would require a proper compensation execution framework
        await Task.CompletedTask;

        // TODO: Implement actual compensation activity execution
        // This would involve:
        // 1. Creating the compensation activity instance
        // 2. Mapping compensation context to activity inputs
        // 3. Executing the compensation activity
        // 4. Handling any compensation failures
    }
}

/// <summary>
///     Suspension error handler
/// </summary>
public class SuspensionErrorHandler<TEvent> : IErrorHandler
{
    public string Reason { get; set; } = string.Empty;
    public Type EventType { get; set; } = typeof(TEvent);
    public Func<object, object, bool>? ResumeCondition { get; set; }
    public IErrorHandler? PostResumeAction { get; set; }
    public CompensationErrorHandler? PreExecute { get; set; }
    public SagaErrorConfiguration? SagaErrorConfig { get; set; }

    public async Task<ErrorHandlingResult> HandleErrorAsync(Exception exception, object context)
    {
        // Execute compensation first if configured
        if (PreExecute != null) await PreExecute.HandleErrorAsync(exception, context);

        // Suspend workflow execution
        return new ErrorHandlingResult
        {
            Action = ErrorAction.Suspend,
            Message = Reason,
            SuspendEventType = EventType,
            ResumeCondition = ResumeCondition,
            PostResumeHandler = PostResumeAction
        };
    }
}

/// <summary>
///     Retry error handler
/// </summary>
public class RetryErrorHandler : IErrorHandler
{
    public RetryPolicy RetryPolicy { get; set; }
    public RetryTarget RetryTarget { get; set; } = RetryTarget.FailedStep;
    public CompensationErrorHandler? PreExecute { get; set; }

    public async Task<ErrorHandlingResult> HandleErrorAsync(Exception exception, object context)
    {
        // Execute compensation first if configured
        if (PreExecute != null) await PreExecute.HandleErrorAsync(exception, context);

        return new ErrorHandlingResult
        {
            Action = ErrorAction.Retry,
            RetryPolicy = RetryPolicy,
            RetryTarget = RetryTarget,
            Message = $"Retrying {RetryTarget} up to {RetryPolicy.MaximumAttempts} times"
        };
    }
}

/// <summary>
///     Termination error handler
/// </summary>
public class TerminationErrorHandler : IErrorHandler
{
    public CompensationErrorHandler? PreExecute { get; set; }
    public SagaErrorConfiguration? SagaErrorConfig { get; set; }

    public async Task<ErrorHandlingResult> HandleErrorAsync(Exception exception, object context)
    {
        // Execute compensation first if configured
        if (PreExecute != null) await PreExecute.HandleErrorAsync(exception, context);

        return new ErrorHandlingResult
        {
            Action = ErrorAction.Terminate,
            Message = "Saga terminated due to error"
        };
    }
}

/// <summary>
///     Continuation error handler
/// </summary>
public class ContinuationErrorHandler : IErrorHandler
{
    public bool IgnoreCompensationErrors { get; set; }
    public CompensationErrorHandler? PreExecute { get; set; }
    public SagaErrorConfiguration? SagaErrorConfig { get; set; }

    public async Task<ErrorHandlingResult> HandleErrorAsync(Exception exception, object context)
    {
        // Execute compensation first if configured
        if (PreExecute != null) await PreExecute.HandleErrorAsync(exception, context);

        return new ErrorHandlingResult
        {
            Action = ErrorAction.Continue,
            Message = "Continuing saga execution despite error"
        };
    }
}

/// <summary>
///     Ignore error handler
/// </summary>
public class IgnoreErrorHandler : IErrorHandler
{
    public async Task<ErrorHandlingResult> HandleErrorAsync(Exception exception, object context)
    {
        return new ErrorHandlingResult
        {
            Action = ErrorAction.Ignore,
            Message = "Error ignored, continuing normal execution"
        };
    }
}

namespace IxIFlow.Core.Runtime;

/// <summary>Keeps the offending step until validation decorates its error.</summary>
internal static class WorkflowBuildError
{
    private const string StepKey = "IxIFlow.BuildStep";

    public static WorkflowEngineException At(WorkflowStep step, WorkflowEngineException error)
    {
        if (!error.Data.Contains(StepKey)) error.Data[StepKey] = step;
        return error;
    }

    public static WorkflowEngineException Invalid(WorkflowStep step, string message) =>
        At(step, new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, message));

    public static WorkflowStep? Step(WorkflowEngineException error) => error.Data[StepKey] as WorkflowStep;

    public static T Check<T>(WorkflowStep step, Func<T> check)
    {
        try { return check(); }
        catch (WorkflowEngineException error) { throw At(step, error); }
    }
}

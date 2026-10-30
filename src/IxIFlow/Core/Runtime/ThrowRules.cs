namespace IxIFlow.Core.Runtime;

internal static class ThrowRules
{
    internal static bool BusinessType(Type? type) => type != null && typeof(Exception).IsAssignableFrom(type) &&
        !type.IsAbstract && !type.ContainsGenericParameters && !typeof(WorkflowEngineException).IsAssignableFrom(type) &&
        !typeof(OperationCanceledException).IsAssignableFrom(type) && !typeof(SagaTerminatedException).IsAssignableFrom(type) &&
        !typeof(SagaFailureException).IsAssignableFrom(type) && !typeof(DurableWorkflowException).IsAssignableFrom(type);

    internal static void Validate(IEnumerable<WorkflowStep> steps, bool caught = false)
    {
        foreach (var step in steps)
        {
            if (step.StepType == WorkflowStepType.Throw)
            {
                if (!BusinessType(step.ExceptionType))
                    throw WorkflowBuildError.Invalid(step, "THROW001: Throw requires a concrete business exception type");

                if (step.ExceptionExpression == null || step.ExceptionFactory == null ||
                    step.ExceptionExpression.ReturnType != step.ExceptionType || step.ExceptionFactoryReturnType != step.ExceptionType)
                    throw WorkflowBuildError.Invalid(step, "THROW002: The factory must return the declared exception type");
            }

            if (step.StepType == WorkflowStepType.Rethrow && !caught)
                throw WorkflowBuildError.Invalid(step, "RETHROW001: Rethrow must be inside a TryCatch Catch body");

            foreach (var child in step.ChildScopes())
            {
                if (step.StepType == WorkflowStepType.TryCatch && child.Kind == "catch")
                {
                    foreach (var handler in child.Steps)
                        Validate(handler.SequenceSteps, true);
                }
                else
                    Validate(child.Steps, child.Kind == "inline" ? false : caught);
            }
        }
    }
}
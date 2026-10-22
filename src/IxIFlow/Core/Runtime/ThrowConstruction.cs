namespace IxIFlow.Core.Runtime;

internal static class ThrowConstruction
{
    internal static WorkflowStep Leaf<TData, TPrevious>(WorkflowStepType kind, string path, int line)
        where TData : class where TPrevious : class => new()
    {
        Name = kind.ToString(), StepType = kind, WorkflowDataType = typeof(TData),
        PreviousStepDataType = typeof(TPrevious), Runtime = WorkflowStepDelegates.Create<TData, TPrevious>(),
        SourceLocation = path + ":" + line
    };
}

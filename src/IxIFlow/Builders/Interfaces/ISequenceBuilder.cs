namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Builder for defining sequences of workflow steps
/// </summary>
public interface ISequenceBuilder<TWorkflowData, TPreviousStepData> 
    : IWorkflowBuilder<TWorkflowData, TPreviousStepData>
    where TPreviousStepData : class
{
}
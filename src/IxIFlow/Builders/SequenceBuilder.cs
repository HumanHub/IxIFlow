using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

internal class SequenceBuilder<TWorkflowData, TPreviousStepData>(List<WorkflowStep> sequenceSteps)
    : WorkflowBuilder<TWorkflowData, TPreviousStepData>(sequenceSteps, string.Empty), ISequenceBuilder<TWorkflowData, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
}
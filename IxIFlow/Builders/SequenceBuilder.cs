using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

public class SequenceBuilder<TWorkflowData, TPreviousStepData>(List<WorkflowStep> sequenceSteps)
    : WorkflowBuilder<TWorkflowData, TPreviousStepData>(sequenceSteps), ISequenceBuilder<TWorkflowData, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{ }

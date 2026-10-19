using IxIFlow.Builders.Interfaces;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Core.Runtime;

internal static class ChildWorkflowBinding
{
    public static WorkflowDefinition Resolve<TWorkflow, TData>(IServiceProvider services)
        where TWorkflow : IWorkflow<TData> where TData : class
    {
        return services.GetService<RegisteredWorkflowDefinition<TWorkflow>>()?.Definition
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Workflow type '{typeof(TWorkflow).Name}' is not registered");
    }
}

using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Core.Runtime;

internal static class ChildWorkflowBinding
{
    public static WorkflowDefinition Resolve<TWorkflow, TData>(IServiceProvider services)
        where TWorkflow : IWorkflow<TData> where TData : class
    {
        if (services.GetService<RegisteredWorkflowDefinition<TWorkflow>>() is { } registered)
            return registered.Definition;
        var workflow = ActivatorUtilities.GetServiceOrCreateInstance<TWorkflow>(services);
        var builder = new WorkflowBuilder<TData>(typeof(TWorkflow).Name, workflow.Version);
        workflow.Build(builder);
        return builder.Build();
    }
}

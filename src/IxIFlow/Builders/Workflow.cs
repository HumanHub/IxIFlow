using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Builders;

/// <summary>
///     Static factory for creating workflow builders
/// </summary>
public static class Workflow
{
    /// <summary>
    ///     Creates a new workflow builder for the specified workflow data type
    /// </summary>
    /// <typeparam name="TWorkflowData">The type of data that flows through the workflow</typeparam>
    /// <param name="name">Required workflow name</param>
    /// <param name="version">Optional workflow version</param>
    /// <returns>A new workflow builder instance</returns>
    public static IWorkflowBuilder<TWorkflowData> Create<TWorkflowData>(string name, int version = 1)
        where TWorkflowData : class
    {
        return new WorkflowBuilder<TWorkflowData>(name, version);
    }
}

/// <summary>
///     Extension methods for workflow builders
/// </summary>
public static class WorkflowBuilderExtensions
{
    /// <summary>
    ///     Executes the workflow directly using the workflow engine
    /// </summary>
    /// <typeparam name="TWorkflowData">The workflow data type</typeparam>
    /// <param name="builder">The workflow builder</param>
    /// <param name="workflowData">The initial workflow data</param>
    /// <param name="serviceProvider">Optional service provider with IxIFlow registered through AddIxIFlow</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>The workflow execution result</returns>
    public static async Task<WorkflowExecutionResult> ExecuteAsync<TWorkflowData>(
        this IWorkflowBuilder<TWorkflowData> builder,
        TWorkflowData workflowData,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
        where TWorkflowData : class
    {
        var definition = builder.Build();
        await using var defaultProvider = serviceProvider == null ? CreateDefaultServiceProvider() : null;
        using var scope = (serviceProvider ?? defaultProvider!).CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowClient>();
        return await engine.RunAsync(definition, workflowData, cancellationToken: cancellationToken);
    }

    /// <summary>
    ///     Creates a service provider with the standard IxIFlow registration
    /// </summary>
    private static ServiceProvider CreateDefaultServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }
}

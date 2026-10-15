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
    /// <param name="name">Optional workflow name</param>
    /// <param name="version">Optional workflow version</param>
    /// <returns>A new workflow builder instance</returns>
    public static IWorkflowBuilder<TWorkflowData> Create<TWorkflowData>(string name = "", int version = 1)
        where TWorkflowData : class
    {
        return new WorkflowBuilder<TWorkflowData>(name, version);
    }

    /// <summary>
    ///     Creates a workflow definition from a builder configuration
    /// </summary>
    /// <typeparam name="TWorkflowData">The type of data that flows through the workflow</typeparam>
    /// <param name="configure">Builder configuration action</param>
    /// <param name="name">Optional workflow name</param>
    /// <param name="version">Optional workflow version</param>
    /// <returns>A compiled workflow definition ready for execution</returns>
    public static WorkflowDefinition Build<TWorkflowData>(
        Action<IWorkflowBuilder<TWorkflowData>> configure,
        string name = "",
        int version = 1)
        where TWorkflowData : class
    {
        var builder = new WorkflowBuilder<TWorkflowData>(name, version);
        configure(builder);
        return builder.Build();
    }
}

/// <summary>
///     Extension methods for workflow builders
/// </summary>
public static class WorkflowBuilderExtensions
{
    /// <summary>
    ///     Builds the workflow definition from the builder
    /// </summary>
    /// <typeparam name="TWorkflowData">The workflow data type</typeparam>
    /// <param name="builder">The workflow builder</param>
    /// <returns>A compiled workflow definition</returns>
    public static WorkflowDefinition Build<TWorkflowData>(this IWorkflowBuilder<TWorkflowData> builder)
        where TWorkflowData : class
    {
        // Handle concrete WorkflowBuilder
        if (builder is WorkflowBuilder<TWorkflowData> concreteBuilder) return concreteBuilder.Build();

        // Use the Build method directly from the interface
        return builder.Build();
    }

    /// <summary>
    ///     Builds the workflow definition from the builder with previous step data
    /// </summary>
    /// <typeparam name="TWorkflowData">The workflow data type</typeparam>
    /// <typeparam name="TPreviousStepData">The previous step data type</typeparam>
    /// <param name="builder">The workflow builder</param>
    /// <returns>A compiled workflow definition</returns>
    public static WorkflowDefinition Build<TWorkflowData, TPreviousStepData>(
        this IWorkflowBuilder<TWorkflowData, TPreviousStepData> builder)
        where TWorkflowData : class
        where TPreviousStepData : class
    {
        // Handle concrete WorkflowBuilder
        if (builder is WorkflowBuilder<TWorkflowData, TPreviousStepData> concreteBuilder)
            return concreteBuilder.Build();


        // Use the Build method directly from the interface
        return builder.Build();
    }

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
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
        return await engine.ExecuteWorkflowAsync(definition, workflowData, cancellationToken: cancellationToken);
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

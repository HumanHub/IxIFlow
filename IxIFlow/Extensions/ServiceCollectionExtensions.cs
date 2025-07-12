using System.Reflection;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Extensions;

/// <summary>
/// Extension methods for configuring IxIFlow services in dependency injection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds IxIFlow workflow services to the service collection
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddIxIFlow(this IServiceCollection services)
    {
        // Core workflow services
        services.AddScoped<IWorkflowEngine, WorkflowEngine>();
        services.AddScoped<IActivityExecutor, ActivityExecutor>();
        services.AddScoped<IWorkflowInvoker, WorkflowInvoker>();
        services.AddScoped<ISuspendResumeExecutor, SuspendResumeExecutor>();
        services.AddScoped<ISagaExecutor, SagaExecutor>();
        
        // Workflow state and persistence
        services.AddSingleton<IWorkflowVersionRegistry, WorkflowVersionRegistry>();
        services.AddScoped<IWorkflowStateRepository, InMemoryWorkflowStateRepository>();
        services.AddScoped<IEventStore, InMemoryEventStore>();
        
        // Workflow tracing and monitoring
        services.AddScoped<IWorkflowTracer, WorkflowTracer>();
        
        // Event management
        services.AddScoped<IEventCorrelator, EventCorrelator>();
        services.AddScoped<IEventRepository, InMemoryEventRepository>();
        
        // Expression evaluation
        services.AddScoped<IExpressionEvaluator, ExpressionEvaluator>();
        
        // Suspension management
        services.AddScoped<ISuspensionManager, SuspensionManager>();
        
        // Workflow event management
        services.AddScoped<IWorkflowEventManager, WorkflowEventManager>();

        return services;
    }

    /// <summary>
    /// Adds IxIFlow workflow services with custom configuration
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <param name="configure">Configuration action for workflow options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddIxIFlow(this IServiceCollection services, Action<IxIFlowOptions> configure)
    {
        var options = new IxIFlowOptions();
        configure(options);

        // Add core services
        services.AddIxIFlow();

        // Apply custom configurations
        if (options.UseCustomStateRepository != null)
        {
            services.AddScoped(typeof(IWorkflowStateRepository), options.UseCustomStateRepository);
        }

        if (options.UseCustomEventStore != null)
        {
            services.AddScoped(typeof(IEventStore), options.UseCustomEventStore);
        }

        if (options.UseCustomTracer != null)
        {
            services.AddScoped(typeof(IWorkflowTracer), options.UseCustomTracer);
        }

        return services;
    }

    /// <summary>
    /// Registers a workflow class and builds its definition (automatically infers workflow data type)
    /// </summary>
    /// <typeparam name="TWorkflow">The workflow class that implements IWorkflow&lt;TWorkflowData&gt;</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="name">Optional workflow name (defaults to type name)</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection RegisterWorkflow<TWorkflow>(
        this IServiceCollection services,
        string? name = null)
        where TWorkflow : class
    {
        // Use reflection to find the IWorkflow<T> interface
        var workflowInterface = typeof(TWorkflow)
            .GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IWorkflow<>));
        
        if (workflowInterface == null)
            throw new ArgumentException($"{typeof(TWorkflow).Name} must implement IWorkflow<T>");
        
        var workflowDataType = workflowInterface.GetGenericArguments()[0];
        
        // Register the workflow class
        services.AddTransient<TWorkflow>();
        
        // Use reflection to call the generic BuildWorkflowDefinition method
        var buildMethod = typeof(ServiceCollectionExtensions)
            .GetMethod(nameof(BuildWorkflowDefinition), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(typeof(TWorkflow), workflowDataType);
        
        // Register the workflow definition as a singleton
        services.AddSingleton<WorkflowDefinition>(serviceProvider =>
        {
            var workflow = serviceProvider.GetRequiredService<TWorkflow>();
            return (WorkflowDefinition)buildMethod.Invoke(null, new object[] { workflow, name })!;
        });

        return services;
    }

    /// <summary>
    /// Registers a workflow class and builds its definition (explicit types for advanced scenarios)
    /// </summary>
    /// <typeparam name="TWorkflow">The workflow class that implements IWorkflow&lt;TWorkflowData&gt;</typeparam>
    /// <typeparam name="TWorkflowData">The workflow data type</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="name">Optional workflow name (defaults to type name)</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection RegisterWorkflow<TWorkflow, TWorkflowData>(
        this IServiceCollection services,
        string? name = null)
        where TWorkflow : class, IWorkflow<TWorkflowData>
        where TWorkflowData : class
    {
        // Register the workflow class
        services.AddTransient<TWorkflow>();
        
        // Register the workflow definition as a singleton
        services.AddSingleton<WorkflowDefinition>(serviceProvider =>
        {
            var workflow = serviceProvider.GetRequiredService<TWorkflow>();
            return BuildWorkflowDefinition<TWorkflow, TWorkflowData>(workflow, name);
        });

        return services;
    }

    /// <summary>
    /// Registers multiple workflows at once
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Configuration action for workflow registration</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection RegisterWorkflows(
        this IServiceCollection services,
        Action<IWorkflowRegistrationBuilder> configure)
    {
        var builder = new WorkflowRegistrationBuilder(services);
        configure(builder);
        return services;
    }

    /// <summary>
    /// Builds a workflow definition from a workflow class instance
    /// </summary>
    /// <typeparam name="TWorkflow">The workflow class type</typeparam>
    /// <typeparam name="TWorkflowData">The workflow data type</typeparam>
    /// <param name="workflow">The workflow instance</param>
    /// <param name="name">Optional workflow name</param>
    /// <returns>The built workflow definition</returns>
    public static WorkflowDefinition BuildWorkflowDefinition<TWorkflow, TWorkflowData>(
        TWorkflow workflow,
        string? name = null)
        where TWorkflow : IWorkflow<TWorkflowData>
        where TWorkflowData : class
    {
        var workflowName = name ?? typeof(TWorkflow).Name;
        var builder = Workflow.Create<TWorkflowData>(workflowName, workflow.Version);
        workflow.Build(builder);
        return builder.Build();
    }

    /// <summary>
    /// Gets a workflow definition by type from the service provider (automatically infers workflow data type)
    /// </summary>
    /// <typeparam name="TWorkflow">The workflow class type</typeparam>
    /// <param name="serviceProvider">The service provider</param>
    /// <returns>The workflow definition</returns>
    public static WorkflowDefinition GetWorkflowDefinition<TWorkflow>(
        this IServiceProvider serviceProvider)
        where TWorkflow : class
    {
        // Try to get a registered definition first
        var definitions = serviceProvider.GetServices<WorkflowDefinition>();
        var definition = definitions.FirstOrDefault(d => d.Name == typeof(TWorkflow).Name);
        
        if (definition != null)
            return definition;

        // If not found, build it on demand using reflection
        var workflowInterface = typeof(TWorkflow)
            .GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IWorkflow<>));
        
        if (workflowInterface == null)
            throw new ArgumentException($"{typeof(TWorkflow).Name} must implement IWorkflow<T>");
        
        var workflowDataType = workflowInterface.GetGenericArguments()[0];
        
        var buildMethod = typeof(ServiceCollectionExtensions)
            .GetMethod(nameof(BuildWorkflowDefinition), BindingFlags.Public | BindingFlags.Static)!
            .MakeGenericMethod(typeof(TWorkflow), workflowDataType);
        
        var workflow = serviceProvider.GetRequiredService<TWorkflow>();
        return (WorkflowDefinition)buildMethod.Invoke(null, new object[] { workflow, null })!;
    }

    /// <summary>
    /// Gets a workflow definition by type from the service provider (explicit types for advanced scenarios)
    /// </summary>
    /// <typeparam name="TWorkflow">The workflow class type</typeparam>
    /// <typeparam name="TWorkflowData">The workflow data type</typeparam>
    /// <param name="serviceProvider">The service provider</param>
    /// <returns>The workflow definition</returns>
    public static WorkflowDefinition GetWorkflowDefinition<TWorkflow, TWorkflowData>(
        this IServiceProvider serviceProvider)
        where TWorkflow : class, IWorkflow<TWorkflowData>
        where TWorkflowData : class
    {
        // Try to get a registered definition first
        var definitions = serviceProvider.GetServices<WorkflowDefinition>();
        var definition = definitions.FirstOrDefault(d => d.Name == typeof(TWorkflow).Name);
        
        if (definition != null)
            return definition;

        // If not found, build it on demand
        var workflow = serviceProvider.GetRequiredService<TWorkflow>();
        return BuildWorkflowDefinition<TWorkflow, TWorkflowData>(workflow);
    }
}

/// <summary>
/// Builder for registering multiple workflows
/// </summary>
public interface IWorkflowRegistrationBuilder
{
    /// <summary>
    /// Registers a workflow with automatic type inference
    /// </summary>
    IWorkflowRegistrationBuilder RegisterWorkflow<TWorkflow>(string? name = null)
        where TWorkflow : class;

    /// <summary>
    /// Registers a workflow with explicit types (for advanced scenarios)
    /// </summary>
    IWorkflowRegistrationBuilder RegisterWorkflow<TWorkflow, TWorkflowData>(
        string? name = null)
        where TWorkflow : class, IWorkflow<TWorkflowData>
        where TWorkflowData : class;
}

/// <summary>
/// Implementation of workflow registration builder
/// </summary>
internal class WorkflowRegistrationBuilder : IWorkflowRegistrationBuilder
{
    private readonly IServiceCollection _services;

    public WorkflowRegistrationBuilder(IServiceCollection services)
    {
        _services = services;
    }

    public IWorkflowRegistrationBuilder RegisterWorkflow<TWorkflow>(string? name = null)
        where TWorkflow : class
    {
        _services.RegisterWorkflow<TWorkflow>(name);
        return this;
    }

    public IWorkflowRegistrationBuilder RegisterWorkflow<TWorkflow, TWorkflowData>(
        string? name = null)
        where TWorkflow : class, IWorkflow<TWorkflowData>
        where TWorkflowData : class
    {
        _services.RegisterWorkflow<TWorkflow, TWorkflowData>(name);
        return this;
    }
}

/// <summary>
/// Configuration options for IxIFlow services
/// </summary>
public class IxIFlowOptions
{
    /// <summary>
    /// Custom workflow state repository implementation
    /// </summary>
    public Type? UseCustomStateRepository { get; set; }

    /// <summary>
    /// Custom event store implementation
    /// </summary>
    public Type? UseCustomEventStore { get; set; }

    /// <summary>
    /// Custom workflow tracer implementation
    /// </summary>
    public Type? UseCustomTracer { get; set; }

    /// <summary>
    /// Whether to enable detailed logging
    /// </summary>
    public bool EnableDetailedLogging { get; set; } = false;

    /// <summary>
    /// Whether to persist workflow state by default
    /// </summary>
    public bool PersistStateByDefault { get; set; } = true;

    /// <summary>
    /// Maximum number of concurrent workflow executions
    /// </summary>
    public int MaxConcurrentExecutions { get; set; } = 100;
}

public static class WorkflowHostExtensions
{
    /// <summary>
    /// Adds IxIFlow distributed workflow host services with SQL Server persistence
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <param name="configure">Configuration action for workflow host options</param>
    /// <param name="connectionString">SQL Server connection string for persistence</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddIxIFlowHost(
        this IServiceCollection services,
        Action<WorkflowHostOptions> configure,
        string connectionString)
    {
        // Add core IxIFlow services
        services.AddIxIFlow();

        // Configure host options
        var options = new WorkflowHostOptions();
        configure(options);
        options.StateRepositoryConnectionString = connectionString;
        options.MessageBusConnectionString = connectionString;
        services.AddSingleton(options);

        // Add distributed workflow services
        services.AddSingleton<IWorkflowHost, WorkflowHost>();
        services.AddSingleton<IWorkflowCoordinator, WorkflowCoordinator>();
        services.AddSingleton<IWorkflowHostClient, HttpWorkflowHostClient>();

        // Add SQL-based persistence services
        services.AddSingleton<IHostRegistry>(sp => new SqlHostRegistry(connectionString));
        services.AddSingleton<IMessageBus>(sp => new SqlMessageBus(connectionString));

        // Add background services for distributed operation
        services.AddHostedService<WorkflowQueueService>();
        services.AddHostedService<HostHealthService>();

        return services;
    }

    /// <summary>
    /// Adds IxIFlow distributed workflow host services with custom persistence implementations
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <param name="configure">Configuration action for workflow host options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddIxIFlowHost(
        this IServiceCollection services,
        Action<WorkflowHostOptions> configure)
    {
        // Add core IxIFlow services
        services.AddIxIFlow();

        // Configure host options
        var options = new WorkflowHostOptions();
        configure(options);
        services.AddSingleton(options);

        // Add distributed workflow services
        services.AddSingleton<IWorkflowHost, WorkflowHost>();
        services.AddSingleton<IWorkflowCoordinator, WorkflowCoordinator>();
        services.AddSingleton<IWorkflowHostClient, HttpWorkflowHostClient>();

        // Note: IHostRegistry and IMessageBus must be registered separately when using this overload
        // This allows for custom implementations to be provided

        // Add background services for distributed operation
        services.AddHostedService<WorkflowQueueService>();
        services.AddHostedService<HostHealthService>();

        return services;
    }
}

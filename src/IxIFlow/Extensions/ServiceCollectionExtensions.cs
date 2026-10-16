using System.Reflection;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
        services.TryAddSingleton(HostSettings.Default);
        services.AddSingleton<LocalWorkflowHost>();
        // One watch per host keeps the state store for the host's lifetime, so the store is a singleton.
        services.AddSingleton<CancellationWatch>();
        services.AddSingleton<IWorkflowClient, HostedWorkflowClient>();
        services.AddScoped<DirectInstanceClient>();
        services.AddScoped<RunControl>();
        services.AddScoped<IActivityExecutor, ActivityExecutor>();
        services.AddScoped<IWorkflowInvoker, WorkflowInvoker>();
        services.AddScoped<InstanceHost>();
        services.AddHostedService<WorkflowRecoveryService>();
        // Registered after the scanner so it stops first: hosted services stop in reverse order,
        // and the host drains the work the scanner admitted.
        services.AddHostedService(provider => provider.GetRequiredService<LocalWorkflowHost>());
        services.AddSingleton(InProcessInstanceGate.Shared);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(ExecutionLimits.Default);
        
        // Workflow state and persistence
        services.AddSingleton<IWorkflowVersionRegistry, WorkflowVersionRegistry>();
        services.TryAddSingleton<IWorkflowStateRepository, InMemoryWorkflowStateRepository>();
        
        // Workflow tracing and monitoring
        services.AddScoped<IWorkflowTracer, WorkflowTracer>();
        
        // Expression evaluation
        services.AddScoped<IExpressionEvaluator, ExpressionEvaluator>();
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
        if (options.MaxCheckpointBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(configure), "The checkpoint size limit must be positive");
        if (options.MaxCrashes <= 0)
            throw new ArgumentOutOfRangeException(nameof(configure), "The crash limit must be positive");
        if (options.MaxActiveBranches <= 0)
            throw new ArgumentOutOfRangeException(nameof(configure), "The branch limit must be positive");
        if (options.MaxActivitiesPerRun <= 0)
            throw new ArgumentOutOfRangeException(nameof(configure), "The activity limit must be positive");
        if (options.MaxForEachItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(configure), "The ForEach item limit must be positive");
        if (options.ParkRetriesLongerThan < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(configure), "The park threshold cannot be negative");
        if (options.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(configure), "The poll interval must be positive");
        services.Replace(ServiceDescriptor.Singleton(new ExecutionLimits
        {
            MaxCheckpointBytes = options.MaxCheckpointBytes,
            MaxCrashes = options.MaxCrashes,
            MaxActiveBranches = options.MaxActiveBranches,
            MaxActivitiesPerRun = options.MaxActivitiesPerRun,
            MaxForEachItems = options.MaxForEachItems,
            ParkRetriesLongerThan = options.ParkRetriesLongerThan
        }));
        if (options.Slots <= 0)
            throw new ArgumentOutOfRangeException(nameof(configure), "The slot count must be positive");
        if (options.DrainTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(configure), "The drain timeout cannot be negative");
        services.Replace(ServiceDescriptor.Singleton(
            new HostSettings(options.Slots, options.DrainTimeout, options.PollInterval)));

        // Apply custom configurations
        if (options.UseCustomStateRepository != null)
        {
            services.Replace(ServiceDescriptor.Singleton(typeof(IWorkflowStateRepository), options.UseCustomStateRepository));
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
        EnsureWorkflowTypeIsNotRegistered<TWorkflow>(services);
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
        
        services.AddSingleton<RegisteredWorkflowDefinition<TWorkflow>>(serviceProvider =>
        {
            var workflow = serviceProvider.GetRequiredService<TWorkflow>();
            var definition = (WorkflowDefinition)buildMethod.Invoke(null, new object[] { workflow, name })!;
            return new RegisteredWorkflowDefinition<TWorkflow>(definition);
        });
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<RegisteredWorkflowDefinition<TWorkflow>>().Definition);

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
        EnsureWorkflowTypeIsNotRegistered<TWorkflow>(services);
        // Register the workflow class
        services.AddTransient<TWorkflow>();
        
        services.AddSingleton<RegisteredWorkflowDefinition<TWorkflow>>(serviceProvider =>
        {
            var workflow = serviceProvider.GetRequiredService<TWorkflow>();
            return new RegisteredWorkflowDefinition<TWorkflow>(
                BuildWorkflowDefinition<TWorkflow, TWorkflowData>(workflow, name));
        });
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<RegisteredWorkflowDefinition<TWorkflow>>().Definition);

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
        var registration = serviceProvider.GetService<RegisteredWorkflowDefinition<TWorkflow>>();
        if (registration != null)
            return registration.Definition;

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
        var registration = serviceProvider.GetService<RegisteredWorkflowDefinition<TWorkflow>>();
        if (registration != null)
            return registration.Definition;

        // Try to get a registered definition first
        var definitions = serviceProvider.GetServices<WorkflowDefinition>();
        var definition = definitions.FirstOrDefault(d => d.Name == typeof(TWorkflow).Name);
        
        if (definition != null)
            return definition;

        // If not found, build it on demand
        var workflow = serviceProvider.GetRequiredService<TWorkflow>();
        return BuildWorkflowDefinition<TWorkflow, TWorkflowData>(workflow);
    }

    private static void EnsureWorkflowTypeIsNotRegistered<TWorkflow>(IServiceCollection services)
        where TWorkflow : class
    {
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(RegisteredWorkflowDefinition<TWorkflow>)))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                $"Workflow '{typeof(TWorkflow).Name}' is already registered");
    }
}

internal interface IRegisteredWorkflowDefinition
{
    WorkflowDefinition Definition { get; }
}

internal sealed record RegisteredWorkflowDefinition<TWorkflow>(WorkflowDefinition Definition)
    : IRegisteredWorkflowDefinition where TWorkflow : class;

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
    /// Custom workflow state repository implementation. Registered as a singleton;
    /// implementations must support concurrent workflow calls.
    /// </summary>
    public Type? UseCustomStateRepository { get; set; }

    /// <summary>
    /// Custom workflow tracer implementation
    /// </summary>
    public Type? UseCustomTracer { get; set; }

    /// <summary>
    /// The largest checkpoint, in bytes of workflow data and execution state, that an instance may
    /// save. A run that would exceed it stops at NeedsResolution with its last checkpoint; raise the
    /// limit and release the instance to continue.
    /// </summary>
    public long MaxCheckpointBytes { get; set; } = ExecutionLimits.DefaultMaxCheckpointBytes;

    /// <summary>
    /// How many owners in a row may lose an instance while it runs, by crashing or losing their
    /// lease, before the instance is quarantined at NeedsResolution. No host runs a quarantined
    /// instance until an operator releases it. Progress resets the count.
    /// </summary>
    public int MaxCrashes { get; set; } = ExecutionLimits.DefaultMaxCrashes;

    /// <summary>
    /// How many continuations of one instance may be live at once: the root, every parallel branch,
    /// and every parent waiting at its join. A parallel step that would exceed it stops the instance
    /// at NeedsResolution before forking, after its running activities finish; raise the limit and
    /// release the instance to continue.
    /// </summary>
    public int MaxActiveBranches { get; set; } = ExecutionLimits.DefaultMaxActiveBranches;

    /// <summary>
    /// How many activities one run of an instance has in flight at once. Further activities wait
    /// for one to finish; other branches keep advancing. It bounds each run, not the host: the host's
    /// concurrency is set by <see cref="Slots"/>. Branches that wait on each other's activities need
    /// enough room to run together.
    /// </summary>
    public int MaxActivitiesPerRun { get; set; } = ExecutionLimits.DefaultMaxActivitiesPerRun;

    /// <summary>
    /// The longest list a <c>ForEach</c> takes. A longer one stops the instance at NeedsResolution before
    /// any item runs; raise the limit and release the instance to continue, or split the list.
    /// </summary>
    public int MaxForEachItems { get; set; } = ExecutionLimits.DefaultMaxForEachItems;

    /// <summary>
    /// A run with nothing else to do whose next retry is further away than this gives its slot back
    /// and the host resumes it on time; a shorter backoff is waited out in the slot. Callers waiting
    /// for the run keep waiting either way.
    /// </summary>
    public TimeSpan ParkRetriesLongerThan { get; set; } = ExecutionLimits.DefaultParkRetriesLongerThan;

    /// <summary>
    /// How often the host looks in the store for instances that are due and that nobody owns, such
    /// as work left by another host. Its own timers fire without waiting for the poll.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = HostSettings.DefaultPollInterval;

    /// <summary>
    /// How many instances this host runs at once. A run holds its slot until the instance is idle,
    /// including while it sleeps on a retry delay or a deadline in the same run.
    /// </summary>
    public int Slots { get; set; } = HostSettings.DefaultSlots;

    /// <summary>
    /// How long a stopping host lets running instances reach their next commit before cancelling
    /// them. Keep it below the application's shutdown timeout.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = HostSettings.DefaultDrainTimeout;
}

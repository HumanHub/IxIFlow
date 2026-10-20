using System.Reflection;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Extensions;

/// <summary>
/// Extension methods for configuring IxIFlow services in dependency injection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds IxIFlow workflow services to the service collection. The builder it returns chooses the store, such as
    /// <c>.UseSqlServer(connectionString)</c> from IxIFlow.SqlServer; without one, instances live in memory.
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <returns>The builder, whose <see cref="IIxIFlowBuilder.Services"/> continues the service collection</returns>
    public static IIxIFlowBuilder AddIxIFlow(this IServiceCollection services)
    {
        services.AddLogging();
        services.TryAddSingleton(InspectionRedactionPolicy.Default);
        services.AddScoped<IWorkflowDiagnostics, WorkflowDiagnostics>();
        // Core workflow services
        services.TryAddSingleton<HostSettings>(provider => HostSettings.Default with
        { BundleHostingEnabled = provider.GetService<WorkflowBundleOptions>() != null && provider.GetService<IArtifactStore>() != null,
            CapabilityLogger = provider.GetService<ILogger<WorkflowHeartbeatService>>() });
        services.TryAddSingleton(RetentionSettings.Default);
        services.TryAddSingleton<IRunnableNotifier, NullRunnableNotifier>();
        services.TryAddSingleton<CoordinationState>();
        services.AddSingleton<RunnableNotifications>();
        services.AddSingleton(provider => new WorkflowHost(provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<HostSettings>(), provider.GetService<TimeProvider>(),
            provider.GetService<ILogger<WorkflowHost>>(), provider.GetService<ILogger<SensitiveDiagnostics>>(),
            provider.GetRequiredService<InspectionRedactionPolicy>()));
        // One watch per host keeps the state store for the host's lifetime, so the store is a singleton.
        services.AddSingleton<CancellationWatch>();
        services.AddSingleton<ExecutionLeaseRenewer>();
        services.AddSingleton<IWorkflowClient, HostedWorkflowClient>();
        services.AddSingleton<IWorkflowOperations, HostedWorkflowOperations>();
        services.AddSingleton<IWorkflowIngressClient, WorkflowIngressClient>();
        services.AddScoped<WorkflowHost.SlotClient>();
        services.AddScoped<RunControl>();
        services.AddScoped<IActivityExecutor, ActivityExecutor>();
        services.AddScoped<WorkflowHost.InstanceOperations>();
        services.TryAddSingleton(new OutboxSettings());
        services.TryAddSingleton(new PayloadStoreSettings());
        services.TryAddSingleton<IPayloadStore>(provider =>
            provider.GetRequiredService<IWorkflowStateRepository>().PayloadStore);
        // Registers before recovery starts; stops after the host has drained its work.
        services.AddHostedService<WorkflowHeartbeatService>();
        // Stops after the workflow host, so its final commits can be delivered during drain.
        services.AddHostedService<WorkflowOutboxService>();
        services.AddHostedService<WorkflowRecoveryService>();
        services.AddHostedService<WorkflowRetentionService>();
        // Registered after the scanner so it stops first: hosted services stop in reverse order,
        // and the host drains the work the scanner admitted.
        services.AddHostedService(provider => provider.GetRequiredService<WorkflowHost>());
        services.AddSingleton(InProcessInstanceGate.Shared);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(ExecutionLimits.Default);
        services.TryAddSingleton(new ClientActor(null));

        // Workflow state and persistence
        services.AddSingleton<IWorkflowCatalog>(provider => new WorkflowCatalog(
            provider.GetRequiredService<IWorkflowStateRepository>(), provider.GetServices<WorkflowDefinition>(),
            provider.GetRequiredService<IArtifactStore>(), provider.GetService<WorkflowBundleOptions>()));
        services.TryAddSingleton<IArtifactStore>(provider => new RepositoryArtifactStore(provider.GetRequiredService<IWorkflowStateRepository>()));
        services.AddSingleton<IWorkflowDefinitionSource>(provider => provider.GetRequiredService<IWorkflowCatalog>());
        services.TryAddSingleton<IWorkflowStateRepository, InMemoryWorkflowStateRepository>();
        return new IxIFlowBuilder(services);
    }

    /// <summary>
    /// Adds IxIFlow workflow services with custom configuration
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <param name="configure">Configuration action for workflow options</param>
    /// <returns>The builder that chooses the store; its <see cref="IIxIFlowBuilder.Services"/> continues the collection</returns>
    public static IIxIFlowBuilder AddIxIFlow(this IServiceCollection services, Action<IxIFlowOptions> configure)
    {
        var options = new IxIFlowOptions();
        configure(options);

        // Add core services
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton(new PayloadStoreSettings(options.MaxPayloadBytes, options.PayloadThresholdBytes)));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxHydratedPayloadBytes);
        services.Replace(ServiceDescriptor.Singleton(new InspectionRedactionPolicy(options.EnableSensitiveDiagnostics)));
        if (options.MaxCheckpointBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(configure), "The checkpoint size limit must be positive");
        if (!Enum.IsDefined(options.TraceDetail))
            throw new ArgumentOutOfRangeException(nameof(options.TraceDetail));
        if (options.MaxTraceEntriesPerInstance is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxTraceEntriesPerInstance));
        if (options.MaxTraceEntriesPerCommit <= 0)
            throw new ArgumentOutOfRangeException(nameof(configure), "The trace entry limit must be positive");
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
        if (options.UnschedulableScanInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.UnschedulableScanInterval));
        if (options.HostPinLifetime <= TimeSpan.Zero || options.HostPinLifetime > CheckpointCommit.MaxDuration)
            throw new ArgumentOutOfRangeException(nameof(options.HostPinLifetime));
        services.AddSingleton(new DemandSettings(options.HostPinLifetime));
        MemberRegistry.ValidateInterval(options.HeartbeatInterval);
        var capabilities = MemberRegistry.SnapshotCapabilities(options.Capabilities);
        if (capabilities.Keys.Any(WorkflowHeartbeatService.IsAutomaticCapability))
            throw new ArgumentException("Configured capabilities cannot replace os, arch, runtime, host or workflow:* capabilities",
                nameof(options.Capabilities));
        services.Replace(ServiceDescriptor.Singleton(new ExecutionLimits
        {
            MaxCheckpointBytes = options.MaxCheckpointBytes,
            PayloadThresholdBytes = options.PayloadThresholdBytes,
            MaxPayloadBytes = options.MaxPayloadBytes,
            MaxHydratedPayloadBytes = options.MaxHydratedPayloadBytes,
            MaxTraceEntriesPerCommit = options.MaxTraceEntriesPerCommit,
            TraceDetail = options.TraceDetail,
            MaxTraceEntriesPerInstance = options.MaxTraceEntriesPerInstance,
            MaxActiveBranches = options.MaxActiveBranches,
            MaxActivitiesPerRun = options.MaxActivitiesPerRun,
            MaxForEachItems = options.MaxForEachItems,
            ParkRetriesLongerThan = options.ParkRetriesLongerThan,
            VerifyCheckpointParts = options.VerifyCheckpointParts
        }));
        if (options.Slots <= 0)
            throw new ArgumentOutOfRangeException(nameof(configure), "The slot count must be positive");
        if (options.DrainTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(configure), "The drain timeout cannot be negative");
        if (string.IsNullOrWhiteSpace(options.HostName))
            throw new ArgumentOutOfRangeException(nameof(configure), "The host name cannot be empty");
        var hostSettings = (options.Slots, options.DrainTimeout, options.PollInterval, options.HostName, options.HeartbeatInterval);
        services.Replace(ServiceDescriptor.Singleton<HostSettings>(provider =>
            new HostSettings(hostSettings.Slots, hostSettings.DrainTimeout, hostSettings.PollInterval, hostSettings.HostName)
            { Debugging = options.Debugging, HeartbeatInterval = hostSettings.HeartbeatInterval, Capabilities = capabilities, MaxCrashes = options.MaxCrashes,
                BundleHostingEnabled = provider.GetService<WorkflowBundleOptions>() != null && provider.GetService<IArtifactStore>() != null,
                CapabilityLogger = provider.GetService<ILogger<WorkflowHeartbeatService>>(),
                UnschedulableScanInterval = options.UnschedulableScanInterval }));
        services.Replace(ServiceDescriptor.Singleton(
            new ClientActor(string.IsNullOrWhiteSpace(options.Actor) ? null : options.Actor)));

        RetentionSettings.Validate(options.FinishedInstanceRetention, options.TombstoneRetention, options.RetentionBatchSize);
        if (options.RetentionScanInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.RetentionScanInterval), "The retention scan interval must be positive");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ArtifactRetention, TimeSpan.Zero);
        services.Replace(ServiceDescriptor.Singleton(new RetentionSettings(options.FinishedInstanceRetention,
            options.TombstoneRetention, options.RetentionScanInterval, options.RetentionBatchSize) { ArtifactRetention = options.ArtifactRetention }));

        // Apply custom configurations
        if (options.UseCustomStateRepository != null)
        {
            services.Replace(ServiceDescriptor.Singleton(typeof(IWorkflowStateRepository), options.UseCustomStateRepository));
        }

        return new IxIFlowBuilder(services, options.UseCustomStateRepository?.Name);
    }

    /// <summary>
    /// Registers a workflow class and builds its definition (automatically infers workflow data type)
    /// </summary>
    /// <typeparam name="TWorkflow">The workflow class that implements IWorkflow&lt;TWorkflowData&gt;</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection RegisterWorkflow<TWorkflow>(
        this IServiceCollection services, Action<WorkflowRegistration>? configure = null)
        where TWorkflow : class
    {
        _ = WorkflowAttribute.For(typeof(TWorkflow));
        EnsureWorkflowTypeIsNotRegistered<TWorkflow>(services);
        var registration = new WorkflowRegistration();
        configure?.Invoke(registration);
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
            var definition = (WorkflowDefinition)buildMethod.Invoke(null, new object?[] { workflow })!;
            definition.RegistrationDemands = registration.Demands.ToArray();
            definition.Demands.AddRange(definition.RegistrationDemands);
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
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection RegisterWorkflow<TWorkflow, TWorkflowData>(
        this IServiceCollection services, Action<WorkflowRegistration>? configure = null)
        where TWorkflow : class, IWorkflow<TWorkflowData>
        where TWorkflowData : class
    {
        _ = WorkflowAttribute.For(typeof(TWorkflow));
        EnsureWorkflowTypeIsNotRegistered<TWorkflow>(services);
        var registration = new WorkflowRegistration();
        configure?.Invoke(registration);
        // Register the workflow class
        services.AddTransient<TWorkflow>();

        services.AddSingleton<RegisteredWorkflowDefinition<TWorkflow>>(serviceProvider =>
        {
            var workflow = serviceProvider.GetRequiredService<TWorkflow>();
            var definition = BuildWorkflowDefinition<TWorkflow, TWorkflowData>(workflow);
            definition.RegistrationDemands = registration.Demands.ToArray();
            definition.Demands.AddRange(definition.RegistrationDemands);
            return new RegisteredWorkflowDefinition<TWorkflow>(definition);
        });
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<RegisteredWorkflowDefinition<TWorkflow>>().Definition);

        return services;
    }

    /// <summary>
    /// Builds a workflow definition from a workflow class instance
    /// </summary>
    /// <typeparam name="TWorkflow">The workflow class type</typeparam>
    /// <typeparam name="TWorkflowData">The workflow data type</typeparam>
    /// <param name="workflow">The workflow instance</param>
    /// <returns>The built workflow definition</returns>
    public static WorkflowDefinition BuildWorkflowDefinition<TWorkflow, TWorkflowData>(
        TWorkflow workflow)
        where TWorkflow : IWorkflow<TWorkflowData>
        where TWorkflowData : class
    {
        var identity = WorkflowAttribute.For(typeof(TWorkflow));
        var builder = Workflow.Create<TWorkflowData>(identity.Name, identity.Version);
        workflow.Build(builder);
        return builder.Build();
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

internal sealed record RegisteredWorkflowDefinition<TWorkflow>(WorkflowDefinition Definition);

/// <summary>
/// Configuration options for IxIFlow services
/// </summary>
public class IxIFlowOptions
{
    /// <summary>Whole-value UTF-8 claim-check threshold, disabled by default (zero). Set 256 KiB to enable it.</summary>
    public long PayloadThresholdBytes { get; set; } = PayloadStoreSettings.DefaultPayloadThresholdBytes;
    /// <summary>Aggregate hydrated and in-flight payload bytes per run, default 128 MiB.</summary>
    public long MaxHydratedPayloadBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>Maximum bytes in one put, default 64 MiB.</summary>
    public long MaxPayloadBytes { get; set; } = PayloadStoreSettings.DefaultMaxPayloadBytes;

    /// <summary>Lifetime of a RequiredHostId demand, measured by store time. Default five minutes.</summary>
    public TimeSpan HostPinLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Emit original exceptions to the sensitive diagnostics logger category. Off by default.</summary>
    public bool EnableSensitiveDiagnostics { get; set; }

    /// <summary>How long unreferenced artifacts are retained; defaults to 90 days.</summary>
    public TimeSpan ArtifactRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How long terminal rows and their results remain, measured from the first terminal store write. At least one minute.</summary>
    public TimeSpan FinishedInstanceRetention { get; set; } = RetentionSettings.Default.FinishedRetention;

    /// <summary>How long a removed ID is reserved, measured from removal. Defaults to 90 days.</summary>
    public TimeSpan TombstoneRetention { get; set; } = RetentionSettings.Default.TombstoneRetention;

    /// <summary>Time between bounded retention passes. Defaults to one hour.</summary>
    public TimeSpan RetentionScanInterval { get; set; } = RetentionSettings.Default.ScanInterval;

    /// <summary>Maximum instances and maximum expired tombstones removed per pass. Defaults to 100 each.</summary>
    public int RetentionBatchSize { get; set; } = RetentionSettings.Default.BatchSize;

    /// <summary>
    /// Custom workflow state repository implementation. Registered as a singleton;
    /// implementations must support concurrent workflow calls.
    /// </summary>
    public Type? UseCustomStateRepository { get; set; }

    /// <summary>
    /// The largest checkpoint, in bytes of workflow data and execution state, that an instance may
    /// save. A run that would exceed it stops at NeedsResolution with its last checkpoint; raise the
    /// limit and release the instance to continue.
    /// </summary>
    public long MaxCheckpointBytes { get; set; } = ExecutionLimits.DefaultMaxCheckpointBytes;

    /// <summary>
    /// Maximum trace entries in one commit, default 1,000. On overflow the last slot becomes one Truncated
    /// entry counting all omissions, so at most 999 ordinary entries remain at the default. The bound resets
    /// only after acknowledgement. Entries of a pass never committed are dropped.
    /// </summary>
    public int MaxTraceEntriesPerCommit { get; set; } = ExecutionLimits.DefaultMaxTraceEntriesPerCommit;

    /// <summary>Full records control flow; Activities records activity, event, operator and cancellation entries.</summary>
    public TraceDetail TraceDetail { get; set; } = TraceDetail.Full;

    /// <summary>Optional instance budget. Once reached, one marker is recorded and control flow stops; other entries continue.</summary>
    public long? MaxTraceEntriesPerInstance { get; set; }

    /// <summary>
    /// A diagnostic: at each commit, serialize every part of the checkpoint and fail the commit, naming the part,
    /// when one changed without being marked. Off by default; it costs a serialization of every branch per commit.
    /// </summary>
    public bool VerifyCheckpointParts { get; set; }

    /// <summary>
    /// How many owners in a row may lose an instance while it runs, by crashing or losing their
    /// lease, before the instance is quarantined at NeedsResolution. No host runs a quarantined
    /// instance until an operator releases it. Progress resets the count.
    /// </summary>
    /// <summary>Enables authorized pause and continue operations on this host. Off by default.</summary>
    public bool Debugging { get; set; }

    public int MaxCrashes { get; set; } = HostSettings.DefaultMaxCrashes;

    /// <summary>
    /// Maximum live branches of one instance, excluding the root, parents waiting at joins, and
    /// completed, cancelled or cancelling branches. A parallel step that would exceed it stops the instance
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

    /// <summary>Time between store registry heartbeats. Defaults to five seconds; at most twenty minutes.</summary>
    /// <summary>Interval between bounded checks for work no live host can run; defaults to thirty seconds.</summary>
    public TimeSpan UnschedulableScanInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan HeartbeatInterval { get; set; } = MemberRegistry.DefaultHeartbeatInterval;

    /// <summary>User capability keys and values, published alongside automatic host and workflow capabilities.</summary>
    public Dictionary<string, string> Capabilities { get; set; } = new(StringComparer.Ordinal);

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

    /// <summary>
    /// How this host names itself in the records it leaves, such as the attempts an activity sees. The default
    /// is the process ID and machine name, <c>pid@machine</c>.
    /// </summary>
    public string HostName { get; set; } = HostSettings.DefaultName;

    /// <summary>
    /// Who this application's operator calls are recorded as when a call names nobody: a plain string the engine
    /// stores and never checks, such as <c>"orders-service"</c>. A call that acts for a user names them
    /// instead: <see cref="CancellationReason.RequestedBy"/>, the resolution's <c>decidedBy</c>, or the release's
    /// actor. Deciding who may call is the application's job.
    /// </summary>
    public string? Actor { get; set; }
}

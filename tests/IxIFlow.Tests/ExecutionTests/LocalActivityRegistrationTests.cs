using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.DemoActivities;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring")]
[Trait("Spec", "F-21")]
public sealed class LocalActivityRegistrationTests
{
    [Fact]
    public void AssemblyAndSingleTypeRegistrationAreIdempotent()
    {
        var services = new ServiceCollection();
        services.AddIxIFlow().AddActivities(typeof(ReadJsonActivity).Assembly).AddActivity<ReadJsonActivity>()
            .AddActivities(typeof(ReadJsonActivity).Assembly);
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IActivityRegistry>();
        Assert.Equal(3, registry.Activities.Count);
        Assert.Single(registry.Packages);
        Assert.True(registry.IsRegistered(typeof(ReadJsonActivity)));
        Assert.False(registry.IsRegistered(typeof(Unmarked)));
        Assert.Null(registry.Find("absent"));
        Assert.Equal(typeof(ReadJsonActivity), registry.Find("demo.json.read")!.ActivityType);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ReadJsonActivity));
    }

    [Fact]
    public void CanonicalMetadataIncludesTypesConvertedDefaultsAndNullDefaultPresence()
    {
        var services = new ServiceCollection();
        services.AddIxIFlow().AddActivity<Metadata>().AddActivity<Injected>();
        using var first = services.BuildServiceProvider();
        var registry = first.GetRequiredService<IActivityRegistry>();
        var metadata = registry.Find("local.metadata")!;
        Assert.Equal(new[] { "Converted", "NoDefault", "NullDefault" }, metadata.Inputs.Select(member => member.Name));
        var converted = metadata.Inputs[0];
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(int)), converted.Type);
        Assert.Equal("12", converted.DefaultJson);
        Assert.True(converted.HasDefault);
        Assert.Equal("Amount", converted.DisplayName);
        Assert.Equal("Converted amount", converted.Description);
        Assert.Equal("code", converted.UIHint);
        Assert.False(metadata.Inputs[1].HasDefault);
        Assert.Null(metadata.Inputs[1].DefaultJson);
        Assert.True(metadata.Inputs[2].HasDefault);
        Assert.Equal("null", metadata.Inputs[2].DefaultJson);
        var package = Assert.Single(registry.Packages);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(package.ActivitiesJson))), package.Fingerprint);
        Assert.DoesNotContain("ActivityType", package.ActivitiesJson);
        Assert.DoesNotContain("\n", package.ActivitiesJson);
        var reverse = new ServiceCollection();
        reverse.AddIxIFlow().AddActivity<Injected>().AddActivity<Metadata>();
        using var second = reverse.BuildServiceProvider();
        Assert.Equal(package, Assert.Single(second.GetRequiredService<IActivityRegistry>().Packages));
        var subset = new ServiceCollection();
        subset.AddIxIFlow().AddActivity<Metadata>();
        using var third = subset.BuildServiceProvider();
        Assert.NotEqual(package.Fingerprint, Assert.Single(third.GetRequiredService<IActivityRegistry>().Packages).Fingerprint);
    }

    [Theory]
    [InlineData("2.3.4+commit", "2.3.4")]
    [InlineData("2.3.4.0", "2.3.4")]
    [InlineData("2.3.4-Beta.1+commit", "2.3.4-beta.1")]
    [InlineData(null, "7.8.9")]
    public void AssemblyIdentityUsesPackageRules(string? informational, string expected)
    {
        var assembly = AssemblyWithActivity("Local.Identity." + Guid.NewGuid().ToString("N"), informational);
        var services = new ServiceCollection();
        services.AddIxIFlow().AddActivities(assembly);
        using var provider = services.BuildServiceProvider();
        var package = Assert.Single(provider.GetRequiredService<IActivityRegistry>().Packages);
        Assert.Equal(assembly.GetName().Name!.ToLowerInvariant(), package.Id);
        Assert.Equal(expected, package.Version);
    }

    [Fact]
    public void InvalidInformationalVersionAndEmptyAssemblyAreRefused()
    {
        var builder = new ServiceCollection().AddIxIFlow();
        Assert.Throws<WorkflowEngineException>(() => builder.AddActivities(
            AssemblyWithActivity("Local.Invalid." + Guid.NewGuid().ToString("N"), "invalid")));
        Assert.Throws<WorkflowEngineException>(() => builder.AddActivities(typeof(object).Assembly));
    }

    [Fact]
    public void AssemblyTypeLoadFailuresNameTheAssemblyAndFirstLoaderError()
    {
        var name = "Local.MissingDependency." + Guid.NewGuid().ToString("N");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(IAsyncActivity).Assembly.Location).Distinct()
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        static byte[] Compile(string name, string source, IEnumerable<MetadataReference> references)
        {
            using var stream = new MemoryStream();
            var result = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(stream);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return stream.ToArray();
        }
        var dependency = Compile(name, """
            public class MissingBase : IxIFlow.Core.IAsyncActivity
            {
                public System.Threading.Tasks.Task ExecuteAsync(IxIFlow.Core.IActivityContext context,
                    System.Threading.CancellationToken token) => System.Threading.Tasks.Task.CompletedTask;
            }
            """, references);
        var bytes = Compile(name + ".Activities", """
            [IxIFlow.ActivitySdk.WorkflowActivity("missing-dependency")]
            public sealed class BrokenActivity : MissingBase { }
            """, references.Append(MetadataReference.CreateFromImage(dependency)));
        using var image = new MemoryStream(bytes);
        var assembly = AssemblyLoadContext.Default.LoadFromStream(image);
        var loader = Assert.Throws<ReflectionTypeLoadException>(() => assembly.GetTypes());
        var error = Assert.Throws<WorkflowEngineException>(() => new ServiceCollection().AddIxIFlow().AddActivities(assembly));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains(assembly.GetName().Name!, error.Message);
        Assert.Contains(loader.LoaderExceptions[0]!.Message, error.Message);
    }

    [Theory]
    [InlineData(typeof(Unmarked))]
    [InlineData(typeof(Invisible))]
    [InlineData(typeof(Abstract))]
    [InlineData(typeof(EmptyKey))]
    [InlineData(typeof(EmptyVersion))]
    [InlineData(typeof(BadInput))]
    [InlineData(typeof(BadOutput))]
    [InlineData(typeof(BadInOut))]
    [InlineData(typeof(BadDefault))]
    [InlineData(typeof(BadControl))]
    public void InvalidSingleDeclarationIsRefusedAndNamesTheType(Type type)
    {
        var builder = new ServiceCollection().AddIxIFlow();
        var method = typeof(ActivityBuilderExtensions).GetMethod(nameof(ActivityBuilderExtensions.AddActivity))!;
        var error = Assert.Throws<TargetInvocationException>(() => method.MakeGenericMethod(type).Invoke(null, [builder]));
        Assert.Contains(type.Name, Assert.IsType<WorkflowEngineException>(error.InnerException).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericDeclarationsAreRefusedAndNameTheType(bool closed)
    {
        var assembly = AssemblyWithActivity("Local.Generic." + Guid.NewGuid().ToString("N"), "1.0.0", generic: true);
        var type = Assert.Single(assembly.GetTypes());
        var builder = new ServiceCollection().AddIxIFlow();
        WorkflowEngineException error;
        if (closed)
        {
            type = type.MakeGenericType(typeof(int));
            var method = typeof(ActivityBuilderExtensions).GetMethod(nameof(ActivityBuilderExtensions.AddActivity))!;
            error = Assert.IsType<WorkflowEngineException>(Assert.Throws<TargetInvocationException>(() =>
                method.MakeGenericMethod(type).Invoke(null, [builder])).InnerException);
        }
        else error = Assert.Throws<WorkflowEngineException>(() => builder.AddActivities(assembly));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains(type.FullName!, error.Message);
    }

    [Fact]
    public void BuildChecksDeclarationsWithoutCatalogControlsOrDefaultSerialization()
    {
        Workflow.Create<Data>("ControlIsMetadata").Step<BadControl>().Build();
        Workflow.Create<Data>("NoDefaultSerialization").Step<UnsavableDefault>().Build();
    }

    [Fact]
    public void CatalogDefaultsUseTheInputPropertyConverter()
    {
        var services = new ServiceCollection();
        services.AddIxIFlow().AddActivity<ConvertedDefault>();
        using var provider = services.BuildServiceProvider();
        var member = Assert.Single(provider.GetRequiredService<IActivityRegistry>().Find("local.converted-default")!.Inputs);
        Assert.Equal("\"converted:12\"", member.DefaultJson);
        var property = typeof(ConvertedDefault).GetProperty(nameof(ConvertedDefault.Value))!;
        var (saved, copy) = ActivityInputValue.Of(property, ActivityContract.Default(property), provider);
        Assert.Equal(member.DefaultJson, saved.Value!.Json.InlineText);
        Assert.Equal(12, Assert.IsType<int>(copy));
    }

    [Fact]
    public void AssemblyScanRefusesAttributedIneligibleTypesRatherThanFilteringThem()
    {
        var assembly = AssemblyWithActivity("Local.Scan." + Guid.NewGuid().ToString("N"), "1.0.0", eligible: false);
        var error = Assert.Throws<WorkflowEngineException>(() => new ServiceCollection().AddIxIFlow().AddActivities(assembly));
        Assert.Contains("Activity", error.Message);
    }

    [Fact]
    public void DuplicateKeyAndDuplicatePackageIdAreRefusedDuringConfiguration()
    {
        var builder = new ServiceCollection().AddIxIFlow().AddActivity<Injected>();
        Assert.Throws<WorkflowEngineException>(() => builder.AddActivity<SameKey>());
        var name = "Local.Duplicate." + Guid.NewGuid().ToString("N");
        builder.AddActivities(AssemblyWithActivity(name, "1.0.0", key: "first"));
        Assert.Throws<WorkflowEngineException>(() => builder.AddActivities(
            AssemblyWithActivity(name.ToLowerInvariant(), "1.0.0", key: "second")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonDefaultLoadContextsAreRefused(bool collectible)
    {
        var context = new AssemblyLoadContext("local-refusal", collectible);
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(ReadJsonActivity).Assembly.Location);
            Assert.Throws<WorkflowEngineException>(() => new ServiceCollection().AddIxIFlow().AddActivities(assembly));
        }
        finally { if (collectible) context.Unload(); }
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton, false)]
    [InlineData(ServiceLifetime.Scoped, false)]
    [InlineData(ServiceLifetime.Transient, true)]
    public void FinalServiceCollectionIsValidatedAlsoByStandaloneExecutors(ServiceLifetime lifetime, bool duplicate)
    {
        var services = new ServiceCollection();
        services.AddIxIFlow().AddActivity<Injected>();
        if (!duplicate) services.RemoveAll<Injected>();
        services.Add(new ServiceDescriptor(typeof(Injected), typeof(Injected), lifetime));
        using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<WorkflowEngineException>(() => new ActivityExecutor(provider, NullLogger<ActivityExecutor>.Instance));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("exactly one transient", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyedDescriptorsDoNotCountAsUnkeyedActivityRegistrations(bool unkeyed)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Dependency());
        services.AddIxIFlow().AddActivity<Injected>();
        if (unkeyed) services.AddKeyedSingleton<Injected>("unrelated");
        else
        {
            services.RemoveAll<Injected>();
            services.AddKeyedTransient<Injected>("only-keyed");
        }
        using var provider = services.BuildServiceProvider();
        if (unkeyed)
        {
            _ = new ActivityExecutor(provider, NullLogger<ActivityExecutor>.Instance);
            Assert.IsType<Injected>(ActivityActivation.Create(typeof(Injected), provider, provider.GetRequiredService<IActivityRegistry>()));
        }
        else
        {
            var error = Assert.Throws<WorkflowEngineException>(() => new ActivityExecutor(provider, NullLogger<ActivityExecutor>.Instance));
            Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
            Assert.Contains(typeof(Injected).FullName!, error.Message);
        }
    }

    [Fact]
    public void SharedFactoryInstancesAreRefusedAcrossScopesAndProviders()
    {
        var shared = new Injected(new Dependency());
        var services = new ServiceCollection();
        services.AddTransient(_ => shared);
        services.AddIxIFlow().AddActivity<Injected>();
        using var first = services.BuildServiceProvider();
        using var second = services.BuildServiceProvider();
        var registry = first.GetRequiredService<IActivityRegistry>();
        Assert.Same(shared, ActivityActivation.Create(typeof(Injected), first, registry));
        var error = Assert.Throws<WorkflowEngineException>(() => ActivityActivation.Create(typeof(Injected), second,
            second.GetRequiredService<IActivityRegistry>()));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
    }

    [Fact]
    public void DisposableRegisteredActivitiesAreOwnedByTheirRunScope()
    {
        var services = new ServiceCollection();
        services.AddIxIFlow().AddActivity<Disposable>();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IActivityRegistry>();
        Disposable activity;
        using (var scope = provider.CreateScope())
        {
            activity = Assert.IsType<Disposable>(ActivityActivation.Create(typeof(Disposable), scope.ServiceProvider, registry));
            Assert.False(activity.Disposed);
        }
        Assert.True(activity.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConstructorInjectionAndApplicationFactoriesRunInChildAndConcurrentSteps(bool factory)
    {
        var dependency = new Dependency();
        var services = new ServiceCollection();
        services.AddSingleton(dependency);
        if (factory) services.AddTransient(_ => new Injected(dependency) { Factory = true });
        services.AddIxIFlow().AddActivity<Injected>();
        services.RegisterWorkflow<Child, Data>();
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<Data>("LocalParent").Step<Injected>()
            .Invoke<Child, Data>()
            .Parallel(parallel => parallel.Do(branch => branch.Step<Injected>(step => step
                    .Input(activity => activity.Parallel).From(_ => true)))
                .Do(branch => branch.Step<Injected>(step => step
                    .Input(activity => activity.Parallel).From(_ => true)))).Build();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(definition, new Data());
        result = await client.AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(4, dependency.Instances.Count);
        Assert.All(dependency.Instances, instance => Assert.Equal(factory, instance.Factory));
        Assert.Equal(4, dependency.Instances.Distinct().Count());
        Assert.True(dependency.MaxConcurrent >= 2);
    }

    [Fact]
    public void RestorationUsesTheApplicationFactoryAndRestoresSavedProperties()
    {
        var dependency = new Dependency();
        var services = new ServiceCollection();
        services.AddTransient(_ => new Injected(dependency) { Factory = true });
        services.AddIxIFlow().AddActivity<Injected>();
        using var provider = services.BuildServiceProvider();
        var restored = Assert.IsType<Injected>(SerializedValue.ReadActivity(typeof(Injected), "{\"Value\":42}", provider));
        Assert.True(restored.Factory);
        Assert.Equal(42, restored.Value);
        Assert.NotSame(restored, SerializedValue.ReadActivity(typeof(Injected), "{\"Value\":42}", provider));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrashRecoveryCreatesANewInjectedActivityAndHonoursTheFactory(bool factory)
    {
        var probe = new RecoveryProbe();
        var store = new FaultInjectingRepository { FailCommit = _ => probe.Executions != 0 };
        ServiceProvider Host()
        {
            var services = new ServiceCollection();
            services.AddSingleton(probe);
            if (factory) services.AddTransient(_ => new Recoverable(probe) { Factory = true });
            services.AddIxIFlow().AddActivity<Recoverable>();
            services.AddSingleton<IWorkflowStateRepository>(store);
            return services.BuildServiceProvider();
        }
        var definition = Workflow.Create<DemoData>("LocalCrashRecovery").Step<Recoverable>(step => step
            .Output(activity => activity.Result!).To(context => context.WorkflowData.Json)).Build();
        using (var first = Host())
            await EngineAssert.StoreFailureAsync(() => first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new DemoData()));
        var row = Assert.Single((await store.QueryInstancesAsync(new InstanceQuery(Statuses: [WorkflowStatus.Running]))).Items);
        store.FailCommit = _ => false;
        using var second = Host();
        await second.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var recovered = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(row.InstanceId);
        Assert.True(recovered.Status == WorkflowExecutionStatus.Success, recovered.ErrorMessage);
        Assert.Equal("recovered", Assert.IsType<DemoData>(recovered.WorkflowData).Json);
        Assert.Equal(1, probe.Executions);
        Assert.Equal(1, probe.Recoveries);
        Assert.True(probe.Objects.Count >= 2);
        Assert.Equal(probe.Objects.Count, probe.Objects.Distinct().Count());
        Assert.All(probe.Objects, activity => Assert.Equal(factory, activity.Factory));
    }

    [Fact]
    public async Task DemoAssemblyRunsQueryCustomerWithAConstructorInjectedFactory()
    {
        var factory = new DemoConnectionFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IDemoConnectionFactory>(factory);
        services.AddIxIFlow().AddActivities(typeof(QueryCustomerActivity).Assembly);
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<DemoData>("LocalDemoQuery").Step<QueryCustomerActivity>(step => step
            .Input(activity => activity.CustomerId).From(_ => "customer-1")
            .Output(activity => activity.CustomerJson).To(context => context.WorkflowData.Json)).Build();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new DemoData());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal("customer-db", factory.Reference);
        Assert.Equal("Ada", JsonDocument.Parse(Assert.IsType<DemoData>(result.WorkflowData).Json!).RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task HeartbeatPublishesPackagesInitiallyAndAfterPruningWithoutChangingCapabilities()
    {
        var services = new ServiceCollection();
        services.AddIxIFlow().AddActivity<Metadata>();
        using var provider = services.BuildServiceProvider();
        var activities = provider.GetRequiredService<IActivityRegistry>();
        var time = new TimerRecordingClock();
        var storeTime = new FakeTimeProvider(time.GetUtcNow());
        var store = new ObservedStore(storeTime);
        var catalog = new WorkflowCatalog(store);
        var revision = catalog.DefinitionRevision;
        var settings = HostSettings.Default;
        using var heartbeat = new WorkflowHeartbeatService(store, catalog, settings, time,
            NullLogger<WorkflowHeartbeatService>.Instance, activities: activities);
        await heartbeat.StartAsync(default);
        try
        {
            var initial = await store.Registrations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await time.WaitForTimerAsync(timer => timer.Period == settings.HeartbeatInterval && timer.Pending);
            var package = Assert.Single(await store.GetInstalledPackagesAsync());
            Assert.Equal(Assert.Single(activities.Packages).Fingerprint, package.Fingerprint);
            Assert.Equal(settings.Id, Assert.Single(package.Hosts));
            var capabilities = (await store.GetLiveMembersAsync()).Single().Capabilities;
            Assert.Equal((await WorkflowHeartbeatService.CapabilitiesAsync(catalog, settings)).OrderBy(pair => pair.Key), capabilities.OrderBy(pair => pair.Key));
            storeTime.Advance(TimeSpan.FromHours(2));
            Assert.Empty(Assert.Single(await store.GetInstalledPackagesAsync()).Hosts);
            time.Advance(settings.HeartbeatInterval);
            var restarted = await store.Registrations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(initial.Id, restarted.Id);
            Assert.Equal(activities.Packages, restarted.InstalledPackages);
            Assert.Equal(settings.Id, Assert.Single(Assert.Single(await store.GetInstalledPackagesAsync()).Hosts));
            Assert.Equal(revision, catalog.DefinitionRevision);
        }
        finally { await heartbeat.StopAsync(default); }
        Assert.Empty(Assert.Single(await store.GetInstalledPackagesAsync()).Hosts);
    }

    private sealed class ObservedStore(TimeProvider time) : InMemoryWorkflowStateRepository(time), IWorkflowStateRepository
    {
        public Channel<MemberRegistration> Registrations { get; } = Channel.CreateUnbounded<MemberRegistration>();
        async Task IWorkflowStateRepository.RegisterMemberAsync(MemberRegistration member, CancellationToken token)
        {
            await base.RegisterMemberAsync(member, token);
            Registrations.Writer.TryWrite(member);
        }
    }

    [Fact]
    public void BundleActivitiesNeverQueryTheHostContainerAndRemainCollectible()
    {
        using var provider = new ServiceCollection().AddIxIFlow().Services.BuildServiceProvider();
        var weak = ActivateBundle(provider.GetRequiredService<IActivityRegistry>());
        for (var i = 0; i < 20 && weak.IsAlive; i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.False(weak.IsAlive);
        GC.KeepAlive(provider);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ActivateBundle(IActivityRegistry registry)
    {
        var context = new AssemblyLoadContext("local-direct-bundle", true);
        context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == name.Name);
        var assembly = context.LoadFromAssemblyPath(typeof(ReadJsonActivity).Assembly.Location);
        var type = assembly.GetType(typeof(ReadJsonActivity).FullName!)!;
        var activity = ActivityActivation.Create(type, new RejectCollectibleServices(), registry);
        Assert.Equal(type, activity.GetType());
        var weak = new WeakReference(context);
        context.Unload();
        return weak;
    }

    private sealed class RejectCollectibleServices : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            Assert.False(serviceType.Assembly.IsCollectible);
            return null;
        }
    }

    private static Assembly AssemblyWithActivity(string name, string? informational, bool eligible = true, string key = "dynamic", bool generic = false)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name) { Version = new Version(7, 8, 9, 0) }, AssemblyBuilderAccess.Run);
        if (informational != null) assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [informational]));
        var type = assembly.DefineDynamicModule(name).DefineType("Activity", TypeAttributes.Public | (eligible ? 0 : TypeAttributes.Abstract), typeof(Unmarked));
        if (generic) type.DefineGenericParameters("T");
        type.SetCustomAttribute(new CustomAttributeBuilder(typeof(WorkflowActivityAttribute).GetConstructor([typeof(string)])!, [key]));
        type.CreateType();
        return assembly;
    }

    public class Unmarked : IAsyncActivity
    {
        public virtual Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    [WorkflowActivity("invisible")] private sealed class Invisible : Unmarked;
    [WorkflowActivity("abstract")] public abstract class Abstract : Unmarked;
    [WorkflowActivity("")] public sealed class EmptyKey : Unmarked;
    [WorkflowActivity("empty-version", Version = "")] public sealed class EmptyVersion : Unmarked;
    [WorkflowActivity("bad-input")] public sealed class BadInput : Unmarked { [ActivityInput] public int Value { get; } }
    [WorkflowActivity("bad-output")] public sealed class BadOutput : Unmarked { [ActivityOutput] public int Value { private get; set; } }
    [WorkflowActivity("bad-inout")] public sealed class BadInOut : Unmarked { [ActivityInputOutput] public int Value { get; } }
    [WorkflowActivity("bad-default")] public sealed class BadDefault : Unmarked { [DefaultValue("bad")] public int Value { get; set; } }
    [WorkflowActivity("bad-control")] public sealed class BadControl : Unmarked { [UIHint("unknown")] public int Value { get; set; } }
    [WorkflowActivity("local.injected")] public sealed class SameKey : Unmarked;
    [WorkflowActivity("local.converted-default")]
    public sealed class ConvertedDefault : Unmarked
    {
        [ActivityInput, DefaultValue("12"), JsonConverter(typeof(DefaultConverter))]
        public int Value { get; set; }
    }
    public sealed class DefaultConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            int.Parse(reader.GetString()!["converted:".Length..], System.Globalization.CultureInfo.InvariantCulture);
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
            writer.WriteStringValue("converted:" + value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    public sealed class UnsavableDefault : Unmarked
    {
        [ActivityInput, DefaultValue(12), JsonConverter(typeof(ThrowingConverter))]
        public int Value { get; set; }
    }
    public sealed class ThrowingConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("Build must not deserialize defaults");
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
            throw new NotSupportedException("Build must not serialize defaults");
    }
    [WorkflowActivity("local.disposable")]
    public sealed class Disposable : Unmarked, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
    [WorkflowActivity("local.metadata")]
    public sealed class Metadata : Unmarked
    {
        [ActivityInput, Required, DefaultValue("12"), Display(Name = "Amount", Description = "Converted amount"), UIHint("code")]
        public int Converted { get; set; }
        [ActivityInput] public string? NoDefault { get; set; }
        [ActivityInput, DefaultValue(null)] public string? NullDefault { get; set; }
    }
    public sealed class Dependency
    {
        public System.Collections.Concurrent.ConcurrentBag<Injected> Instances { get; } = [];
        private int _active;
        private int _arrivals;
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaxConcurrent;
        public async Task Run(Injected activity)
        {
            Instances.Add(activity);
            var active = Interlocked.Increment(ref _active);
            int prior;
            do { prior = MaxConcurrent; } while (active > prior && Interlocked.CompareExchange(ref MaxConcurrent, active, prior) != prior);
            if (activity.Parallel)
            {
                if (Interlocked.Increment(ref _arrivals) == 2) _bothArrived.TrySetResult();
                await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Interlocked.Decrement(ref _active);
        }
    }
    [WorkflowActivity("local.injected")]
    public sealed class Injected(Dependency dependency) : IAsyncActivity
    {
        [System.Text.Json.Serialization.JsonIgnore] public bool Factory { get; set; }
        public int Value { get; set; }
        public bool Parallel { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => dependency.Run(this);
    }
    public sealed class Data;
    public sealed class DemoData { public string Json { get; set; } = ""; }
    public sealed class RecoveryProbe
    {
        public int Executions;
        public int Recoveries;
        public List<Recoverable> Objects { get; } = [];
    }
    [WorkflowActivity("local.recoverable")]
    public sealed class Recoverable : IRecoverableActivity<string>
    {
        private readonly RecoveryProbe _probe;
        public Recoverable(RecoveryProbe probe) { _probe = probe; probe.Objects.Add(this); }
        [System.Text.Json.Serialization.JsonIgnore] public bool Factory { get; set; }
        [ActivityOutput] public string? Result { get; private set; }
        public string CaptureRecoveryState(IActivityContext context) => context.InvocationId;
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { _probe.Executions++; Result = "executed"; return Task.CompletedTask; }
        public Task<ActivityRecoveryResult<string>> RecoverAsync(string state, IActivityContext context, CancellationToken cancellationToken = default)
        { _probe.Recoveries++; Result = "recovered"; return Task.FromResult<ActivityRecoveryResult<string>>(new ActivityRecoveryResult<string>.Completed()); }
    }
    private sealed class DemoConnectionFactory : IDemoConnectionFactory
    {
        public string? Reference;
        public async Task<System.Data.Common.DbConnection> OpenAsync(string connectionRef, CancellationToken cancellationToken)
        {
            Reference = connectionRef;
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE customers (id TEXT, name TEXT); INSERT INTO customers VALUES ('customer-1', 'Ada');";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
    }
    [Workflow("LocalChild", Version = 1)]
    public sealed class Child : IxIFlow.Builders.Interfaces.IWorkflow<Data>
    {
        public void Build(IxIFlow.Builders.Interfaces.IWorkflowBuilder<Data> builder) => builder.Step<Injected>();
    }
}

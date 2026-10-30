using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
public sealed class PrereleaseSlice2CoreTests
{
    [Fact]
    public async Task CatalogConstructionDoesNoStoreIoAndAsyncLookupInitializesOnce()
    {
        var store = new Mock<IWorkflowCatalogStore>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        store.Setup(s => s.RegisterWorkflowVersionAsync(It.IsAny<WorkflowVersion>(), It.IsAny<IArtifactStore?>(),
                cancellationToken: Moq.It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                entered.TrySetResult();
                await release.Task;
            });

        var definition = Workflow.Create<Data>("AsyncCatalog").Step<Noop>().Build();
        using var catalog = new WorkflowCatalog(store.Object, [definition]);

        store.Verify(
            s => s.RegisterWorkflowVersionAsync(It.IsAny<WorkflowVersion>(), It.IsAny<IArtifactStore?>(),
                cancellationToken: Moq.It.IsAny<CancellationToken>()), Times.Never);

        var lookup = catalog.GetWorkflowDefinitionAsync(definition.Name, 1);
        var enumeration = catalog.GetWorkflowDefinitionsAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(lookup.IsCompleted);
        Assert.False(enumeration.IsCompleted);
        release.SetResult();
        Assert.Same(definition, await lookup);
        Assert.Single(await enumeration);

        store.Verify(
            s => s.RegisterWorkflowVersionAsync(It.IsAny<WorkflowVersion>(), It.IsAny<IArtifactStore?>(),
                cancellationToken: Moq.It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RegistrationUsesTheAlreadyValidatedExecutableCatalog()
    {
        var definition = Workflow.Create<Data>("CachedRegistration").Delay(TimeSpan.FromSeconds(1)).Build();
        var validated = WorkflowDefinitionValidator.Inspect(definition);

        // Public definitions are mutable; execution uses the catalog frozen at validation.
        definition.Steps[0].DelayDuration = TimeSpan.FromSeconds(2);
        using var catalog = new WorkflowCatalog();
        await catalog.RegisterWorkflowAsync(definition);
        Assert.Equal(validated, await catalog.GetWorkflowStructureHashAsync(definition.Name, 1));
    }

    [Theory]
    [InlineData("long")]
    [InlineData("nul")]
    public void HostNamesAreCheckedAtRegistration(string kind)
    {
        var name = kind == "long" ? new string('x', 201) : "host\0name";
        var error = Record.Exception(() => new ServiceCollection().AddIxIFlow(options => options.HostName = name));
        Assert.NotNull(error);
        Assert.True(error is WorkflowEngineException or ArgumentException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootGraphKeepsDeclarationsAndExecutableHash(bool machine)
    {
        WorkflowDefinition Build(bool declare)
        {
            var root = Workflow.Create<Data>("GraphContract");

            if (declare)
                root.Input(d => d.Input).Output(d => d.Output);

            return machine
                ? root.StateMachine(m => m.Start("done").State("done", s => s.Final())).Build()
                : root.Flowchart(f => f.Start("done").Node("done")).Build();
        }

        var declared = Build(true);
        var implicitDefinition = Build(false);
        Assert.Equal([nameof(Data.Input)], declared.DeclaredInputs);
        Assert.Equal([nameof(Data.Output)], declared.DeclaredOutputs);
        var contract = WorkflowDefinitionValidator.GetContract(declared);
        Assert.Equal(nameof(Data.Input), Assert.Single(contract.Inputs).Name);
        Assert.Equal(nameof(Data.Output), Assert.Single(contract.Outputs).Name);
        Assert.Equal(WorkflowDefinitionValidator.Inspect(implicitDefinition), WorkflowDefinitionValidator.Inspect(declared));
    }

    [Fact]
    public void InvalidSingleGenericRegistrationThrowsTheDefinitionError()
    {
        var services = new ServiceCollection();
        services.AddIxIFlow();
        services.RegisterWorkflow<InvalidWorkflow>();
        using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<WorkflowEngineException>(() => provider.GetServices<WorkflowDefinition>().ToArray());
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(1, 0.5)]
    [InlineData(1, double.NaN)]
    public void SagaRetryPolicyIsValidatedBeforeExecution(int retries, double coefficient)
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("InvalidSagaPolicy")
            .Step<Noop>().Saga(s => s.Step<Noop>())
            .OnError(h => h.Compensate().ThenRetry(new RetryPolicy { MaximumAttempts = retries, BackoffCoefficient = coefficient }))
            .Build());

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
    }

    [Fact]
    public void NegativeSagaRetryCountIsRejectedByTheIntegerOverload()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("NegativeSagaCount")
            .Step<Noop>().Saga(s => s.Step<Noop>()).OnError(h => h.Compensate().ThenRetry(-1)).Build());

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
    }

    [Fact]
    public void SagaRetryRequiresAPolicyAtBuild()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("MissingSagaPolicy")
            .Step<Noop>().Saga(s => s.Step<Noop>()).OnError(h => h.Compensate().ThenRetry((RetryPolicy)null!)).Build());

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
    }

    [Fact]
    public void ScopeInputBuilderKeepsRetryAndThreadingConfiguration()
    {
        var policy = new RetryPolicy(2);

        var definition = new WorkflowBuilder<Data, object>([], "ScopeInputOptions")
            .Scope<Container>(s => s.Input(x => x.Name).From(c => c.WorkflowData.Input)
                .Retry(policy).Threading(ScopeThreading.ThreadAffine), _ =>
            {
            }).Build();

        var step = Assert.Single(definition.Steps);
        Assert.Same(policy, step.ScopeRetryPolicy);
        Assert.Equal(ScopeThreading.ThreadAffine, step.ScopeThreading);
    }

    [Fact]
    public void TimeoutBodyRefusesRootOnlyConfiguration()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<Data, object>([], "TimeoutRoot")
            .WaitFor<Go>("go", configure: wait => wait.TimeoutAfter(TimeSpan.FromSeconds(1))
                .OnTimeout(body => body.DisplayName("lost"))).Build());

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
    }

    [Fact]
    public void NullWebhookSignatureIsNotValid() => Assert.False(StandardWebhookSignature.Verify(
        [new byte[32]], "id", "1", null!, [], DateTimeOffset.FromUnixTimeSeconds(1), TimeSpan.FromMinutes(1)));

    [Fact]
    public void MissingWorkflowDataTypeHasAnExplicitNullableContract()
    {
        Assert.Null(new WorkflowDefinition().WorkflowDataType);
        var property = typeof(WorkflowDefinition).GetProperty(nameof(WorkflowDefinition.WorkflowDataType))!;
        Assert.Equal(NullabilityState.Nullable, new NullabilityInfoContext().Create(property).ReadState);
    }

    [Fact]
    public void SwitchTableIsExcludedFromDefinitionJson() => Assert.NotNull(typeof(WorkflowDefinition)
        .GetProperty(nameof(WorkflowDefinition.SwitchTable))!.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>());

    [Fact]
    public void AStoredNonGenericTypeWithGenericArgumentsIsIgnored()
    {
        var saved = WorkflowTypeIdentity.SavedName(typeof(List<int>));
        var generic = typeof(List<>).FullName!;
        var nongeneric = typeof(string).FullName!;
        saved = saved.Replace($"{generic.Length}:{generic}", $"{nongeneric.Length}:{nongeneric}", StringComparison.Ordinal);
        Assert.Null(WorkflowTypeIdentity.Resolve(saved));
    }

    [Fact]
    public void NullCombinedCollectionsHaveConflictDiagnostics()
    {
        Assert.Contains("cannot be appended", Assert.Throws<JoinConflictException>(() => new Append<int>().Combine([1], [null!])).Message);

        Assert.Contains("cannot be merged",
            Assert.Throws<JoinConflictException>(() => new Union<string, int>().Combine([], [null!])).Message);

        Assert.Contains("cannot be merged", Assert.Throws<JoinConflictException>(() => new Union<int>().Combine([1], [null!])).Message);
    }

    [Theory]
    [InlineData("Enumerable.Range(0, int.MaxValue).Count() > 0")]
    [InlineData("Enumerable.Repeat(1, int.MaxValue).Count() > 0")]
    [InlineData("Enumerable.Range(count: int.MaxValue, start: 0).Count() > 0")]
    [InlineData("Enumerable.Range(0, 1000).Select(i => Enumerable.Range(0, 1000).Count()).Sum() > 0")]
    [InlineData("context.WorkflowData.Items.SelectMany(i => context.WorkflowData.Items).Count() > 0")]
    public void BreakpointSequenceEvaluationHasAWorkBudget(string condition)
    {
        using var compiler = new BreakpointConditionCompiler();
        var compiled = compiler.Compile(new("hash", condition, typeof(Data)));
        Assert.Empty(compiled.Diagnostics);
        Assert.NotNull(compiled.Evaluate);

        Assert.Throws<TimeoutException>(() =>
            compiled.Evaluate(new Data { Items = Enumerable.Range(0, 1000).ToList() }, null, null, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void BreakpointEvaluationTimeBudgetUsesItsInjectedClock()
    {
        using var compiler = new BreakpointConditionCompiler(null, new AdvancingClock());
        var compiled = compiler.Compile(new("hash", "Enumerable.Range(0, 1).Any()", typeof(Data)));
        Assert.Empty(compiled.Diagnostics);
        Assert.Throws<TimeoutException>(() => compiled.Evaluate!(new Data(), null, null, null, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("Enumerable.Repeat(context.WorkflowData.Items, 2).All(items => items[0] == 1)")]
    [InlineData("Enumerable.Count(predicate: (int i) => i == 1, source: context.WorkflowData.Items) > 0")]
    [InlineData("Enumerable.Range(0, 2).All(__budgetItem => Enumerable.Range(0, 2).All(i => i >= 0))")]
    [InlineData("Enumerable.Range(0, 2).All(__budget => __budget >= 0)")]
    [InlineData("Enumerable.Range(0, 2).All(__count => __count >= 0)")]
    public void BreakpointBudgetsPreserveSequenceValuedElements(string condition)
    {
        using var compiler = new BreakpointConditionCompiler();
        var compiled = compiler.Compile(new("hash", condition, typeof(Data)));
        Assert.Empty(compiled.Diagnostics);
        Assert.True(compiled.Evaluate!(new Data { Items = [1] }, null, null, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void MalformedElfStringTableIsArtifactMismatch()
    {
        var bytes = new byte[160];
        "\u007fELF"u8.CopyTo(bytes);
        bytes[4] = 2;
        bytes[5] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(54), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(72), 120);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(96), 32);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(120), 1);
        var error = Assert.Throws<WorkflowEngineException>(() => WorkflowNativeImports.Read(bytes));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
    }

    [Fact]
    public async Task BundleIdleCapacityDoesNotCountRunningVersions()
    {
        var store = new InMemoryWorkflowStateRepository();
        var one = await PublishedBundleScenario.PublishAsync(store, 1);
        var two = await PublishedBundleScenario.PublishAsync(store, 2);
        using var loader = new WorkflowBundleLoader(store, new(Folder(), 1));
        using var active = await loader.AcquireAsync(one.ArtifactDigest!.Value, default);
        WorkflowDefinition cached;

        using (var idle = await loader.AcquireAsync(two.ArtifactDigest!.Value, default))
            cached = idle.Definition;

        using var again = await loader.AcquireAsync(two.ArtifactDigest!.Value, default);
        Assert.Same(cached, again.Definition);
    }

    [Fact]
    public async Task CacheLockFailureIsLoggedAndArtifactFetchStillWorks()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var folder = Folder();
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        await File.WriteAllTextAsync(folder, "file blocks cache directory");
        var logger = new Logger();

        try
        {
            var cache = new BundleCache(store, folder, new(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), logger);
            Assert.NotNull(await cache.ReadAsync(version.ArtifactDigest!.Value, default));
            Assert.Contains(logger.Messages, message => message.Contains("cache lock", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(folder);
        }
    }

    [Fact]
    public async Task FailedDemandCapabilityRefreshIsLogged()
    {
        var catalog = new Mock<IWorkflowCatalog>();

        catalog.Setup(c => c.GetWorkflowDefinitionsAsync())
            .ThrowsAsync(new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "store offline"));

        var logger = new Logger();
        var settings = HostSettings.Default with { CapabilityLogger = logger };
        Assert.Null(await settings.RefreshDemandCapabilitiesAsync(catalog.Object, TimeProvider.System));
        Assert.Contains(logger.Messages, message => message.Contains("refresh workflow placement", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShutdownDisposesTheHostOutboxAndCatalogSynchronizationResources()
    {
        var collection = new ServiceCollection();
        collection.AddIxIFlow();
        await using var provider = collection.BuildServiceProvider();
        var host = provider.GetRequiredService<WorkflowHost>();
        await ((IAsyncDisposable)host).DisposeAsync();
        host.Dispose();
        AssertSemaphoreDisposed(host, "_slots");
        using var catalog = new WorkflowCatalog();
        catalog.Dispose();
        AssertSemaphoreDisposed(catalog, "_registrationLock");

        using var outbox = new WorkflowOutboxService(new InMemoryWorkflowStateRepository(), [], HostSettings.Default,
            new(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowOutboxService>.Instance);

        outbox.Dispose();
        AssertSemaphoreDisposed(outbox, "_slots");

        static void AssertSemaphoreDisposed(object owner, string name)
        {
            var semaphore = (SemaphoreSlim)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            Assert.Throws<ObjectDisposedException>(() => semaphore.Wait(0));
        }
    }

    private static string Folder() => Path.Combine(Path.GetTempPath(), "IxIFlow.Slice2", Guid.NewGuid().ToString("N"));

    private sealed class AdvancingClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref _timestamp, TimeSpan.FromMilliseconds(101).Ticks);
    }

    public sealed class Data
    {
        public string Input { get; set; } = "";
        public string Output { get; set; } = "";
        public List<int> Items { get; set; } = [];
    }

    public sealed class Go;

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken token) => Task.CompletedTask;
    }

    public sealed class ScopeValue;

    public sealed class Container : IScopeActivity<ScopeValue>
    {
        public string Name { get; set; } = "";

        public Task<ScopeValue> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token) =>
            Task.FromResult(new ScopeValue());

        public Task CloseAsync(ScopeValue value, ScopeClosing closing, IScopeContext context, CancellationToken token) =>
            Task.CompletedTask;
    }

    [Workflow("InvalidDefinitionRegistration", Version = 1)]
    public sealed class InvalidWorkflow : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> builder) =>
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, "definition error");
    }

    private sealed class Logger : ILogger<WorkflowHeartbeatService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) =>
            Messages.Add(format(state, error));
    }
}
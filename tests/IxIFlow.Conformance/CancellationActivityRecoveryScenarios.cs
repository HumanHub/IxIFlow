using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "cancellation"), Trait("Spec", "W-06")]
public sealed class CancellationActivityRecoveryScenarios
{
    public static IEnumerable<object[]> Boundaries() => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { false, true }.SelectMany(end => new[] { false, true }.Select(lost => new object[] { profile, end, lost })));

    [Theory(Timeout = 120_000), MemberData(nameof(Boundaries))]
    public async Task CancellationHandlerActivityRecoversItsInvocationAtStartAndEndBoundaries(string profile, bool end, bool lost)
    {
        await using var store = await StoreProfiles.CreateAsync(profile); var repository = store.NewRepository();
        var catalog = new WorkflowCatalog(); var effects = new Effects();
        var definition = new WorkflowBuilder<Data, object>([], "CancellationActivityBoundary").WaitFor<Go>("start")
            .Try(t => t.CancellationScope(b => b.Throw<InvalidOperationException>(c => new("original")),
                h => h.Step<Cleanup>().WaitFor<Go>("cleanup")))
            .Catch<InvalidOperationException>(c => c.Assign(x => x.WorkflowData.Caught, x => true)).Build();
        await catalog.RegisterWorkflowAsync(definition); var injecting = false; var interrupted = false; var block = false;
        bool Interrupt(WorkflowInstance instance)
        {
            if (!injecting || interrupted) return false;
            var candidate = Read(repository, instance).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            var branch = candidate.Continuations[0];
            if (!branch.Stack.Any(f => f.CancellationState?.Phase == CancellationScopePhase.Handler)) return false;
            var match = end ? effects.Invocations.Count == 1 && branch.PendingActivity == null : branch.PendingActivity != null;
            if (!match) return false;
            interrupted = true; block = lost; return true;
        }
        var faults = new FaultRepository(repository)
        { FailCommit = instance => injecting && !lost && (interrupted || Interrupt(instance)) || block,
            LoseAcknowledgement = instance => lost && Interrupt(instance) };
        string id;
        await using (var first = await Services(store, faults, catalog, effects))
        {
            var client = first.GetRequiredService<IWorkflowClient>(); id = (await client.RunAsync(definition, new Data())).InstanceId;
            injecting = true;
            Assert.IsType<WorkflowEngineException>(await Record.ExceptionAsync(() => client.ResumeAsync(id, "start", new Go())));
            Assert.True(interrupted); injecting = false;
        }
        if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();
        await using var second = await Services(store, store.NewRepository(), catalog, effects);
        var recovered = second.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await recovered.RecoverAsync(id)).Status);
        Assert.Single(effects.Invocations);
        Assert.All(effects.Tokens, cancelled => Assert.False(cancelled));
        var done = await recovered.ResumeAsync(id, "cleanup", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, done.Status); Assert.True(Assert.IsType<Data>(done.WorkflowData).Caught);
        Assert.Single(effects.Invocations);
        var trace = await recovered.GetTraceAsync(id);
        Assert.Single(trace, e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
        Assert.Single(trace, e => e.EntryType == TraceEntryType.CancellationHandlerCompleted);
    }

    private static async Task<ExecutionCheckpoint> Read(IWorkflowStateRepository repository, WorkflowInstance instance)
    {
        var reader = new PayloadReader(async reference => await repository.PayloadStore.GetAsync(reference) ?? throw new InvalidDataException("Missing payload"), new PayloadBudget(100_000_000));
        return ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(instance, reader));
    }
    private static async Task<ServiceProvider> Services(ProfileStore store, IWorkflowStateRepository repository, IWorkflowCatalog catalog, Effects effects)
    {
        var collection = new ServiceCollection().AddLogging(); collection.AddIxIFlow(o => o.PayloadThresholdBytes = 1);
        collection.AddSingleton(repository); collection.AddSingleton(catalog); collection.AddSingleton(effects);
        store.ConfigureHost(collection); var provider = collection.BuildServiceProvider();
        await store.StartHostLinksAsync(provider).WaitAsync(TimeSpan.FromSeconds(30)); return provider;
    }
    public sealed class Data { public bool Caught { get; set; } }
    public sealed class Go;
    public sealed class Effects
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, bool> Invocations { get; } = new();
        public System.Collections.Concurrent.ConcurrentBag<bool> Tokens { get; } = new();
    }
    public sealed class Cleanup(Effects effects) : IRecoverableActivity<string>
    {
        public string CaptureRecoveryState(IActivityContext context) => context.InvocationId;
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            effects.Tokens.Add(cancellationToken.IsCancellationRequested);
            Assert.True(effects.Invocations.TryAdd(context.InvocationId, true)); return Task.CompletedTask;
        }
        public Task<ActivityRecoveryResult<string>> RecoverAsync(string state, IActivityContext context, CancellationToken cancellationToken = default)
        {
            Assert.Equal(state, context.InvocationId); effects.Tokens.Add(cancellationToken.IsCancellationRequested);
            return Task.FromResult<ActivityRecoveryResult<string>>(effects.Invocations.ContainsKey(state)
                ? new ActivityRecoveryResult<string>.Completed() : new ActivityRecoveryResult<string>.Execute());
        }
    }
}

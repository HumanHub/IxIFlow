using System.Collections.Concurrent;
using System.Reflection;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "control-flow")]
[Trait("Spec", "W-03")]
public sealed partial class SwitchScenarios
{
    public static IEnumerable<object[]> Compositions => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "saga", "parallel", "each", "flowchart", "state" }.SelectMany(shape =>
            new[] { "normal", "before", "lost-ack", "after", "after-lost-ack", "takeover" }.SelectMany(fault =>
                new[] { "fluent", "yaml" }.Select(authoring => new object[] { profile, shape, fault, authoring }))));

    public static IEnumerable<object[]> Retries => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "none", "up-to", "all" }.Select(mode => new object[] { profile, mode }));

    [Theory]
    [MemberData(nameof(Retries))]
    public async Task SagaSwitchRetryRestoresTheArmOrReselectsAtTheSagaStartAndPairsRequests(string profile, string mode)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var registry = new WorkflowCatalog();
        var effects = new Effects();

        var definition = Workflow.Create<Data>("SwitchRetry" + mode).Step<Seed>().Saga(s => s.Switch(ctx => ctx.WorkflowData.Route, c => c
                .Case(1,
                    arm => arm.Request<Ask>("reply", a => a.CompensateWith<Undo>()).WaitFor<Go>("reply").Step<FailOnce>()
                        .WaitFor<Go>("finish"))
                .Default(arm =>
                    arm.Request<AskFallback>("reply", a => a.CompensateWith<Undo>()).WaitFor<Go>("reply").Step<FailOnce>()
                        .WaitFor<Go>("finish"))))
            .OnError<InvalidOperationException>(error =>
            {
                var waiting = error.WaitFor<Go>("retry");

                var undo = mode == "all" ? waiting.Compensate() :
                    mode == "up-to" ? waiting.CompensateUpTo<Ask>() : waiting.CompensateNone();

                undo.ThenRetry(new RetryPolicy(1));
            }).Build();

        var selections = 0;
        var value = 1;

        definition.Steps[1].SequenceSteps[0].OutcomeSelector = _ =>
        {
            selections++;

            return value;
        };

        await registry.RegisterWorkflowAsync(definition);
        string id;

        await using (var first = Services(store, repository, registry, effects))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var idle = await client.RunAsync(definition, new Data());
            id = idle.InstanceId;
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.ReplyAsync(effects.Requests.Single(), new Go())).Status);
        }

        value = 2;
        await using var second = Services(store, repository, registry, effects);
        var resumedClient = second.GetRequiredService<IWorkflowClient>();
        var retry = await resumedClient.ResumeAsync(id, "retry", new Go());
        Assert.Equal(WorkflowExecutionStatus.Suspended, retry.Status);
        Assert.Equal(mode == "none" ? 1 : 2, effects.Requests.Count);

        if (mode != "none")
        {
            Assert.NotEqual(effects.Requests.First(), effects.Requests.Last());
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await resumedClient.ReplyAsync(effects.Requests.Last(), new Go())).Status);
        }

        Assert.Equal(mode == "all" ? 2 : 1, selections);
        var inspection = (await second.GetRequiredService<IWorkflowOperations>().InspectAsync(id))!;
        Assert.Contains(inspection.Threads.SelectMany(thread => thread.Frames), frame => frame.Kind == "SwitchCase");
        Assert.Equal(mode == "none" ? 0 : 1, effects.Values.Count(value => value == "undo"));
        Assert.Equal(WorkflowExecutionStatus.Success, (await resumedClient.ResumeAsync(id, "finish", new Go())).Status);
        Assert.Equal(2, effects.Failures);
    }

    private static async Task SavedSwitchCasesRecoverInsideEveryCompositionAndSagaCompensates(string profile, string shape, string fault,
        string authoring)
    {
        var time = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider() : null;
        await using var store = await StoreProfiles.CreateAsync(profile, time);
        var effects = new Effects();
        var registry = new WorkflowCatalog();
        var fluent = Definition(shape);

        var yaml = shape switch
        {
            "saga" => IxIFlow.Conformance.Generated.Workflow_switch_recovery_saga_workflow.Build(),
            "parallel" => IxIFlow.Conformance.Generated.Workflow_switch_recovery_parallel_workflow.Build(),
            "each" => IxIFlow.Conformance.Generated.Workflow_switch_recovery_distinct_each_workflow.Build(),
            "flowchart" => IxIFlow.Conformance.Generated.Workflow_switch_recovery_flowchart_workflow.Build(),
            _ => IxIFlow.Conformance.Generated.Workflow_switch_recovery_state_workflow.Build()
        };

        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
        var definition = authoring == "fluent" ? fluent : yaml;
        var selections = 0;
        var value = 1;

        foreach (var pick in ParallelRules.Flatten(definition.Steps).Where(step => step.StepType == WorkflowStepType.Switch))
        {
            var original = pick.OutcomeSelector!;

            pick.OutcomeSelector = ctx =>
            {
                Interlocked.Increment(ref selections);

                return shape == "each" && value == 1 ? original(ctx) : value;
            };
        }

        await registry.RegisterWorkflowAsync(definition);
        var repository = store.NewRepository(time);
        var injecting = true;
        bool Boundary(WorkflowInstance instance) => Volatile.Read(ref selections) > 0 && effects.Values.IsEmpty;

        var faults = new FaultRepository(repository)
        {
            FailCommit = instance => injecting && (fault == "before" && Boundary(instance) ||
                (fault == "after" || fault == "takeover") && !effects.Values.IsEmpty),
            LoseAcknowledgement = instance =>
                fault == "lost-ack" && Boundary(instance) || fault == "after-lost-ack" && !effects.Values.IsEmpty
        };

        var instanceId = Guid.NewGuid().ToString("N");
        WorkflowExecutionResult idle = new() { InstanceId = instanceId };
        var formerStore = LeaseProbe.Wrap(faults, fault == "takeover");
        var formerProbe = (LeaseProbe)formerStore;

        await using (var first = Services(store, formerStore, registry, effects, time))
        {
            if (fault is "before" or "after" or "takeover")
                await Assert.ThrowsAnyAsync<Exception>(() => first.GetRequiredService<IWorkflowClient>().RunAsync(definition,
                    new Data { Items = shape == "each" ? [1, 2] : [1] }, new WorkflowOptions { InstanceId = instanceId }));
            else
            {
                idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition,
                    new Data { Items = shape == "each" ? [1, 2] : [1] }, new WorkflowOptions { InstanceId = instanceId });

                Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            }
        }

        injecting = false;
        WorkflowInstance? stale = null;

        if (fault == "takeover")
        {
            // The failed host acquired this lease itself. Its crash decorator retained it,
            // so recovery must compete with the expired ownership through the host path.
            Assert.NotNull(formerProbe.Grant);
            Assert.True(formerProbe.ReleaseAttempts > 0);
            var query = await IxIFlow.Tests.Infrastructure.StoreTestSupport.ClaimAsync(registry);
            Assert.Null(await repository.TryAcquireExecutionLeaseAsync(instanceId, "early", TimeSpan.FromSeconds(10), query));
            stale = (await repository.GetWorkflowInstanceAsync(instanceId))!;

            var reader = new PayloadReader(async reference => (await repository.PayloadStore.GetAsync(reference))!.Value,
                new PayloadBudget(long.MaxValue));

            stale.ExecutionStateJson = await StoredCheckpoint.WholeStateAsync(stale, reader);
            await reader.ReadAsync(stale.WorkflowDataJson);

            if (time != null)
                time.Advance(TimeSpan.FromSeconds(31));
            else
                await store.ExpireExecutionLeaseAsync(instanceId);
        }

        var successorStore = LeaseProbe.Wrap(repository, false);
        await using var second = Services(store, successorStore, registry, effects, time);
        var client = second.GetRequiredService<IWorkflowClient>();

        if (fault is "before" or "after" or "takeover")
        {
            idle = await client.RecoverAsync(idle.InstanceId);
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        }

        if (fault == "takeover")
        {
            Assert.True(((LeaseProbe)successorStore).Grant!.Epoch > formerProbe.Grant!.Epoch);

            Assert.Equal(WorkflowCommitStatus.StaleEpoch, (await repository.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(stale!, formerProbe.Grant.Epoch, stale!.Revision))).Status);
        }

        var inspection = await second.GetRequiredService<IWorkflowOperations>().InspectAsync(idle.InstanceId);

        Assert.Equal(shape == "each" ? 2 : 1,
            inspection!.Threads.SelectMany(branch => branch.Frames).Count(frame => frame.Kind == "SwitchCase"));

        if (shape == "each")
            Assert.Equal(new[] { "one", "two" },
                (await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: idle.InstanceId))).Select(w => w.Key).Order().ToArray());

        var selectedBefore = selections;
        value = 2; // A restored arm must not evaluate this changed selector again.

        // Both item waits have now been saved together. Replace the provider before delivering either event.
        await second.DisposeAsync();
        await using var resumedHost = Services(store, repository, registry, effects, time);
        var resumedClient = resumedHost.GetRequiredService<IWorkflowClient>();

        if (shape == "each")
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await resumedClient.ResumeAsync(idle.InstanceId, "one", new Go())).Status);
            Assert.Equal("two", Assert.Single(await resumedClient.GetOpenWaitsAsync(new WaitQuery(InstanceId: idle.InstanceId))).Key);
        }

        var resumed = await resumedClient.ResumeAsync(idle.InstanceId, shape == "each" ? "two" : "inside", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.Equal(selectedBefore + (shape == "flowchart" ? 1 : 0), selections);

        Assert.Equal(shape == "saga" ? new[] { "forward", "undo" } : shape == "each" ? new[] { "one", "two" } : new[] { "forward" },
            (shape == "each" ? effects.Values.Order().ToArray() : effects.Values.ToArray()));

        if (fault is "after" or "takeover")
        {
            Assert.NotEmpty(effects.Recoveries);
            Assert.All(effects.Recoveries, invocation => Assert.Contains(invocation, effects.Invocations.Keys));

            if (shape != "each")
                Assert.Equal(effects.Invocations.Keys.Single(), Assert.Single(effects.Recoveries));
        }

        var trace = await repository.GetTraceAsync(idle.InstanceId);
        Assert.Equal(shape is "flowchart" or "each" ? 2 : 1, trace.Count(entry => entry.Entry.EntryType == TraceEntryType.SwitchSelected));

        if (shape == "each")
            Assert.Equal(new[] { SwitchPath.Case, SwitchPath.Default }, trace
                .Where(entry => entry.Entry.EntryType == TraceEntryType.SwitchSelected)
                .Select(entry => Assert.IsType<SwitchTrace>(entry.Entry.Payload).Path).Order().ToArray());

        var replay = TraceReplay.Load(definition, trace);

        foreach (var entered in trace.Where(entry => entry.Entry.EntryType == TraceEntryType.StepEntered &&
                     definition.StepTable.Single(step => step.CatalogId == entry.Entry.StepId).Kind == WorkflowStepType.Switch))
        {
            replay.Seek(entered.Sequence);
            Assert.Equal(entered.Entry.StepId, replay.CurrentStepId);
        }
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task SwitchTimeoutUsesTheStoreClockAndRestoresItsSavedCase(string profile)
    {
        var time = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider() : null;
        await using var store = await StoreProfiles.CreateAsync(profile, time);
        var repository = store.NewRepository(time);
        var registry = new WorkflowCatalog();

        var definition = Workflow.Create<Data>("SwitchTimeout").Step<Seed>()
            .Switch(ctx => ctx.WorkflowData.Route, cases => cases.Case(1, body => body
                .WaitFor<Go>("inside", configure: wait => wait.TimeoutAfter(TimeSpan.FromMilliseconds(100))
                    .OnTimeout(b => b.Step<Mark>(s =>
                        s.Input(a => a.Text).From(ctx => "timeout").Output(a => a.Text).To(ctx => ctx.WorkflowData.Text))))))
            .Build();

        await registry.RegisterWorkflowAsync(definition);
        var effects = new Effects();
        WorkflowExecutionResult idle;

        await using (var first = Services(store, repository, registry, effects, time))
            idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());

        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        await FlowchartScenarios.WaitForTimeoutAsync(repository, idle.InstanceId, time);
        await using var second = Services(store, repository, registry, effects, time);
        var resumed = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(idle.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.Equal("timeout", Assert.IsType<Data>(resumed.WorkflowData).Text);
        Assert.Single(await repository.GetTraceAsync(idle.InstanceId), entry => entry.Entry.EntryType == TraceEntryType.SwitchSelected);
    }

    private static WorkflowDefinition Definition(string shape)
    {
        var builder = Workflow.Create<Data>((shape == "each" ? "SwitchDistinctComposition" : "SwitchComposition") + shape).Step<Seed>();

        void Body(IWorkflowBuilder<Data, Seed> body) => body.Switch(ctx => ctx.WorkflowData.Route, c => c
            .Case(1, arm => arm.Step<Forward>().WaitFor<Go>("inside"))
            .Default(arm => arm.Step<Seed>()));

        switch (shape)
        {
            case "saga":
                return builder.Saga(s => s.Switch(ctx => ctx.WorkflowData.Route, c => c
                        .Case(1, arm => arm.Step<Forward>(a => a.CompensateWith<Undo>()).WaitFor<Go>("inside"))
                        .Default(arm => arm.Step<Seed>())).Step<Fail>())
                    .OnError(error => error.Compensate().ThenContinue()).Build();
            case "parallel":
                return builder.Parallel(p => p.Do(Body).Do(b => b.Step<Seed>())).Build();
            case "each":
                return builder.ForEach(itemsExpression: ctx => ctx.WorkflowData.Items, body: (b, item) => b.Switch(ctx => ctx.Item(item),
                        c => c
                            .Case(1, arm => arm.Step<Forward>(a => a.Input(x => x.Text).From(ctx => "one")).WaitFor<Go>("one"))
                            .Default(arm => arm.Step<Forward>(a => a.Input(x => x.Text).From(ctx => "two")).WaitFor<Go>("two"))),
                    setup: setup => setup.As("item").Parallel(2)).Build();
            case "flowchart":
                return builder.Flowchart(g => g.Start("work").Node("work", b =>
                {
                    Body(b);

                    return b.Step<Increment>(a =>
                        a.Input(x => x.Value).From(ctx => ctx.WorkflowData.Visits).Output(x => x.Value).To(ctx => ctx.WorkflowData.Visits));
                }, e => e.When(ctx => ctx.PreviousStep.Value < 2, "work"))).Build();
            default:
                return builder.StateMachine(m => m.Start("a").State("a", s => s.Immediately(t => t.Otherwise("done", Body)))
                    .State("done", s => s.Final())).Build();
        }
    }

    private static ServiceProvider Services(ProfileStore store, IWorkflowStateRepository repository,
        IWorkflowCatalog registry, Effects effects, TimeProvider? time = null)
    {
        var services = new ServiceCollection().AddLogging();

        services.AddIxIFlow(o =>
        {
            o.Debugging = true;
            o.PayloadThresholdBytes = 1;
        });

        services.AddSingleton(repository);
        services.AddSingleton(registry);
        services.AddSingleton(effects);

        if (time != null)
            services.AddSingleton(time);

        store.ConfigureHost(services);
        var provider = services.BuildServiceProvider();
        store.StartHostLinksAsync(provider).GetAwaiter().GetResult();

        return provider;
    }

    public class LeaseProbe : DispatchProxy
    {
        private IWorkflowStateRepository _inner = null!;
        private bool _retainRelease;
        public ExecutionLeaseGrant? Grant { get; private set; }
        public int ReleaseAttempts { get; private set; }

        public static IWorkflowStateRepository Wrap(IWorkflowStateRepository inner, bool retainRelease)
        {
            var proxy = Create<IWorkflowStateRepository, LeaseProbe>();
            ((LeaseProbe)proxy)._inner = inner;
            ((LeaseProbe)proxy)._retainRelease = retainRelease;

            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IWorkflowStateRepository.ReleaseExecutionLeaseAsync))
            {
                ReleaseAttempts++;

                if (_retainRelease)
                    return Task.FromResult(new ExecutionLeaseReleaseResult(false));
            }

            var result = targetMethod.Invoke(_inner, args);

            return targetMethod.Name == nameof(IWorkflowStateRepository.TryAcquireExecutionLeaseAsync)
                ? Acquired((Task<ExecutionLeaseGrant?>)result!)
                : result;
        }

        private async Task<ExecutionLeaseGrant?> Acquired(Task<ExecutionLeaseGrant?> pending)
        {
            var grant = await pending;

            if (grant != null)
                Grant = grant;

            return grant;
        }
    }

    public sealed class Effects
    {
        public ConcurrentQueue<string> Values { get; } = new();
        public ConcurrentDictionary<string, bool> Invocations { get; } = new();
        public ConcurrentQueue<string> Recoveries { get; } = new();
        public ConcurrentQueue<string> Requests { get; } = new();
        public int Failures;
    }

    public sealed class Data
    {
        public int Route { get; set; } = 1;
        public int Visits { get; set; }
        public string Text { get; set; } = "";
        public List<int> Items { get; set; } = [1];
    }

    public sealed class Go
    {
    }

    public sealed class Seed : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Forward(Effects effects) : IRecoverableActivity<string>
    {
        public string Text { get; set; } = "forward";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Assert.True(effects.Invocations.TryAdd(context.InvocationId, true));
            effects.Values.Enqueue(Text);

            return Task.CompletedTask;
        }

        public string CaptureRecoveryState(IActivityContext context) => context.InvocationId;

        public Task<ActivityRecoveryResult<string>> RecoverAsync(string state, IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(state, context.InvocationId);
            effects.Recoveries.Enqueue(state);

            return Task.FromResult<ActivityRecoveryResult<string>>(effects.Invocations.ContainsKey(state)
                ? new ActivityRecoveryResult<string>.Completed()
                : new ActivityRecoveryResult<string>.Execute());
        }
    }

    public sealed class Undo(Effects effects) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            effects.Values.Enqueue("undo");

            return Task.CompletedTask;
        }
    }

    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("compensate"));
    }

    public sealed class FailOnce(Effects effects) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref effects.Failures) == 1
                ? Task.FromException(new InvalidOperationException("retry"))
                : Task.CompletedTask;
    }

    public sealed class Ask(Effects effects) : IRequestActivity<Go>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            effects.Requests.Enqueue(context.Reply!.Token);

            return Task.CompletedTask;
        }
    }

    public sealed class AskFallback(Effects effects) : IRequestActivity<Go>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            effects.Requests.Enqueue(context.Reply!.Token);

            return Task.CompletedTask;
        }
    }

    public sealed class Mark : IAsyncActivity
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Increment : IAsyncActivity
    {
        public int Value { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Value++;

            return Task.CompletedTask;
        }
    }
}
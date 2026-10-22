using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "control-flow")]
[Trait("Spec", "W-04")]
public sealed class AssignScenarios
{
    public static IEnumerable<object[]> Boundaries => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "sequence", "parallel", "losing", "each", "catch", "switch", "inline", "flowchart", "state" }.SelectMany(shape =>
            new[] { "normal", "before", "lost-ack", "after", "after-lost-ack" }.SelectMany(fault =>
                (shape == "inline" ? new[] { "fluent" } : new[] { "fluent", "yaml" })
                    .Select(authoring => new object[] { profile, shape, fault, authoring }))));

    [Theory, MemberData(nameof(Boundaries))]
    public async Task AssignmentsRecoverAtTheirOrdinaryBoundaryWithBranchIsolation(string profile, string shape, string fault, string authoring)
    {
        var time = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider() : null;
        await using var store = await StoreProfiles.CreateAsync(profile, time);
        var effects = new Effects();
        var catalog = new WorkflowCatalog();
        var fluent = Definition(shape);
        var definition = fluent;
        if (shape != "inline")
        {
            var yaml = Yaml(shape);
            Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
            if (authoring == "yaml") definition = yaml;
        }
        var writes = 0;
        foreach (var step in ParallelRules.Flatten(definition.Steps).Where(step => step.Assignment != null))
        {
            var apply = step.Assignment!.Apply!;
            step.Assignment.Apply = context => { apply(context); Interlocked.Increment(ref writes); };
        }
        await catalog.RegisterWorkflowAsync(definition);
        var repository = store.NewRepository(time);
        var injecting = true;
        bool Before(WorkflowInstance _) => writes > 0 && effects.Invocations.IsEmpty;
        var decorated = new FaultRepository(repository)
        {
            FailCommit = instance => injecting && (fault == "before" && Before(instance) || fault == "after" && !effects.Invocations.IsEmpty),
            LoseAcknowledgement = instance => fault == "lost-ack" && Before(instance) || fault == "after-lost-ack" && !effects.Invocations.IsEmpty
        };
        var id = Guid.NewGuid().ToString("N");
        await using (var first = Services(store, decorated, catalog, effects, time))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            if (fault is "before" or "after")
                await Assert.ThrowsAnyAsync<Exception>(() => client.RunAsync(definition, new Data(), new WorkflowOptions { InstanceId = id }));
            else
                Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.RunAsync(definition, new Data(), new WorkflowOptions { InstanceId = id })).Status);
        }
        injecting = false;
        repository = await RestartRepository(store, repository, time);
        await using var second = Services(store, repository, catalog, effects, time);
        var resumed = second.GetRequiredService<IWorkflowClient>();
        if (fault is "before" or "after")
        {
            var idle = await resumed.RecoverAsync(id);
            Assert.True(idle.Status == WorkflowExecutionStatus.Suspended, idle.ErrorMessage + "\n" + idle.ErrorStackTrace);
        }
        var result = await resumed.ResumeAsync(id, "saved", new Go());
        if (shape == "flowchart")
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            result = await resumed.ResumeAsync(id, "saved", new Go());
        }
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(shape == "flowchart" ? 4 : 2, data.Count);
        Assert.Equal(0, data.Other); // A sibling sees the fork's original value; losing writes are discarded.
        Assert.Equal(shape == "each" ? 2 : 1, data.Items[0]);
        Assert.Equal(shape == "catch" ? "fault" : "", data.Text);
        Assert.Equal(shape == "flowchart" ? 2 : 1, effects.Invocations.Count);
        var operations = second.GetRequiredService<IWorkflowOperations>();
        var trace = await operations.GetTraceAsync(id);
        Assert.Contains(trace, entry => entry.Entry.Payload is DataAssignedTrace);
        Assert.DoesNotContain(trace, entry => entry.Entry.Payload is DataAssignedTrace assigned && assigned.Target.Contains("99", StringComparison.Ordinal));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AssignTimeoutUsesStoreDeadlinesAndRestoresTheCommittedValue(string profile)
    {
        var time = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider() : null;
        await using var store = await StoreProfiles.CreateAsync(profile, time);
        var repository = store.NewRepository(time);
        var catalog = new WorkflowCatalog();
        var fluent = Workflow.Create<Data>("AssignTimeout").Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1)
            .WaitFor<Go>("timer", configure: wait => wait.TimeoutAfter(TimeSpan.FromMilliseconds(100)).OnTimeout(b => b
                .Assign(ctx => ctx.WorkflowData.Other, ctx => ctx.WorkflowData.Count))).Build();
        var yaml = IxIFlow.Conformance.Generated.Workflow_assign_timeout_workflow.Build();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
        await catalog.RegisterWorkflowAsync(yaml);
        var effects = new Effects();
        WorkflowExecutionResult idle;
        await using (var first = Services(store, repository, catalog, effects, time))
            idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(yaml, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        await FlowchartScenarios.WaitForTimeoutAsync(repository, idle.InstanceId, time);
        repository = await RestartRepository(store, repository, time);
        await using var second = Services(store, repository, catalog, effects, time);
        var result = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(idle.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(1, data.Count);
        Assert.Equal(1, data.Other);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AssignDebuggerPauseIsDurableBeforeAndAfterTheWrite(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var catalog = new WorkflowCatalog();
        var definition = new WorkflowBuilder<Data, object>([], "AssignPause").WaitFor<Go>("start")
            .Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1).WithId("assign")
            .Assign(ctx => ctx.WorkflowData.Other, ctx => ctx.WorkflowData.Count).WithId("after").Build();
        await catalog.RegisterWorkflowAsync(definition);
        var effects = new Effects();
        string id;
        var repository = store.NewRepository();
        await using (var first = Services(store, repository, catalog, effects, null))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var ops = first.GetRequiredService<IWorkflowOperations>();
            id = (await client.RunAsync(definition, new Data())).InstanceId;
            await ops.SetBreakpointsAsync(id, [new("before", "assign")], "test");
            var paused = await client.ResumeAsync(id, "start", new Go());
            Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
            Assert.Equal(0, Assert.IsType<Data>(paused.WorkflowData).Count);
        }
        repository = await RestartRepository(store, repository);
        await using (var second = Services(store, repository, catalog, effects, null))
        {
            var ops = second.GetRequiredService<IWorkflowOperations>();
            var before = (await ops.InspectAsync(id))!;
            Assert.Equal("assign", Assert.Single(before.Threads).AuthorStepId);
            await ops.SetBreakpointsAsync(id, [], "test");
            var paused = await ops.StepAsync(id, before.Threads[0].ContinuationId, DebugStepKind.Into, "test");
            Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
            Assert.Equal(1, Assert.IsType<Data>(paused.WorkflowData).Count);
        }
        repository = await RestartRepository(store, repository);
        await using var third = Services(store, repository, catalog, effects, null);
        var operations = third.GetRequiredService<IWorkflowOperations>();
        Assert.Equal("after", Assert.Single((await operations.InspectAsync(id))!.Threads).AuthorStepId);
        var completed = await operations.ContinueAsync(id, "test");
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, Assert.IsType<Data>(completed.WorkflowData).Count);
        Assert.Equal(1, Assert.IsType<Data>(completed.WorkflowData).Other);
        var trace = await operations.GetTraceAsync(id);
        var replay = TraceReplay.Load(definition, trace);
        foreach (var fact in trace.Where(entry => entry.Entry.EntryType == TraceEntryType.DataAssigned))
        {
            replay.Seek(fact.Sequence);
            Assert.Null(replay.CurrentStepId);
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.MultipleHosts), MemberType = typeof(StoreProfiles))]
    public async Task AssignBoundaryTakeoverRecomputesOnlyTheUncommittedWrite(string profile)
    {
        var time = profile == StoreProfiles.LocalMemory ? new FakeTimeProvider() : null;
        await using var store = await StoreProfiles.CreateAsync(profile, time);
        var repository = store.NewRepository(time);
        var catalog = new WorkflowCatalog();
        var definition = Definition("sequence");
        var writes = 0;
        var assignment = definition.Steps[0].SequenceSteps[0].Assignment!;
        var apply = assignment.Apply!;
        assignment.Apply = context => { apply(context); writes++; };
        await catalog.RegisterWorkflowAsync(definition);
        var injecting = true;
        var faults = new FaultRepository(repository) { FailCommit = _ => injecting && writes > 0 };
        var former = SwitchScenarios.LeaseProbe.Wrap(faults, true);
        var effects = new Effects();
        var id = Guid.NewGuid().ToString("N");
        await using (var first = Services(store, former, catalog, effects, time))
            await Assert.ThrowsAnyAsync<Exception>(() => first.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new Data(), new WorkflowOptions { InstanceId = id }));
        injecting = false;
        var formerProbe = (SwitchScenarios.LeaseProbe)former;
        Assert.NotNull(formerProbe.Grant);
        if (time != null) time.Advance(TimeSpan.FromSeconds(31)); else await store.ExpireExecutionLeaseAsync(id);
        var successor = SwitchScenarios.LeaseProbe.Wrap(store.NewRepository(time), false);
        await using var second = Services(store, successor, catalog, effects, time);
        var client = second.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.RecoverAsync(id)).Status);
        Assert.True(((SwitchScenarios.LeaseProbe)successor).Grant!.Epoch > formerProbe.Grant!.Epoch);
        var result = await client.ResumeAsync(id, "saved", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(2, Assert.IsType<Data>(result.WorkflowData).Count);
        Assert.Single(effects.Invocations);
        Assert.True(writes >= 2);
    }

    private static void Body(IWorkflowBuilder<Data, object> builder) => builder
        .Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1).WithId("assign")
        .Step<Boundary>().WaitFor<Go>("saved")
        .Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1);

    private static WorkflowDefinition Definition(string shape)
    {
        var builder = new WorkflowBuilder<Data, object>([], "Assign" + shape);
        switch (shape)
        {
            case "sequence": return builder.Sequence(Body).Build();
            case "parallel": return builder.Parallel(p => p.Do(Body).Do(b => b.Assign(ctx => ctx.WorkflowData.Other, ctx => ctx.WorkflowData.Count))).Build();
            case "losing": return builder.Parallel(p => p.WaitAny().Do(Body).Do(b => b.Assign(ctx => ctx.WorkflowData.Count, ctx => 99).WaitFor<Go>("never"))).Build();
            case "each": return builder.ForEach(itemsExpression: ctx => ctx.WorkflowData.Items, body: (b, item) =>
            {
                b.Assign(ctx => ctx.WorkflowData.Items[ctx.Index(item)], ctx => ctx.Item(item) + 1);
                Body(b);
            }).Build();
            case "catch": return builder.Try(t => t.Step<Fail>()).Catch<InvalidOperationException, Fault>(c => c
                .Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.Fault.Message)
                .Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1).WithId("assign")
                .Step<Boundary>().WaitFor<Go>("saved").Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1)).Build();
            case "switch": return builder.Switch(ctx => ctx.WorkflowData.Count, cases => cases.Case(0, Body)).Build();
            case "inline": return builder.Use<Inline, Data>(u => u.Input(x => x.Count).From(ctx => ctx.WorkflowData.Count)
                .Output(x => x.Count).To(ctx => ctx.WorkflowData.Count)).Build();
            case "flowchart": return builder.Flowchart(g => g.Start("work").Node("work", b =>
            {
                return b.Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1).WithId("assign")
                    .Step<Boundary>().WaitFor<Go>("saved").Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1);
            }, edges => edges.When(ctx => ctx.WorkflowData.Count < 3, "work"))).Build();
            default: return builder.StateMachine(m => m.Start("work").State("work", s => s.Entry(b =>
            {
                return b.Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1).WithId("assign")
                    .Step<Boundary>().WaitFor<Go>("saved").Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1);
            }).Immediately(t => t.Otherwise("done"))).State("done", s => s.Final())).Build();
        }
    }

    private static WorkflowDefinition Yaml(string shape) => shape switch
    {
        "sequence" => IxIFlow.Conformance.Generated.Workflow_assign_sequence_workflow.Build(),
        "parallel" => IxIFlow.Conformance.Generated.Workflow_assign_parallel_workflow.Build(),
        "losing" => IxIFlow.Conformance.Generated.Workflow_assign_losing_workflow.Build(),
        "each" => IxIFlow.Conformance.Generated.Workflow_assign_each_workflow.Build(),
        "catch" => IxIFlow.Conformance.Generated.Workflow_assign_catch_workflow.Build(),
        "switch" => IxIFlow.Conformance.Generated.Workflow_assign_switch_workflow.Build(),
        "flowchart" => IxIFlow.Conformance.Generated.Workflow_assign_flowchart_workflow.Build(),
        _ => IxIFlow.Conformance.Generated.Workflow_assign_state_workflow.Build()
    };

    private static async Task<IWorkflowStateRepository> RestartRepository(ProfileStore store, IWorkflowStateRepository repository, TimeProvider? time = null)
    {
        if (store.Profile == StoreProfiles.LocalSqlite && repository is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
        return store.NewRepository(time);
    }

    private static ServiceProvider Services(ProfileStore store, IWorkflowStateRepository repository, IWorkflowCatalog catalog, Effects effects, TimeProvider? time)
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow(o => { o.Debugging = true; o.PayloadThresholdBytes = 1; }).Services;
        services.AddSingleton(repository).AddSingleton(catalog).AddSingleton(effects);
        if (time != null) services.AddSingleton(time);
        store.ConfigureHost(services);
        var provider = services.BuildServiceProvider();
        store.StartHostLinksAsync(provider).GetAwaiter().GetResult();
        return provider;
    }

    public sealed class Data
    {
        public int Count { get; set; }
        public int Other { get; set; }
        public string Text { get; set; } = "";
        public List<int> Items { get; set; } = [1];
    }
    public sealed class Go { }
    public sealed class Fault { public string Message { get; set; } = ""; }
    public sealed class Effects { public ConcurrentDictionary<string, bool> Invocations { get; } = new(); }
    public sealed class Boundary(Effects effects) : IRecoverableActivity<string>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Assert.True(effects.Invocations.TryAdd(context.InvocationId, true)); return Task.CompletedTask; }
        public string CaptureRecoveryState(IActivityContext context) => context.InvocationId;
        public Task<ActivityRecoveryResult<string>> RecoverAsync(string state, IActivityContext context, CancellationToken cancellationToken = default)
        {
            Assert.Equal(state, context.InvocationId);
            return Task.FromResult<ActivityRecoveryResult<string>>(effects.Invocations.ContainsKey(state)
                ? new ActivityRecoveryResult<string>.Completed() : new ActivityRecoveryResult<string>.Execute());
        }
    }
    public sealed class Fail : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.FromException(new InvalidOperationException("fault")); }
    [Workflow("AssignInline")]
    public sealed class Inline : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> builder) => builder.Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1).WithId("assign")
            .Step<Boundary>().WaitFor<Go>("saved").Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1);
    }
}

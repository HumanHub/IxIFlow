using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.ActivitySdk;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "G-04")]
[Trait("Spec", "G-05")]
public sealed class BreakpointTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("target")]
    [InlineData("root/1")]
    public async Task FunctionBreakpointPausesBeforeTheStepAndContinueSkipsItOnce(string address)
    {
        var store = new Store(); var probe = new Probe();
        await using var first = Services(store, probe); await using var second = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "FunctionBreakpoint").WaitFor<Go>("go")
            .Step<Count>().WithId("target").Build();
        var client = first.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());
        var ops = first.GetRequiredService<IWorkflowOperations>();
        var resolved = Assert.Single(await ops.SetBreakpointsAsync(idle.InstanceId, [new("bp", address)], "setter"));
        Assert.Equal("root/1", resolved.StepId);
        Assert.Equal(address, resolved.RequestedStepId);
        var paused = await client.ResumeAsync(idle.InstanceId, "go", new Go());
        Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
        Assert.Equal(0, probe.Count);
        var saved = (await store.GetWorkflowInstanceAsync(idle.InstanceId))!;
        Assert.Null(saved.RunnableAtUtc);
        var checkpoint = WorkflowDebugRules.Checkpoint(saved);
        Assert.Equal("root/1", checkpoint.Stop!.StepId);
        Assert.Equal("bp", checkpoint.Stop.BreakpointId);
        Assert.NotNull(checkpoint.Stop.BranchId);
        Assert.Equal(1, Assert.Single(await ops.GetBreakpointsAsync(idle.InstanceId)).HitCount);
        var hit = Assert.Single(await store.GetTraceAsync(idle.InstanceId), e => e.Entry.EntryType == TraceEntryType.BreakpointHit);
        Assert.Equal("bp", Assert.IsType<BreakpointHitTrace>(hit.Entry.Payload).BreakpointId);
        Assert.All((await store.GetDebugStatesAsync([idle.InstanceId]))[idle.InstanceId].Commands, c => Assert.NotNull(c.AcknowledgedRevision));
        Assert.Equal(WorkflowExecutionStatus.Success, (await second.GetRequiredService<IWorkflowOperations>()
            .ContinueAsync(idle.InstanceId, "continuer")).Status);
        Assert.Equal(1, probe.Count);
        Assert.Equal(1, Assert.Single(await ops.GetBreakpointsAsync(idle.InstanceId)).HitCount);
    }

    [Fact]
    public async Task SourceBreakpointReturnsVerifiedLineAndKeepsOriginalLocationAndHash()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "SourceBreakpoint").WaitFor<Go>("go", sourcePath: "flow.cs", sourceLine: 10)
            .Step<Count>(sourcePath: "flow.cs", sourceLine: 30).Build();
        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());
        var ops = services.GetRequiredService<IWorkflowOperations>();
        var bp = Assert.Single(await ops.SetBreakpointsAsync(idle.InstanceId,
            [new("line", Source: new("flow.cs", 21))], "setter"));
        Assert.Equal(new("flow.cs", 21), bp.Source);
        Assert.Equal(new("flow.cs", 30), bp.VerifiedSource);
        Assert.Equal("root/1", bp.StepId);
        Assert.Equal(ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(idle.InstanceId))!.ExecutionStateJson).StructureHash, bp.StructureHash);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
    }

    [Theory]
    [InlineData("== 2", 1)]
    [InlineData(">= 2", 3)]
    [InlineData("% 2", 2)]
    public async Task HitConditionsCountLoopVisitsAndContinueDoesNotCountTheSkippedVisit(string condition, int expectedStops)
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "LoopBreakpoint").WaitFor<Go>("go")
            .WhileDo(c => c.WorkflowData.Value < 4, body => body.Step<Increment>(s => s
                .Input(a => a.Value).From(c => c.WorkflowData.Value)
                .Output(a => a.Value).To(c => c.WorkflowData.Value)).WithId("increment")).Build();
        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());
        var ops = services.GetRequiredService<IWorkflowOperations>();
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("loop", "increment", HitCondition: condition)], "setter");
        var result = await client.ResumeAsync(idle.InstanceId, "go", new Go());
        var stops = 0;
        while (result.Status == WorkflowExecutionStatus.Paused && stops < 8)
        {
            stops++;
            result = await ops.ContinueAsync(idle.InstanceId, "continuer");
        }
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(expectedStops, stops);
        Assert.Equal(4, Assert.Single(await ops.GetBreakpointsAsync(idle.InstanceId)).HitCount);
    }

    [Fact]
    public async Task ForEachBreakpointStopsAtEachItem()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "ItemBreakpoint").WaitFor<Go>("go")
            .ForEach(c => c.WorkflowData.Items, (body, item) => body.Step<Count>().WithId("item")).Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("each", "item")], "setter");
        var result = await client.ResumeAsync(idle.InstanceId, "go", new Go());
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
            Assert.Equal(i, probe.Count);
            Assert.Contains((await client.GetSnapshotAsync(idle.InstanceId))!.Frames, f => f.ForEachItemIndex == i);
            result = await ops.ContinueAsync(idle.InstanceId, "continuer");
        }
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(3, probe.Count);
    }

    [Fact]
    public async Task ParallelForEachItemsHaveIndependentStopsAndContinueSkips()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "ParallelItemBreakpoint").WaitFor<Go>("go")
            .ForEach(c => c.WorkflowData.Items, (body, item) => body.Step<Count>().WithId("item"),
                setup => setup.Parallel(2)).Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("each", "item")], "setter");
        var result = await client.ResumeAsync(idle.InstanceId, "go", new Go());
        var branches = new HashSet<string>();
        while (result.Status == WorkflowExecutionStatus.Paused && branches.Count < 4)
        {
            var stop = WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(idle.InstanceId))!).Stop!;
            Assert.True(branches.Add(stop.BranchId));
            result = await ops.ContinueAsync(idle.InstanceId, "continuer");
        }
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(3, branches.Count);
        Assert.Equal(3, probe.Count);
        Assert.Equal(3, Assert.Single(await ops.GetBreakpointsAsync(idle.InstanceId)).HitCount);
    }

    [Fact]
    public async Task BreakpointSetOnAnotherHostReachesRunningOwner()
    {
        var store = new Store(); var probe = new Probe(); await using var owner = Services(store, probe);
        await using var writer = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "RemoteBreakpoint").Step<Block>().Step<Count>().WithId("target").Build();
        var run = owner.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data(), new() { InstanceId = "remote" });
        await probe.Started.Task.WaitAsync(Timeout);
        await writer.GetRequiredService<IWorkflowOperations>().SetBreakpointsAsync("remote", [new("bp", "target")], "remote-actor");
        await store.ChangeObserved.Task.WaitAsync(Timeout);
        await Until(async () => (await store.GetDebugStatesAsync(["remote"]))["remote"].Commands.All(c => c.AcknowledgedRevision != null));
        probe.Finish.TrySetResult();
        Assert.Equal(WorkflowExecutionStatus.Paused, (await run.WaitAsync(Timeout)).Status);
        Assert.Equal(0, probe.Count);
        var trace = await store.GetTraceAsync("remote");
        Assert.Equal("remote-actor", Assert.IsType<DebugCommandTrace>(trace.Single(e => e.Entry.EntryType == TraceEntryType.BreakpointsChanged).Entry.Payload).Actor);
    }

    [Fact]
    public async Task AbandonedHitCountIsRecomputedOnceAfterCrash()
    {
        var store = new Store { FailPause = true }; var probe = new Probe();
        await using var first = Services(store, probe); await using var second = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "CrashBreakpoint").WaitFor<Go>("go").Step<Count>().WithId("target").Build();
        var client = first.GetRequiredService<IWorkflowClient>(); var ops = first.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("bp", "target")], "setter");
        await Assert.ThrowsAsync<WorkflowEngineException>(() => client.ResumeAsync(idle.InstanceId, "go", new Go()));
        Assert.Equal(1, store.AttemptedHitCount);
        Assert.Equal(0, Assert.Single(await ops.GetBreakpointsAsync(idle.InstanceId)).HitCount);
        Assert.Equal(0, probe.Count);
        store.FailPause = false;
        Assert.Equal(WorkflowExecutionStatus.Paused, (await second.GetRequiredService<IWorkflowClient>().RecoverAsync(idle.InstanceId)).Status);
        Assert.Equal(1, Assert.Single(await ops.GetBreakpointsAsync(idle.InstanceId)).HitCount);
        Assert.Single(await store.GetTraceAsync(idle.InstanceId), e => e.Entry.EntryType == TraceEntryType.BreakpointHit);
    }

    [Fact]
    public async Task DisabledBreakpointDoesNotCountOrPause()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "DisabledBreakpoint").WaitFor<Go>("go").Step<Count>().WithId("target").Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("bp", "target", Enabled: false)], "setter");
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        Assert.Equal(1, probe.Count);
        Assert.Equal(0, Assert.Single(await ops.GetBreakpointsAsync(idle.InstanceId)).HitCount);
    }

    [Fact]
    public async Task ReplacingBreakpointsDuringThePauseCommitKeepsTheOrderedChangeAndContinueWake()
    {
        var store = new Store { HoldPause = true }; var probe = new Probe();
        await using var owner = Services(store, probe); await using var writer = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "PauseRace").WaitFor<Go>("go")
            .Step<Count>().WithId("target").Build();
        var client = owner.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());
        var ops = writer.GetRequiredService<IWorkflowOperations>();
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("bp", "target")], "setter");
        var run = client.ResumeAsync(idle.InstanceId, "go", new Go());
        await store.PauseCommitting.Task.WaitAsync(Timeout);
        await ops.SetBreakpointsAsync(idle.InstanceId, [], "remover");
        store.ReleasePause.TrySetResult();
        Assert.Equal(WorkflowExecutionStatus.Paused, (await run.WaitAsync(Timeout)).Status);
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "continuer")).Status);
        Assert.Empty(await ops.GetBreakpointsAsync(idle.InstanceId));
        var commands = (await store.GetDebugStatesAsync([idle.InstanceId]))[idle.InstanceId].Commands;
        Assert.Equal([DebugCommandKind.SetBreakpoints, DebugCommandKind.SetBreakpoints, DebugCommandKind.Continue], commands.Select(c => c.Kind));
        Assert.All(commands, c => Assert.NotNull(c.AcknowledgedRevision));
        Assert.Equal(1, probe.Count);
    }

    [Fact]
    public async Task PausedConfigurationAndContinueApplyTogetherUnderTheFreshLease()
    {
        var store = new Store(); var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "PausedPrefix").WaitFor<Go>("go")
            .Step<Count>().WithId("target").Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("bp", "target")], "setter");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        var generation = WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(idle.InstanceId))!).PauseGeneration;
        Assert.Equal(DebugCommandStatus.Accepted, (await store.RequestDebugCommandAsync(idle.InstanceId,
            new("remove", DebugCommandKind.SetBreakpoints, generation, "remover", []))).Status);
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "continuer")).Status);
        var commands = (await store.GetDebugStatesAsync([idle.InstanceId]))[idle.InstanceId].Commands;
        Assert.Equal(commands.Single(c => c.Id == "remove").AcknowledgedRevision,
            commands.Single(c => c.Kind == DebugCommandKind.Continue).AcknowledgedRevision);
        Assert.Equal(1, probe.Count);
    }

    [Fact]
    public async Task ManualPauseRacingAnAutomaticPauseDoesNotStrandContinue()
    {
        var store = new Store { HoldPause = true }; var probe = new Probe(); await using var services = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "RedundantPause").WaitFor<Go>("go")
            .Step<Count>().WithId("target").Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("bp", "target")], "setter");
        var run = client.ResumeAsync(idle.InstanceId, "go", new Go());
        await store.PauseCommitting.Task.WaitAsync(Timeout);
        Assert.Equal(DebugCommandStatus.Accepted, (await store.RequestDebugCommandAsync(idle.InstanceId,
            new("manual", DebugCommandKind.Pause, 0, "manual-pauser"))).Status);
        store.ReleasePause.TrySetResult();
        Assert.Equal(WorkflowExecutionStatus.Paused, (await run.WaitAsync(Timeout)).Status);
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "continuer")).Status);
        var checkpoint = WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(idle.InstanceId))!);
        Assert.Equal(1, checkpoint.PauseGeneration);
        Assert.All((await store.GetDebugStatesAsync([idle.InstanceId]))[idle.InstanceId].Commands, c => Assert.NotNull(c.AcknowledgedRevision));
        Assert.Equal(1, probe.Count);
    }

    [Fact]
    public async Task AllStopCommitsAnInflightSiblingAndRetainsTheHitIntentAcrossCrash()
    {
        var store = new Store { FailPause = true }; var probe = new Probe();
        await using var owner = Services(store, probe); await using var recovery = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "DrainBreakpoint").WaitFor<Go>("go")
            .Parallel(p => p.Do(b => b.Step<Block>()).Do(b => b.Step<Count>().WithId("target"))).Build();
        var client = owner.GetRequiredService<IWorkflowClient>(); var ops = owner.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("bp", "target")], "setter");
        var run = client.ResumeAsync(idle.InstanceId, "go", new Go());
        await probe.Started.Task.WaitAsync(Timeout);
        probe.Finish.TrySetResult();
        await Assert.ThrowsAsync<WorkflowEngineException>(() => run.WaitAsync(Timeout));
        var saved = WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(idle.InstanceId))!);
        Assert.Equal(1, saved.Breakpoints.Single().HitCount);
        Assert.NotNull(saved.Stop);
        Assert.Equal(0, probe.Count);
        Assert.Contains(await store.GetTraceAsync(idle.InstanceId), e => e.Entry.EntryType == TraceEntryType.ActivityCompleted);
        store.FailPause = false;
        Assert.Equal(WorkflowExecutionStatus.Paused, (await recovery.GetRequiredService<IWorkflowClient>().RecoverAsync(idle.InstanceId)).Status);
        Assert.Equal(1, Assert.Single(await ops.GetBreakpointsAsync(idle.InstanceId)).HitCount);
        Assert.Equal(WorkflowExecutionStatus.Success, (await recovery.GetRequiredService<IWorkflowOperations>()
            .ContinueAsync(idle.InstanceId, "continuer")).Status);
    }

    [Fact]
    public async Task ContinueSkipIsDurableWhenTheFollowingActivityStartIsAbandoned()
    {
        var store = new Store(); var probe = new Probe();
        await using var first = Services(store, probe); await using var second = Services(store, probe);
        var definition = new WorkflowBuilder<Data, object>([], "SkipCrash").WaitFor<Go>("go")
            .Step<Count>().WithId("target").Build();
        var client = first.GetRequiredService<IWorkflowClient>(); var ops = first.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("bp", "target")], "setter");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        store.FailAfterContinue = true;
        await Assert.ThrowsAsync<WorkflowEngineException>(() => ops.ContinueAsync(idle.InstanceId, "continuer"));
        var saved = WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(idle.InstanceId))!);
        Assert.Single(saved.SkipOnce);
        Assert.Null(saved.Stop);
        Assert.Equal(0, probe.Count);
        store.FailAfterContinue = false;
        Assert.Equal(WorkflowExecutionStatus.Success, (await second.GetRequiredService<IWorkflowClient>().RecoverAsync(idle.InstanceId)).Status);
        Assert.Equal(1, probe.Count);
        Assert.Equal(1, Assert.Single(await ops.GetBreakpointsAsync(idle.InstanceId)).HitCount);
    }

    [Theory]
    [InlineData("== 0")]
    [InlineData("% -1")]
    [InlineData("wat 3")]
    public void InvalidHitConditionsAreRefused(string condition) => Assert.Throws<ArgumentException>(() =>
        BreakpointResolver.Resolve(new WorkflowBuilder<Data, object>([], "InvalidHit").Step<Count>().Build(), [new("bp", "root/0", HitCondition: condition)]));

    private static async Task Until(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!await predicate()) await Task.Delay(20, timeout.Token);
    }

    private static ServiceProvider Services(Store store, Probe probe)
    {
        var services = new ServiceCollection().AddLogging(); services.AddIxIFlow(o => o.Debugging = true);
        services.AddSingleton<IWorkflowStateRepository>(store); services.AddSingleton(probe);
        services.AddSingleton<IWorkflowVersionRegistry>(probe.Registry);
        return services.BuildServiceProvider();
    }
    public sealed class Data { public int Value { get; set; } public List<int> Items { get; set; } = [1, 2, 3]; }
    public sealed class Go;
    public sealed class Probe
    {
        public WorkflowVersionRegistry Registry { get; } = new();
        public int Count;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class Count(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref probe.Count); return Task.CompletedTask; }
    }
    public sealed class Increment : IAsyncActivity
    {
        [ActivityInputOutput] public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Value++; return Task.CompletedTask; }
    }
    public sealed class Block(Probe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { probe.Started.TrySetResult(); await probe.Finish.Task.WaitAsync(cancellationToken); }
    }
    private sealed class Store : InMemoryWorkflowStateRepository
    {
        public bool FailPause;
        public bool HoldPause;
        public bool FailAfterContinue;
        private bool _continued;
        public long AttemptedHitCount;
        public TaskCompletionSource PauseCommitting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePause { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ChangeObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> ids)
        {
            var states = await base.GetDebugStatesAsync(ids);
            if (states.Values.Any(s => s.Commands.Any(c => c.Kind == DebugCommandKind.SetBreakpoints))) ChangeObserved.TrySetResult();
            return states;
        }
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            if (FailAfterContinue && _continued) throw new IOException("Injected crash after durable continue");
            if (HoldPause && commit.Instance.Status == WorkflowStatus.Paused)
            {
                PauseCommitting.TrySetResult();
                await ReleasePause.Task.WaitAsync(Timeout);
            }
            if (FailPause && commit.Instance.Status == WorkflowStatus.Paused)
            {
                AttemptedHitCount = WorkflowDebugRules.Checkpoint(commit.ReadTrunk()).Breakpoints.Single().HitCount;
                throw new IOException("Injected crash after counting and before committing breakpoint hit");
            }
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (commit.TraceEntries.Any(e => JsonSerializer.Deserialize<ExecutionTraceEntry>(e)!.EntryType == TraceEntryType.Continued))
                _continued = true;
            return result;
        }
    }
}

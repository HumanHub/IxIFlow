using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static IxIFlow.Tests.ExecutionTests.CancellationScopeTests;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "W-06")]
public sealed class CancellationScopeIntegrationTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task LiveCancellationClosesResourcesInNestingOrderBeforeLeavingTheWrapper(bool wrapperOutside)
    {
        var probe = new Probe(); var live = new LiveProbe();
        var collection = new ServiceCollection(); collection.AddIxIFlow(o => o.VerifyCheckpointParts = true);
        collection.AddSingleton(probe); collection.AddSingleton(live);
        await using var services = collection.BuildServiceProvider();
        var b = new WorkflowBuilder<Data, object>([], "CancellationLiveResource");
        var definition = wrapperOutside ? b.CancellationScope(x => x.Scope<Session>(null, y => y.Step<Blocking>()),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))).Build()
            : b.Scope<Session>(null, x => x.CancellationScope(y => y.Step<Blocking>(), h => h.Step<ReadSession>())).Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var id = Guid.NewGuid().ToString("N");
        var run = client.RunAsync(definition, new Data(), new() { InstanceId = id });
        await live.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await client.CancelAsync(id, new() { ReasonCode = "test" });
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Equal(wrapperOutside ? ["open", "block", "close:Cancelled", "H"] : new[] { "open", "block", "read", "close:Cancelled" }, probe.Events);
    }

    [Theory, InlineData("graph"), InlineData("state"), InlineData("completedEntry")]
    public async Task GraphAndStateCancellationRunsOnlyActiveHandlersAndSkipsStateExit(string shape)
    {
        var probe = new Probe(); await using var services = Services(probe: probe);
        var b = new WorkflowBuilder<Data, object>([], "CancellationGraph" + shape);
        var definition = shape == "graph" ? b.CancellationScope(x => x.Flowchart(g => g.Start("work")
                .Node("work", body => body.WaitFor<Go>("body"))), h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))).Build()
            : b.CancellationScope(x => x.StateMachine(m => m.Start("waiting").State("waiting", state => state
                    .Entry(body => shape == "completedEntry" ? body.CancellationScope(y => y.Assign(c => c.WorkflowData.Code, c => 1),
                        h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "inactive"))) : body)
                    .Exit(body => body.Step<Mark>(s => s.Input(x => x.Text).From(c => "exit")))
                    .On<Go>("body", "done")).State("done", state => state.Final())),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))).Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var idle = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await client.CancelAsync(idle.InstanceId, new() { ReasonCode = "test" })).Status);
        Assert.Equal(new[] { "H" }, probe.Events);
    }

    [Fact]
    public async Task UnhandledFailureInHandlerForkCannotEscapeToTheSavedBusinessCatch()
    {
        var probe = new Probe(); await using var services = Services(probe: probe);
        var definition = new WorkflowBuilder<Data, object>([], "CancellationHandlerForkFailure").Try(t => t.CancellationScope(
            b => b.CancellationScope(x => x.Throw<BusinessError>(c => new("original")),
                h => h.Parallel(p => p.Do(x => x.Throw<InvalidOperationException>(c => new("cleanup"))).Do(x => x.WaitFor<Go>("abandoned")))),
            h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "outer"))))
            .Catch<Exception>(c => c.Step<Mark>(s => s.Input(x => x.Text).From(c => "C"))).Build();
        var done = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Faulted, done.Status); Assert.Equal(new[] { "outer" }, probe.Events);
    }

    [Fact]
    public async Task SagaRetryRestoresTheForwardWrapperWithANewOccurrenceAndOriginalPrevious()
    {
        var probe = new Probe(); var retry = new RetryProbe(); var store = new InMemoryWorkflowStateRepository();
        WorkflowDefinition Definition() => new WorkflowBuilder<Data, object>([], "CancellationSagaRetry").Saga(s => s.CancellationScope(
                b => b.Step<Mark>(a => a.Input(x => x.Text).From(c => "forward"))
                    .Step<RetryOnce>(a => a.Input(x => x.Incoming).From(c => c.PreviousStep.Text)
                        .Output(x => x.Incoming).To(c => c.WorkflowData.Text)),
                h => h.Step<Mark>(a => a.Input(x => x.Text).From(c => "H")).WaitFor<Go>("cleanup")))
            .OnError<BusinessError>(e => e.CompensateNone().ThenRetry(new RetryPolicy(TimeSpan.Zero, TimeSpan.Zero, 1, 1))).Build();
        ServiceProvider Open()
        {
            var c = new ServiceCollection(); c.AddIxIFlow(o => { o.VerifyCheckpointParts = true; o.PayloadThresholdBytes = 1; });
            c.AddSingleton(probe); c.AddSingleton(retry); c.AddSingleton<IWorkflowStateRepository>(store); return c.BuildServiceProvider();
        }
        string id;
        await using (var first = Open())
        {
            var idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(Definition(), new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status); id = idle.InstanceId;
        }
        await using var second = Open(); await second.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(Definition());
        var done = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "cleanup", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, done.Status); Assert.Equal(2, retry.Calls);
        Assert.Equal("forward", Assert.IsType<Data>(done.WorkflowData).Text);
        Assert.Equal(new[] { "forward", "H" }, probe.Events);
        Assert.Single(await second.GetRequiredService<IWorkflowClient>().GetTraceAsync(id), e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
    }

    [Theory, InlineData(DebugStepKind.Into), InlineData(DebugStepKind.Over)]
    public async Task HeldJoinDefersCancellationHandlerUntilContinueAcrossRestart(DebugStepKind kind)
    {
        var store = new InMemoryWorkflowStateRepository(); var probe = new Probe();
        var definition = new WorkflowBuilder<Data, object>([], "CancellationHeldJoin" + kind).WaitFor<Go>("start")
            .Try(t => t.Parallel(p => p.Do(b => b.CancellationScope(x => x.Throw<BusinessError>(c => new("original") { Code = 73 }).WithId("raise"),
                    h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))))
                .Do(b => b.WaitFor<Go>("held"))))
            .Catch<BusinessError, CodeFault>(c => c.Assign(x => x.WorkflowData.Code, x => x.Fault.Code)).Build();
        ServiceProvider Open()
        {
            var c = new ServiceCollection(); c.AddIxIFlow(o => { o.Debugging = true; o.VerifyCheckpointParts = true; o.PayloadThresholdBytes = 1; });
            c.AddSingleton<IWorkflowStateRepository>(store); c.AddSingleton(probe); return c.BuildServiceProvider();
        }
        string id;
        await using (var first = Open())
        {
            var client = first.GetRequiredService<IWorkflowClient>(); var ops = first.GetRequiredService<IWorkflowOperations>();
            id = (await client.RunAsync(definition, new Data())).InstanceId;
            await ops.SetBreakpointsAsync(id, [new("raise", "raise")], "set");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(id, "start", new Go())).Status);
            var before = (await ops.InspectAsync(id))!; var selected = before.DebugState.Stop!.BranchId;
            var held = before.Threads.Where(t => t.ContinuationId != selected).ToArray();
            Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(id, selected, kind, "step")).Status);
            var blocked = (await ops.InspectAsync(id))!;
            Assert.Equal("Blocked", blocked.DebugState.Stop!.Reason);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(held), System.Text.Json.JsonSerializer.Serialize(blocked.Threads.Where(t => t.ContinuationId != selected)));
            Assert.Empty(probe.Events);
            Assert.DoesNotContain(await client.GetTraceAsync(id), e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
        }
        await using var second = Open(); await second.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var resumed = second.GetRequiredService<IWorkflowClient>(); var operations = second.GetRequiredService<IWorkflowOperations>();
        Assert.Equal(WorkflowExecutionStatus.Paused, (await resumed.RecoverAsync(id)).Status);
        await operations.SetBreakpointsAsync(id, [], "clear");
        var result = await operations.ContinueAsync(id, "continue");
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status); Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Code);
        Assert.Equal(new[] { "H" }, probe.Events);
    }

    [Fact]
    public async Task FailedForEachItemKeepsItsFaultRouteWhileCancelledSiblingHasASeparateCause()
    {
        var store = new InMemoryWorkflowStateRepository(); await using var services = Services(store);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "CancellationItemCauses").Try(t => t.ForEach(c => new[] { 0, 1, 2 },
            (body, item) => body.CancellationScope(b => b.If(c => c.Item(item) == 0,
                    fail => fail.WaitFor<Go>("fail").Throw<BusinessError>(c => new("original") { Code = 73 }),
                    peer => peer.WaitFor<Go>("peer")), h => h.WaitFor<Go>("cleanup")), options => options.Parallel(2)))
            .Catch<BusinessError, CodeFault>(c => c.Assign(x => x.WorkflowData.Code, x => x.Fault.Code)).Build();
        var idle = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.ResumeAsync(idle.InstanceId, "fail", new Go())).Status);
        var checkpoint = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(idle.InstanceId))!));
        var failed = Assert.Single(checkpoint.Continuations, c => c.ItemUnwindCause == ItemEndCause.Failed);
        var cancelled = Assert.Single(checkpoint.Continuations, c => c.ItemUnwindCause == ItemEndCause.Cancelled);
        Assert.Equal(CancellationUnwindCause.HandledFaultCancellation, failed.UnwindCause);
        Assert.Equal(CancellationUnwindCause.Cancelled, cancelled.UnwindCause);
        Assert.NotNull(failed.UnwindFailure!.Route); Assert.Null(cancelled.UnwindFailure);
        Assert.Equal(CancellationScopePhase.Handler, Assert.Single(failed.Stack, f => f.CancellationState != null).CancellationState!.Phase);
        var waits = await client.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)); Assert.Equal(2, waits.Count);
        Assert.All(waits, wait => Assert.Equal("cleanup", wait.Key));
        await client.ReplyAsync(waits[0].Token, new Go());
        var done = await client.ReplyAsync(waits[1].Token, new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, done.Status); Assert.Equal(73, Assert.IsType<Data>(done.WorkflowData).Code);
        var trace = await client.GetTraceAsync(idle.InstanceId);
        var ended = trace.Select(e => e.Payload).OfType<ItemEndTrace>().ToArray();
        Assert.Equal(2, ended.Length); Assert.Contains(ended, e => e.Cause == ItemEndCause.Failed);
        Assert.Contains(ended, e => e.Cause == ItemEndCause.Cancelled);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task HandlerAndResourceCloseFollowLexicalNesting(bool wrapperOutside)
    {
        var probe = new Probe(); await using var services = Services(probe: probe);
        var b = new WorkflowBuilder<Data, object>([], "CancellationResource");
        var definition = wrapperOutside
            ? b.CancellationScope(x => x.Scope<Session>(null, y => y.WaitFor<Go>("body")),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))).Build()
            : b.Scope<Session>(null, x => x.CancellationScope(y => y.WaitFor<Go>("body"),
                h => h.Step<ReadSession>())).Build();
        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());
        var done = await client.CancelAsync(idle.InstanceId, new() { ReasonCode = "test" });
        Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
        // Suspending released the first live value. The handler reopens only when it uses it.
        Assert.Equal(wrapperOutside ? ["open", "close:Suspended", "H"] : new[] { "open", "close:Suspended", "open", "read", "close:Cancelled" }, probe.Events);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task EscapingFaultRunsHandlerBeforeSelectedSagaOnError(bool wrapperOutside)
    {
        var probe = new Probe(); await using var services = Services(probe: probe);
        var b = new WorkflowBuilder<Data, object>([], "CancellationSagaFault");
        var definition = wrapperOutside
            ? b.Try(t => t.CancellationScope(x => x.Saga(s => s.Step<Mark>(a => a.Input(x => x.Text).From(c => "forward")
                    .CompensateWith<Mark>(u => u.Input(x => x.Text).From(c => "undo"))).Throw<BusinessError>(c => new("original") { Code = 73 })),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))))
                .Catch<BusinessError, CodeFault>(c => c.Assign(x => x.WorkflowData.Code, x => x.Fault.Code)
                    .Step<Mark>(s => s.Input(x => x.Text).From(c => "C"))).Build()
            : b.Saga(s => s.Step<Mark>(a => a.Input(x => x.Text).From(c => "forward")
                    .CompensateWith<Mark>(u => u.Input(x => x.Text).From(c => "undo")))
                .CancellationScope(x => x.Throw<BusinessError>(c => new("original") { Code = 73 }),
                    h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))))
                .OnError<BusinessError, CodeFault>(e => e.Assign(x => x.WorkflowData.Code, x => x.Fault.Code)
                    .Step<Mark>(s => s.Input(x => x.Text).From(c => "C")).Compensate().ThenContinue()).Build();
        var done = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, done.Status);
        Assert.Equal(73, Assert.IsType<Data>(done.WorkflowData).Code);
        Assert.Equal(wrapperOutside ? ["forward", "undo", "H", "C"] : new[] { "forward", "H", "undo", "C" }, probe.Events);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task LosingBranchHandlerCanPauseAndWaitAndItsDataIsDiscarded(bool faulting)
    {
        var probe = new Probe(); var store = new InMemoryWorkflowStateRepository();
        var collection = new ServiceCollection(); collection.AddIxIFlow(o => { o.Debugging = true; o.VerifyCheckpointParts = true; });
        collection.AddSingleton(probe); collection.AddSingleton<IWorkflowStateRepository>(store);
        await using var services = collection.BuildServiceProvider();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var definition = new WorkflowBuilder<Data, object>([], "CancellationLoser").Try(t => t.Parallel(p => p.WaitAny()
            .Do(b => b.CancellationScope(x => x.WaitFor<Go>("body").Throw<BusinessError>(c => new("original") { Code = 73 }),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H")).WithId("handlerPick")
                    .WaitFor<Go>("cleanup").Assign(c => c.WorkflowData.Code, c => 999)))
            .Do(b => b.WaitFor<Go>("winner"))))
            .Catch<BusinessError, CodeFault>(c => c.Assign(x => x.WorkflowData.Code, x => x.Fault.Code))
            .Assign(c => c.WorkflowData.Text, c => "after").Build();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("handler", "handlerPick")], "set");
        var paused = await client.ResumeAsync(idle.InstanceId, faulting ? "body" : "winner", new Go());
        Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
        var inspection = (await ops.InspectAsync(idle.InstanceId))!;
        Assert.Contains(inspection.Threads.SelectMany(t => t.Frames), f => f.Kind == "CancellationHandler");
        Assert.Empty(probe.Events);
        var waiting = await ops.ContinueAsync(idle.InstanceId, "continue");
        Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);
        Assert.DoesNotContain(await client.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)), w => w.Key is "body" or "winner");
        var done = await client.ResumeAsync(idle.InstanceId, "cleanup", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, done.Status);
        Assert.Equal(faulting ? 73 : 0, Assert.IsType<Data>(done.WorkflowData).Code);
        Assert.Equal("after", Assert.IsType<Data>(done.WorkflowData).Text);
        Assert.Equal(new[] { "H" }, probe.Events);
        var trace = await ops.GetTraceAsync(idle.InstanceId);
        var handler = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.CancellationHandlerEntered);
        Assert.Equal(faulting ? CancellationUnwindCause.HandledFaultCancellation : CancellationUnwindCause.Cancelled,
            Assert.IsType<CancellationHandlerTrace>(handler.Entry.Payload).Cause);
        var replay = TraceReplay.Load(definition, trace);
        replay.Continue(); Assert.Empty(replay.Current.Threads);
    }

    [Fact]
    public async Task DuplicateCancellationPreservesHandlerWaitAndTerminationStopsPendingHandler()
    {
        var probe = new Probe(); await using var services = Services(probe: probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "CancellationDuplicate")
            .CancellationScope(b => b.WaitFor<Go>("body"), h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "entered"))
                .WaitFor<Go>("cleanup").Step<Mark>(s => s.Input(x => x.Text).From(c => "finished"))).Build();
        var idle = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.CancelAsync(idle.InstanceId, new() { ReasonCode = "test" })).Status);
        var before = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)));
        await client.CancelAsync(idle.InstanceId, new() { ReasonCode = "duplicate" });
        var after = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)));
        Assert.Equal(before.WaitId, after.WaitId);
        await services.GetRequiredService<IWorkflowOperations>().TerminateAsync(idle.InstanceId, "stop", actor: "test");
        Assert.Equal(new[] { "entered" }, probe.Events);
    }

    [Fact]
    public async Task ExecutionDeadlineRunsHandlerAndRetainsTimedOutOutcome()
    {
        var clock = new FakeTimeProvider(); var probe = new Probe();
        var collection = new ServiceCollection(); collection.AddIxIFlow(o => o.VerifyCheckpointParts = true);
        collection.AddSingleton<TimeProvider>(clock); collection.AddSingleton(probe);
        await using var services = collection.BuildServiceProvider();
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "CancellationDeadline").CancellationScope(b => b.WaitFor<Go>("body"),
            h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))).Build();
        var idle = await client.RunAsync(definition, new Data(), new() { ExecutionTimeout = TimeSpan.FromSeconds(1) });
        clock.Advance(TimeSpan.FromSeconds(2));
        var done = await client.RecoverAsync(idle.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.TimedOut, done.Status); Assert.Equal(new[] { "H" }, probe.Events);
    }

    [Theory, InlineData(DebugStepKind.Into, "body"), InlineData(DebugStepKind.Over, "after")]
    public async Task WrapperUsesContainerSteppingWithoutAnExtraHandlerPhasePick(DebugStepKind kind, string expected)
    {
        var collection = new ServiceCollection(); collection.AddIxIFlow(o => o.Debugging = true); collection.AddSingleton(new Probe());
        await using var services = collection.BuildServiceProvider();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var definition = new WorkflowBuilder<Data, object>([], "CancellationStep").WaitFor<Go>("start")
            .CancellationScope(b => b.Assign(c => c.WorkflowData.Code, c => 1).WithId("body"), h => { }).WithId("wrapper")
            .Assign(c => c.WorkflowData.Text, c => "after").WithId("after").Build();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("wrapper", "wrapper")], "set");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "start", new Go())).Status);
        await ops.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        var thread = Assert.Single((await ops.InspectAsync(idle.InstanceId))!.Threads);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(idle.InstanceId, thread.ContinuationId, kind, "step")).Status);
        Assert.Equal(expected, Assert.Single((await ops.InspectAsync(idle.InstanceId))!.Threads).AuthorStepId);
    }

    public sealed class Resource;
    public sealed class RetryProbe { public int Calls; }
    public sealed class RetryOnce(RetryProbe retry) : IAsyncActivity
    {
        public string Incoming { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        { if (++retry.Calls == 1) throw new BusinessError("retry"); return Task.CompletedTask; }
    }
    public sealed class LiveProbe { public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public sealed class Blocking(Probe probe, LiveProbe live) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        { probe.Events.Add("block"); live.Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
    }
    public sealed class Session(Probe probe) : IScopeActivity<Resource>
    {
        public Task<Resource> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken)
        { probe.Events.Add("open"); return Task.FromResult(new Resource()); }
        public Task CloseAsync(Resource value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken)
        { probe.Events.Add("close:" + closing.Reason); return Task.CompletedTask; }
    }
    public sealed class ReadSession(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        { Assert.NotNull(context.Scope<Resource>()); Assert.False(cancellationToken.IsCancellationRequested); probe.Events.Add("read"); return Task.CompletedTask; }
    }
}

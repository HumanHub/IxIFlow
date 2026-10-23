using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Json = System.Text.Json.JsonSerializer;
using static IxIFlow.Tests.ExecutionTests.ThrowRethrowTests;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "tracing")]
[Trait("Spec", "W-05")]
public sealed class ThrowDebuggerTests
{
    [Fact]
    public async Task ThrowDoesNotRevisitTheTryBreakpointWhenEnteringItsHandler()
    {
        var store = new InMemoryWorkflowStateRepository(); await using var services = Services(store);
        var definition = new WorkflowBuilder<Data, object>([], "ThrowTryBreakpoint").WaitFor<Go>("start")
            .Try(t => t.Throw<BusinessError>(ctx => new("private") { Code = 7 }))
            .Catch<BusinessError, CodeFault>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))).WithId("try").Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("try", "try")], "set");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "start", new Go())).Status);
        var completed = await ops.ContinueAsync(idle.InstanceId, "continue");
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(7, Assert.IsType<Data>(completed.WorkflowData).Result);
        Assert.Single(await store.GetTraceAsync(idle.InstanceId), e => e.Entry.EntryType == TraceEntryType.BreakpointHit);
    }

    [Theory]
    [InlineData(false, DebugStepKind.Into)] [InlineData(false, DebugStepKind.Over)]
    [InlineData(true, DebugStepKind.Into)] [InlineData(true, DebugStepKind.Over)]
    public async Task ThrowAtHeldJoinPersistsCompleteFaultUntilContinueAcrossRestart(bool each, DebugStepKind kind)
    {
        var store = new InMemoryWorkflowStateRepository(); var catalog = new WorkflowCatalog();
        var definition = new WorkflowBuilder<Data, object>([], "DeferredThrow" + each + kind).WaitFor<Go>("start")
            .Try(t =>
            {
                if (each) t.ForEach(ctx => ctx.WorkflowData.Items, (b, item) => b.Throw<BusinessError>(ctx => new("private") { Code = ctx.Item(item), Detail = "large" }).WithId("throw"), s => s.Parallel(2));
                else t.Parallel(p => p.Do(b => b.Throw<BusinessError>(ctx => new("private") { Code = 1, Detail = "large" }).WithId("throw"))
                    .Do(b => b.Delay(TimeSpan.FromHours(1))));
            }).Catch<BusinessError, DetailFault>(c => c.Step<ReadDetail>(s => s.Input(x => x.Code).From(ctx => ctx.Fault.Code)
                .Input(x => x.Detail).From(ctx => ctx.Fault.Detail).Output(x => x.Code).To(ctx => ctx.WorkflowData.Result)
                .Output(x => x.Detail).To(ctx => ctx.WorkflowData.Detail))).Build();
        await catalog.RegisterWorkflowAsync(definition);
        string id; WorkflowFaultLocation origin;
        await using (var first = Services(store, catalog))
        {
            var client = first.GetRequiredService<IWorkflowClient>(); var ops = first.GetRequiredService<IWorkflowOperations>();
            var idle = await client.RunAsync(definition, new Data()); id = idle.InstanceId;
            await ops.SetBreakpointsAsync(id, [new("target", "throw")], "set");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(id, "start", new Go())).Status);
            var before = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);
            var selected = before.Debug.Stop!.BranchId;
            var held = before.Continuations.Where(c => c.Id != selected).Select(c => Json.Serialize(c)).ToArray();
            Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(id, selected, kind, "step")).Status);
            var after = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);
            Assert.Equal("Blocked", after.Debug.Stop!.Reason);
            var saved = after.Continuations.Single(c => c.Id == selected).DeferredFailure!.Error;
            Assert.NotNull(saved); Assert.Null(saved.Fault); Assert.NotEmpty(saved.Properties);
            origin = saved.OriginalFaultLocation!;
            Assert.Equal(held, after.Continuations.Where(c => c.Id != selected).Select(c => Json.Serialize(c)).ToArray());
        }
        await using var second = Services(store, catalog);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await second.GetRequiredService<IWorkflowClient>().RecoverAsync(id)).Status);
        var ops2 = second.GetRequiredService<IWorkflowOperations>();
        await ops2.SetBreakpointsAsync(id, [], "clear");
        var result = await ops2.ContinueAsync(id, "continue");
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1, Assert.IsType<Data>(result.WorkflowData).Result);
        Assert.Equal("large", Assert.IsType<Data>(result.WorkflowData).Detail);
        var trace = await store.GetTraceAsync(id);
        var raised = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.FaultRaised);
        Assert.Equal(origin, Assert.IsType<FaultTrace>(raised.Entry.Payload).OriginalLocation);
        var replay = TraceReplay.Load(definition, trace);
        foreach (var pick in trace.Where(e => e.Entry.EntryType == TraceEntryType.StepEntered))
        { replay.Seek(pick.Sequence); Assert.Equal(pick.Entry.StepId, replay.CurrentStepId); }
        Assert.Contains(trace, e => e.Entry.EntryType == TraceEntryType.Continued);
    }

    [Theory]
    [InlineData(DebugStepKind.Into)] [InlineData(DebugStepKind.Over)]
    public async Task ThrowStepsToEligibleHandlerAfterOriginFrameIsRemoved(DebugStepKind kind)
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var services = Services(store);
        var definition = new WorkflowBuilder<Data, object>([], "HandlerStep" + kind).WaitFor<Go>("start")
            .Try(t => t.Throw<BusinessError>(ctx => new("private") { Code = 7 }).WithId("throw"))
            .Catch<BusinessError, CodeFault>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result)).WithId("handler")).Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("target", "throw")], "set");
        await client.ResumeAsync(idle.InstanceId, "start", new Go());
        var before = (await ops.InspectAsync(idle.InstanceId))!;
        var result = await ops.StepAsync(idle.InstanceId, before.DebugState.Stop!.BranchId, kind, "step");
        Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
        var after = (await ops.InspectAsync(idle.InstanceId))!;
        Assert.Equal(definition.StepTable.Single(e => e.AuthorId == "handler").CatalogId, after.DebugState.Stop!.StepId);
        Assert.Single(after.CatchFaults);
        Assert.Equal(0, Assert.IsType<Data>(result.WorkflowData).Result);
    }
}

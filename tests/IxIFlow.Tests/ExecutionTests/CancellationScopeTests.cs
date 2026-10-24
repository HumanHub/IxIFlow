using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "W-06")]
public sealed class CancellationScopeTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task NormalOrEmptyBodyDoesNotRunHandlerAndRestoresPrevious(bool empty)
    {
        await using var services = Services();
        var definition = Workflow.Create<Data>("CancellationNormal").Step<Mark>(s => s.Input(x => x.Text).From(c => "entry"))
            .CancellationScope(b => { if (!empty) b.Step<Mark>(s => s.Input(x => x.Text).From(c => "body")); },
                h => h.Throw<InvalidOperationException>(c => new("must not run")))
            .Assign(c => c.WorkflowData.Text, c => c.PreviousStep.Text).Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("entry", Assert.IsType<Data>(result.WorkflowData).Text);
    }

    public static TheoryData<string, string[]> Orders => new()
    {
        { "aroundFinally", ["F", "H"] }, { "insideFinally", ["H", "F"] },
        { "nested", ["inner", "F", "outer"] },
        { "aroundSaga", ["forward", "undo", "H"] },
        { "insideSaga", ["forward", "H", "undo"] }
    };

    [Theory, MemberData(nameof(Orders))]
    public async Task ExplicitCancellationWalksNestingInOrder(string shape, string[] expected)
    {
        var probe = new Probe(); await using var services = Services(probe: probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var start = await client.RunAsync(Definition(shape), new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, start.Status);
        var done = await client.CancelAsync(start.InstanceId, new() { ReasonCode = "test" });
        Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
        Assert.Equal(expected, probe.Events);
        Assert.All(probe.Tokens, cancelled => Assert.False(cancelled));
        var traces = await client.GetTraceAsync(start.InstanceId);
        var entered = traces.Where(e => e.EntryType == TraceEntryType.CancellationHandlerEntered).ToArray();
        Assert.NotEmpty(entered);
        Assert.Equal(entered.Length, traces.Count(e => e.EntryType == TraceEntryType.CancellationHandlerCompleted));
        Assert.All(entered, e => Assert.Equal(CancellationUnwindCause.Cancelled, Assert.IsType<CancellationHandlerTrace>(e.Payload).Cause));
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task EscapingFaultRunsInnerFinallyThenHandlerBeforeOuterCatchWithOriginalProperties(bool finallyOutside)
    {
        var probe = new Probe(); await using var services = Services(probe: probe);
        var builder = new WorkflowBuilder<Data, object>([], "CancellationFaultRoute");
        var definition = builder.Try(outer =>
        {
            if (finallyOutside) outer.Try(t => t.CancellationScope(b => b.Throw<BusinessError>(c => new("original") { Code = 73 }),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))))
                .Finally(f => f.Step<Mark>(s => s.Input(x => x.Text).From(c => "F")));
            else outer.CancellationScope(b => b.Try(t => t.Throw<BusinessError>(c => new("original") { Code = 73 }))
                .Finally(f => f.Step<Mark>(s => s.Input(x => x.Text).From(c => "F"))),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H")));
        }).Catch<BusinessError, CodeFault>(c => c.Assign(x => x.WorkflowData.Code, x => x.Fault.Code)
            .Step<Mark>(s => s.Input(x => x.Text).From(x => "C"))).Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Code);
        Assert.Equal(finallyOutside ? ["H", "F", "C"] : new[] { "F", "H", "C" }, probe.Events);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task InternallyHandledOrCompletelyUnhandledFaultDoesNotRunHandler(bool handled)
    {
        var probe = new Probe(); await using var services = Services(probe: probe);
        var definition = new WorkflowBuilder<Data, object>([], "CancellationIneligible").CancellationScope(b =>
        {
            if (handled) b.Try(t => t.Throw<BusinessError>(c => new("original"))).Catch<BusinessError>(c => { });
            else b.Try(t => t.Throw<BusinessError>(c => new("original")))
                .Finally(f => f.Step<Mark>(s => s.Input(x => x.Text).From(c => "F")));
        }, h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))).Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(handled ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Faulted, result.Status);
        Assert.DoesNotContain("H", probe.Events);
        Assert.Equal(handled ? [] : new[] { "F" }, probe.Events);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task RethrowSelectsItsOuterRouteBeforeDecidingHandlerEligibility(bool outerCatch)
    {
        var probe = new Probe(); await using var services = Services(probe: probe);
        var b = new WorkflowBuilder<Data, object>([], "CancellationRethrowRoute");
        var definition = outerCatch ? b.Try(t => t.Try(x => x.Throw<BusinessError>(c => new("original")))
                .Catch<BusinessError>(c => c.CancellationScope(x => x.Rethrow(), h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H")))))
            .Catch<BusinessError>(c => c.Step<Mark>(s => s.Input(x => x.Text).From(c => "C"))).Build()
            : b.Try(x => x.Throw<BusinessError>(c => new("original")))
                .Catch<BusinessError>(c => c.CancellationScope(x => x.Rethrow(), h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H")))).Build();
        var done = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(outerCatch ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Faulted, done.Status);
        Assert.Equal(outerCatch ? new[] { "H", "C" } : [], probe.Events);
    }

    [Fact]
    public async Task RestartInHandlerRetainsPhaseRouteFaultAndOriginalLocation()
    {
        var store = new InMemoryWorkflowStateRepository(); var probe = new Probe();
        WorkflowDefinition Definition() => new WorkflowBuilder<Data, object>([], "CancellationHandlerRestart")
            .Try(t => t.CancellationScope(b => b.Throw<BusinessError>(c => new("private") { Code = 73 }).WithId("origin"),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))
                    .WaitFor<Go>("cleanup").Assign(c => c.WorkflowData.Text, c => "finished")))
            .Catch<BusinessError, CodeFault>(c => c.Assign(x => x.WorkflowData.Code, x => x.Fault.Code)).Build();
        string id; WorkflowFaultLocation origin;
        await using (var first = Services(store, probe))
        {
            var result = await first.GetRequiredService<IWorkflowClient>().RunAsync(Definition(), new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status); id = result.InstanceId;
            var checkpoint = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(id))!));
            var state = Assert.Single(checkpoint.Continuations[0].Stack, f => f.CancellationState != null).CancellationState!;
            Assert.Equal(CancellationScopePhase.Handler, state.Phase);
            Assert.Equal(CancellationUnwindCause.HandledFaultCancellation, state.Cause);
            Assert.NotNull(state.Failure!.Route); origin = state.Failure.Error.OriginalFaultLocation!;
            Assert.Contains((await first.GetRequiredService<IWorkflowOperations>().InspectAsync(id))!.Threads
                .SelectMany(t => t.Frames), f => f.Kind == "CancellationHandler" && f.Phase == "Handler");
        }
        await using var second = Services(store, probe);
        await second.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(Definition());
        var done = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "cleanup", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, done.Status);
        Assert.Equal(73, Assert.IsType<Data>(done.WorkflowData).Code);
        Assert.Equal("finished", Assert.IsType<Data>(done.WorkflowData).Text);
        Assert.Equal(new[] { "H" }, probe.Events);
        Assert.EndsWith("/sequence/0", origin.StepId);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task HandlerFailureRunsRemainingOuterCleanupAndCannotBeHiddenBySavedCatch(bool locallyHandled)
    {
        var probe = new Probe(); await using var services = Services(probe: probe);
        var definition = new WorkflowBuilder<Data, object>([], "CancellationCleanupFailure").Try(t => t.CancellationScope(
            b => b.CancellationScope(x => x.Throw<BusinessError>(c => new("original")), h =>
            {
                if (locallyHandled) h.Try(x => x.Throw<InvalidOperationException>(c => new("cleanup")))
                    .Catch<InvalidOperationException>(c => c.Step<Mark>(s => s.Input(x => x.Text).From(c => "local")));
                else h.Throw<InvalidOperationException>(c => new("cleanup"));
            }), h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "outer"))))
            .Catch<Exception>(c => c.Step<Mark>(s => s.Input(x => x.Text).From(c => "C")))
            .Finally(f => f.Step<Mark>(s => s.Input(x => x.Text).From(c => "F"))).Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(locallyHandled ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(locallyHandled ? ["local", "outer", "C", "F"] : new[] { "outer", "F" }, probe.Events);
    }

    [Fact]
    public async Task TerminationRunsNoWorkflowCleanup()
    {
        var probe = new Probe(); await using var services = Services(probe: probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var run = await client.RunAsync(Definition("nested"), new Data());
        await services.GetRequiredService<IWorkflowOperations>().TerminateAsync(run.InstanceId, "hard-stop", actor: "test");
        Assert.Empty(probe.Events);
    }

    [Fact]
    public void CloneCatalogHashAndExplicitEmptyHandlerAreStructural()
    {
        var builder = new WorkflowBuilder<Data, object>([], "CancellationShape");
        builder.CancellationScope(b => b.Assign(c => c.WorkflowData.Code, c => 1), h => { }).WithId("scope");
        var clone = builder.Steps[0].Clone();
        Assert.Equal(new[] { "sequence", "cancelled" }, clone.ChildScopes().Where(s => s.Kind is "sequence" or "cancelled").Select(s => s.Kind));
        Assert.NotSame(builder.Steps[0].SequenceSteps[0], clone.SequenceSteps[0]);
        var definition = builder.Build();
        var entry = Assert.Single(definition.StepTable, s => s.Kind == WorkflowStepType.CancellationScope);
        Assert.NotNull(entry.SourceLocation); Assert.NotNull(entry.CancellationScope);
        Assert.NotEqual(WorkflowDefinitionValidator.Inspect(definition), WorkflowDefinitionValidator.Inspect(
            new WorkflowBuilder<Data, object>([], "CancellationShape").Sequence(b => b.Assign(c => c.WorkflowData.Code, c => 1)).Build()));
    }

    [Theory, InlineData(false), InlineData(true)]
    public void CheckpointRefusesMissingHandlerCauseOrInvalidPhase(bool invalidPhase)
    {
        var checkpoint = new ExecutionCheckpoint { StructureHash = "shape", Continuations = [new()
        { Stack = [new() { ScopeId = "root", CancellationState = new()
        { Phase = invalidPhase ? (CancellationScopePhase)99 : CancellationScopePhase.Handler } }] }] };
        Assert.Throws<System.Text.Json.JsonException>(() => ExecutionCheckpoint.Read(System.Text.Json.JsonSerializer.Serialize(checkpoint)));
    }

    internal static WorkflowDefinition Definition(string shape)
    {
        var b = new WorkflowBuilder<Data, object>([], "CancellationOrder" + shape);
        switch (shape)
        {
            case "aroundFinally": return b.CancellationScope(x => x.Try(t => t.WaitFor<Go>("body"))
                .Finally(f => f.Step<Mark>(s => s.Input(x => x.Text).From(c => "F"))),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))).Build();
            case "insideFinally": return b.Try(x => x.CancellationScope(t => t.WaitFor<Go>("body"),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))))
                .Finally(f => f.Step<Mark>(s => s.Input(x => x.Text).From(c => "F"))).Build();
            case "nested": return b.CancellationScope(x => x.Try(t => t.CancellationScope(y => y.WaitFor<Go>("body"),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "inner"))))
                .Finally(f => f.Step<Mark>(s => s.Input(x => x.Text).From(c => "F"))),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "outer"))).Build();
            case "aroundSaga": return b.CancellationScope(x => x.Saga(s => s.Step<Mark>(a => a.Input(x => x.Text).From(c => "forward")
                    .CompensateWith<Mark>(u => u.Input(x => x.Text).From(c => "undo"))).WaitFor<Go>("body")),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))).Build();
            default: return b.Saga(s => s.CancellationScope(x => x.Step<Mark>(a => a.Input(x => x.Text).From(c => "forward")
                .CompensateWith<Mark>(u => u.Input(x => x.Text).From(c => "undo"))).WaitFor<Go>("body"),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H")))).Build();
        }
    }

    internal static ServiceProvider Services(IWorkflowStateRepository? store = null, Probe? probe = null)
    {
        var services = new ServiceCollection(); services.AddIxIFlow(o => o.VerifyCheckpointParts = true);
        services.AddSingleton(probe ?? new Probe());
        if (store != null) services.AddSingleton(store);
        return services.BuildServiceProvider();
    }
    public sealed class Data { public int Code { get; set; } public string Text { get; set; } = ""; }
    public sealed class Go;
    public sealed class CodeFault { public int Code { get; set; } }
    public sealed class BusinessError(string message) : Exception(message) { public int Code { get; set; } }
    public sealed class Probe { public List<string> Events { get; } = []; public List<bool> Tokens { get; } = []; }
    public sealed class Mark(Probe probe) : IAsyncActivity
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        { probe.Events.Add(Text); probe.Tokens.Add(cancellationToken.IsCancellationRequested); return Task.CompletedTask; }
    }
}

using System.Linq.Expressions;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Json = System.Text.Json.JsonSerializer;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "try")]
[Trait("Spec", "W-05")]
public sealed class ThrowRethrowTests
{
    [Theory]
    [InlineData("root")] [InlineData("try")] [InlineData("finally")] [InlineData("saga")]
    public void RethrowRequiresALexicalTryCatch(string placement)
    {
        var builder = new WorkflowBuilder<Data, object>([], "InvalidRethrow");
        var error = Assert.Throws<WorkflowEngineException>(() =>
        {
            switch (placement)
            {
                case "root": builder.Rethrow().Build(); break;
                case "try": builder.Try(b => b.Rethrow()).Finally(b => { }).Build(); break;
                case "finally": builder.Try(b => { }).Finally(b => b.Rethrow()).Build(); break;
                default: builder.Saga(b => b.Throw<InvalidOperationException>(ctx => new("business")))
                    .OnError(e => e.Rethrow()).Build(); break;
            }
        });
        Assert.Contains("RETHROW001", error.Message);
    }

    [Fact]
    public void ThrowRejectsControlTypesAndInvalidDirectFactories()
    {
        Assert.Contains("THROW001", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Control")
            .Throw<OperationCanceledException>(ctx => new()).Build()).Message);
        Assert.Contains("THROW001", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Engine")
            .Throw<WorkflowEngineException>(ctx => new(WorkflowErrorKind.InvalidState, "control", null)).Build()).Message);
        var builder = new WorkflowBuilder<Data, object>([], "Mismatch");
        builder.Throw<InvalidOperationException>(ctx => new("message"));
        builder.Steps[0].ExceptionFactoryReturnType = typeof(Exception);
        Assert.Contains("THROW002", Assert.Throws<WorkflowEngineException>(() => builder.Build()).Message);
    }

    [Fact]
    public void ThrowChecksPurityAndManuallyConstructedMutationTrees()
    {
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Clock")
            .Throw<BusinessError>(ctx => new(DateTime.UtcNow.ToString()) { Code = 7 }).Build());
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("Mutator")
            .Throw<BusinessError>(ctx => new(ctx.WorkflowData.Items.Remove(1).ToString())).Build());
        var context = Expression.Parameter(typeof(WorkflowContext<Data, object>), "ctx");
        var body = Expression.Block(Expression.Assign(Expression.Property(Expression.Property(context, "WorkflowData"), "Code"), Expression.Constant(3)),
            Expression.New(typeof(InvalidOperationException)));
        var factory = Expression.Lambda<Func<WorkflowContext<Data, object>, InvalidOperationException>>(body, context);
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("WriteNode").Throw(factory).Build());
    }

    [Fact]
    public async Task ThrowConstructorAndInitializerReadIncomingPreviousWithoutReplacingIt()
    {
        var definition = Workflow.Create<Data>("ThrowPrevious").Step<Echo>(s => s.Input(x => x.Value).From(ctx => 9))
            .Try(t => t.Throw<BusinessError>(ctx => new(ctx.PreviousStep.Value.ToString()) { Code = ctx.PreviousStep.Value }))
            .Catch<BusinessError, CodeFault>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code + ctx.PreviousStep.Value)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))).Build();
        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(18, Assert.IsType<Data>(result.WorkflowData).Result);
    }

    [Fact]
    public void ThrowCloneCatalogAndExpressionSensitiveHashAreComplete()
    {
        var first = Workflow.Create<Data>("Hash").Throw<BusinessError>(ctx => new("first") { Code = 7 }).WithId("raise").Build();
        var second = Workflow.Create<Data>("Hash").Throw<BusinessError>(ctx => new("second") { Code = 8 }).WithId("raise").Build();
        Assert.NotEqual(WorkflowDefinitionValidator.Inspect(first), WorkflowDefinitionValidator.Inspect(second));
        var clone = first.Steps[0].Clone();
        Assert.Same(first.Steps[0].ExceptionExpression, clone.ExceptionExpression);
        Assert.Same(first.Steps[0].ExceptionFactory, clone.ExceptionFactory);
        var entry = Assert.Single(first.StepTable);
        Assert.Equal(WorkflowStepType.Throw, entry.Kind);
        Assert.Contains(nameof(BusinessError), entry.ExceptionType);
        Assert.Contains(nameof(ThrowRethrowTests), entry.SourceLocation);
    }

    [Theory]
    [InlineData("plain")] [InlineData("switch")] [InlineData("parallel")] [InlineData("each")]
    [InlineData("graph")] [InlineData("state")] [InlineData("inline")]
    public async Task ThrowComposesWithEveryBodyAndNeverRunsItsFollowingSibling(string shape)
    {
        var builder = new WorkflowBuilder<Data, object>([], "Composition" + shape);
        IWorkflowBuilder<Data, Echo> Raise(IWorkflowBuilder<Data, object> b) => b.Throw<BusinessError>(ctx => new("declined") { Code = ctx.WorkflowData.Code })
            .Step<Echo>(s => s.Input(x => x.Value).From(ctx => 999).Output(x => x.Value).To(ctx => ctx.WorkflowData.Code));
        var definition = builder.Try(t =>
        {
            switch (shape)
            {
                case "switch": t.Switch(ctx => 1, c => c.Case(1, b => Raise(b))); break;
                case "parallel": t.Parallel(p => p.Do(b => Raise(b)).Do(b => b.Delay(TimeSpan.FromHours(1)))); break;
                case "each": t.ForEach(ctx => ctx.WorkflowData.Items, (b, item) => b.Throw<BusinessError>(ctx => new("declined") { Code = ctx.Item(item) })); break;
                case "graph": t.Flowchart(g => g.Start("one").Node("one", Raise)); break;
                case "state": t.StateMachine(g => g.Start("one").State("one", s => s.Entry(Raise).On<Go>("go", "done")).State("done", s => s.Final())); break;
                case "inline": t.Use<InlineBody, Data>(); break;
                default: Raise(t); break;
            }
        }).Catch<BusinessError, CodeFault>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
            .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))).Build();
        await using var services = Services();
        var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data { Code = 7 });
        Assert.Equal(WorkflowExecutionStatus.Success, run.Status);
        Assert.Equal(shape == "each" ? 1 : shape == "inline" ? 11 : 7, Assert.IsType<Data>(run.WorkflowData).Result);
        Assert.Equal(7, Assert.IsType<Data>(run.WorkflowData).Code);
    }

    [Fact]
    public async Task ThrowWithoutHandlerFailsTheInstanceAndSagaOnErrorUsesTheFault()
    {
        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        var failed = await client.RunAsync(Workflow.Create<Data>("Unhandled").Throw<BusinessError>(ctx => new("private") { Code = 7 }).Build(), new Data());
        Assert.Equal(WorkflowExecutionStatus.Faulted, failed.Status);
        var saga = new WorkflowBuilder<Data, object>([], "SagaThrow").Saga(s => s.Switch(ctx => 1, c => c.Case(1,
            b => b.Throw<BusinessError>(ctx => new("declined") { Code = 7 }))))
            .OnError<BusinessError, CodeFault>(e => e.Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result)).Compensate().ThenContinue()).Build();
        var handled = await client.RunAsync(saga, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, handled.Status);
        Assert.Equal(7, Assert.IsType<Data>(handled.WorkflowData).Result);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task InvalidFactoryResultsAndConstructorFailureAreCatchable(int factory)
    {
        Expression<Func<WorkflowContext<Data, object>, BusinessError>> expression = factory switch
        {
            0 => ctx => null!, 1 => ctx => new DerivedError(), _ => ctx => new BusinessError(FailConstruction())
        };
        var definition = new WorkflowBuilder<Data, object>([], "InvalidFactory" + factory).Try(b => b.Throw(expression))
            .Catch<InvalidOperationException>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => 1)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))).Build();
        await using var services = Services();
        var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, run.Status);
        Assert.Equal(1, Assert.IsType<Data>(run.WorkflowData).Result);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RethrowAcrossRestartAndForkKeepsUnionOriginAndIgnoresDtoMutation(bool fork)
    {
        var store = new InMemoryWorkflowStateRepository(); var catalog = new WorkflowCatalog();
        var definition = new WorkflowBuilder<Data, object>([], "RestartRethrow" + fork).Try(outer => outer
            .Try(inner => inner.Throw<BusinessError>(ctx => new("original") { Code = 73, Detail = "preserved" }).WithId("origin"))
            .Catch<BusinessError, CodeFault>(c =>
            {
                c.WaitFor<Go>("caught").Step<MutateFault>(s => s.Input(x => x.Fault).From(ctx => ctx.Fault));
                if (fork) c.Parallel(p => p.Do(b => b.Rethrow()).Do(b => b.Delay(TimeSpan.FromHours(1))));
                else c.Try(b => b.Throw<ArgumentException>(ctx => new("nested")))
                    .Catch<ArgumentException>(b => { }).Finally(b => b.Rethrow());
            }).Catch<Exception>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => 999)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))))
            .Catch<BusinessError, DetailFault>(c => c.Step<ReadDetail>(s => s.Input(x => x.Code).From(ctx => ctx.Fault.Code)
                .Input(x => x.Detail).From(ctx => ctx.Fault.Detail).Output(x => x.Code).To(ctx => ctx.WorkflowData.Result)
                .Output(x => x.Detail).To(ctx => ctx.WorkflowData.Detail))).Build();
        await catalog.RegisterWorkflowAsync(definition);
        string id; WorkflowFaultLocation origin;
        await using (var first = Services(store, catalog))
        {
            var run = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, run.Status); id = run.InstanceId;
            var inspection = (await first.GetRequiredService<IWorkflowOperations>().InspectAsync(id))!;
            origin = Assert.Single(inspection.CatchFaults).OriginalLocation!;
            Assert.Equal(definition.StepTable.Single(e => e.AuthorId == "origin").CatalogId, origin.StepId);
        }
        await using var second = Services(store, catalog);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "caught", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(73, Assert.IsType<Data>(result.WorkflowData).Result);
        Assert.Equal("preserved", Assert.IsType<Data>(result.WorkflowData).Detail);
        var trace = await store.GetTraceAsync(id);
        var rethrow = Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.FaultRethrown);
        Assert.Equal(origin, Assert.IsType<FaultTrace>(rethrow.Entry.Payload).OriginalLocation);
        Assert.DoesNotContain("original", Json.Serialize(rethrow.Entry.Payload));
    }

    [Fact]
    public async Task RethrowBindsNearestCatchThenRestoresTheOuterBinding()
    {
        var definition = new WorkflowBuilder<Data, object>([], "NestedCatch").Try(o => o
            .Try(b => b.Throw<BusinessError>(ctx => new("outer") { Code = 10 }))
            .Catch<BusinessError, CodeFault>(c => c.Try(n => n
                .Try(b => b.Throw<BusinessError>(ctx => new("inner") { Code = 20 }))
                .Catch<BusinessError, CodeFault>(b => b.Rethrow()))
                .Catch<BusinessError, CodeFault>(b => b.Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                    .Output(x => x.Value).To(ctx => ctx.WorkflowData.Code))).Rethrow()))
            .Catch<BusinessError, CodeFault>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))).Build();
        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(20, Assert.IsType<Data>(result.WorkflowData).Code);
        Assert.Equal(10, Assert.IsType<Data>(result.WorkflowData).Result);
        Assert.Equal(2, definition.StepTable.Count(e => e.Kind == WorkflowStepType.Rethrow));
        Assert.Equal(2, definition.StepTable.Where(e => e.Kind == WorkflowStepType.Rethrow).Select(e => e.RethrowOwnerId).Distinct().Count());
    }

    [Fact]
    public async Task ThrowFactoryCanReadTheTypedEnclosingCatchInNestedCallbacks()
    {
        var definition = new WorkflowBuilder<Data, object>([], "TypedFactory").Try(t => t
            .Try(b => b.Throw<BusinessError>(ctx => new("one") { Code = 3 }))
            .Catch<BusinessError, CodeFault>(c => c.Switch(ctx => ctx.Fault.Code, cases => cases.Case(3,
                b => b.Throw<BusinessError>(ctx => new("two") { Code = ctx.Fault.Code + 1 })))))
            .Catch<BusinessError, CodeFault>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))).Build();
        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(4, Assert.IsType<Data>(result.WorkflowData).Result);
    }

    [Fact]
    [Trait("Spec", "W-04")]
    public async Task ThrowInAssignBodiesAndAssignInCatchPreserveTheOriginalRethrowFault()
    {
        var definition = Workflow.Create<Data>("AssignThrowRethrow").Step<Echo>(s => s.Input(x => x.Value).From(ctx => 7))
            .Assign(ctx => ctx.WorkflowData.Code, ctx => ctx.PreviousStep.Value)
            .Try(outer => outer.Try(body => body
                .Sequence(b => b.Assign(ctx => ctx.WorkflowData.Code, ctx => ctx.WorkflowData.Code + 1)
                    .Assign(ctx => ctx.WorkflowData.Items[0], ctx => ctx.WorkflowData.Code))
                .ForEach(ctx => ctx.WorkflowData.Items, (b, item) => b
                    .Assign(ctx => ctx.WorkflowData.Code, ctx => ctx.WorkflowData.Code + ctx.Item(item)))
                .Assign(ctx => ctx.WorkflowData.Detail, ctx => "assigned")
                .Throw<BusinessError>(ctx => new("original") { Code = ctx.WorkflowData.Code, Detail = ctx.WorkflowData.Detail })
                .WithId("origin")
                .Assign(ctx => ctx.WorkflowData.Result, ctx => -1))
                .Catch<BusinessError, DetailFault>(caught => caught
                    .Assign(ctx => ctx.WorkflowData.Result, ctx => ctx.Fault.Code)
                    .Assign(ctx => ctx.WorkflowData.Code, ctx => 999)
                    .Sequence(b => b.Assign(ctx => ctx.WorkflowData.Detail, ctx => ctx.Fault.Detail + " catch").Rethrow())
                    .Assign(ctx => ctx.WorkflowData.Result, ctx => -2)))
            .Catch<BusinessError, DetailFault>(caught => caught
                .Assign(ctx => ctx.WorkflowData.Result, ctx => ctx.WorkflowData.Result + ctx.Fault.Code)
                .Assign(ctx => ctx.WorkflowData.Detail, ctx => ctx.WorkflowData.Detail + " " + ctx.Fault.Detail))
            .Build();
        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(999, data.Code);
        Assert.Equal(36, data.Result);
        Assert.Equal("assigned catch assigned", data.Detail);
        Assert.Equal(new[] { 8, 2 }, data.Items);
        var trace = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(result.InstanceId);
        Assert.Equal(11, trace.Count(entry => entry.Entry.EntryType == TraceEntryType.DataAssigned));
        var raised = Assert.IsType<FaultTrace>(Assert.Single(trace, entry => entry.Entry.EntryType == TraceEntryType.FaultRaised).Entry.Payload);
        var rethrown = Assert.IsType<FaultTrace>(Assert.Single(trace, entry => entry.Entry.EntryType == TraceEntryType.FaultRethrown).Entry.Payload);
        Assert.Equal(raised.OriginalLocation, rethrown.OriginalLocation);
        Assert.Equal(definition.StepTable.Single(entry => entry.AuthorId == "origin").CatalogId, rethrown.OriginalLocation!.StepId);
    }

    private static string FailConstruction() => throw new InvalidOperationException("constructor input failed");
    internal static ServiceProvider Services(IWorkflowStateRepository? store = null, IWorkflowCatalog? catalog = null)
    {
        var services = new ServiceCollection().AddLogging(); services.AddIxIFlow(o => o.Debugging = true);
        if (store != null) services.AddSingleton(store);
        if (catalog != null) services.AddSingleton(catalog);
        return services.BuildServiceProvider();
    }
    public sealed class Data { public int Code { get; set; } public int Result { get; set; } public string Detail { get; set; } = ""; public List<int> Items { get; set; } = [1, 2]; }
    public sealed class Go;
    public class BusinessError(string message) : Exception(message) { public int Code { get; init; } public string Detail { get; init; } = ""; }
    public sealed class DerivedError() : BusinessError("derived");
    public sealed class CodeFault { public int Code { get; set; } }
    public sealed class DetailFault { public int Code { get; set; } public string Detail { get; set; } = ""; }
    public sealed class Echo : IAsyncActivity
    { [ActivityInputOutput] public int Value { get; set; } public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    public sealed class ReadDetail : IAsyncActivity
    { [ActivityInputOutput] public int Code { get; set; } [ActivityInputOutput] public string Detail { get; set; } = ""; public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    public sealed class MutateFault : IAsyncActivity
    { [ActivityInput] public CodeFault Fault { get; set; } = new(); public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) { Fault.Code = 999; return Task.CompletedTask; } }
    [Workflow("ThrowInline", Version = 1)]
    public sealed class InlineBody : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> builder) => builder.Throw<BusinessError>(ctx => new("inline") { Code = 11 }); }
}

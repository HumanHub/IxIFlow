using System.Collections.Concurrent;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.ExecutionTests.ThrowRethrowTests;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "try")]
[Trait("Spec", "W-05")]
public sealed class ThrowCompositionTests
{
    [Theory]
    [InlineData("sequence")] [InlineData("if")] [InlineData("switch")] [InlineData("while")] [InlineData("do")]
    [InlineData("parallel")] [InlineData("each")] [InlineData("parallel-each")] [InlineData("graph")] [InlineData("state")]
    [InlineData("try")] [InlineData("saga")]
    public async Task ThrowFactoriesRetainTypedCatchBindingInEveryChildCallback(string shape)
    {
        var definition = new WorkflowBuilder<Data, object>([], "CatchFactory" + shape).Try(outer => outer
            .Try(b => b.Throw<BusinessError>(ctx => new("first") { Code = 7 }))
            .Catch<BusinessError, CodeFault>(caught =>
            {
                void Raise(ICatchWorkflowBuilder<Data, CodeFault, object> b) => b.Throw<BusinessError>(ctx => new("second") { Code = ctx.Fault.Code + 1 });
                switch (shape)
                {
                    case "sequence": caught.Sequence(Raise); break;
                    case "if": caught.If(ctx => true, Raise); break;
                    case "switch": caught.Switch(ctx => ctx.Fault.Code, c => c.Case(7, Raise)); break;
                    case "while": caught.WhileDo(conditionExpression: ctx => ctx.Fault.Code > 0, configure: Raise); break;
                    case "do": caught.DoWhile(Raise, conditionExpression: ctx => false); break;
                    case "parallel": caught.Parallel(p => p.Do(Raise)); break;
                    case "each": case "parallel-each": caught.ForEach(itemsExpression: ctx => ctx.WorkflowData.Items, body: (b, item) => Raise(b), setup: s => { if (shape == "parallel-each") s.Parallel(2); }); break;
                    case "graph": caught.Flowchart(g => g.Start("one").Node("one", b => b.Throw<BusinessError>(ctx => new("second") { Code = ctx.Fault.Code + 1 }))); break;
                    case "state": caught.StateMachine(g => g.Start("one").State("one", s => s.Entry(b => b.Throw<BusinessError>(ctx => new("second") { Code = ctx.Fault.Code + 1 })).On<Go>("go", "done")).State("done", s => s.Final())); break;
                    case "try": caught.Try(Raise).Finally(b => { }); break;
                    default: caught.Saga(s => s.Throw<BusinessError>(ctx => new("second") { Code = ctx.Fault.Code + 1 })); break;
                }
            }))
            .Catch<BusinessError, CodeFault>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))).Build();
        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(8, Assert.IsType<Data>(result.WorkflowData).Result);
    }

    [Fact]
    public async Task ThrowUsesOrderedBaseCatchAndSupportsEmptyFault()
    {
        var definition = new WorkflowBuilder<Data, object>([], "OrderedCatch").Try(t => t.Throw<DerivedError>(ctx => new() { Code = 42 }))
            .Catch<BusinessError, CodeFault>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result)))
            .Catch<DerivedError>(c => c.Throw<Exception>(ctx => new("wrong sibling"))).Build();
        await using var services = Services(); var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(42, Assert.IsType<Data>(result.WorkflowData).Result);
        var empty = new WorkflowBuilder<Data, object>([], "EmptyCatch").Try(b => b.Throw<BusinessError>(ctx => new("empty") { Code = 1 }))
            .Catch(c => c.WaitFor<Go>("caught")).Build();
        var waiting = await client.RunAsync(empty, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.ResumeAsync(waiting.InstanceId, "caught", new Go())).Status);
    }

    [Fact]
    public void RethrowCannotBindAcrossAnInlineWorkflowBoundary()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<Data, object>([], "InvalidChild")
            .Try(b => b.Throw<BusinessError>(ctx => new("outer")))
            .Catch<BusinessError>(c => c.Use<InvalidChild, Data>()).Build());
        Assert.Contains("RETHROW001", error.Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ThrowSettlesSiblingFinallyBeforeOuterCatch(bool each)
    {
        var effects = new Effects();
        var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.AddSingleton(effects).BuildServiceProvider();
        await using var owned = services;
        var definition = new WorkflowBuilder<Data, object>([], "ThrowCleanup" + each).Try(t =>
        {
            if (each) t.ForEach(itemsExpression: ctx => ctx.WorkflowData.Items, body: (b, item) => b
                .Try(child => child.If(ctx => ctx.Item(item) == 1,
                    selected => selected.WaitFor<Go>("raise").Throw<BusinessError>(ctx => new("private") { Code = 73 }),
                    held => held.WaitFor<Go>("held")))
                .Finally(cleanup => cleanup.Step<Mark>(s => s.Input(x => x.Text).From(ctx => "finally"))), setup: s => s.Parallel(2));
            else t.Parallel(p => p.Do(b => b.WaitFor<Go>("raise").Throw<BusinessError>(ctx => new("private") { Code = 73 }))
                .Do(b => b.Try(child => child.WaitFor<Go>("held")).Finally(cleanup => cleanup.Step<Mark>(s => s.Input(x => x.Text).From(ctx => "finally")))));
        }).Catch<BusinessError>(c => c.Step<Mark>(s => s.Input(x => x.Text).From(ctx => "catch"))).Build();
        var client = services.GetRequiredService<IWorkflowClient>();
        var waiting = await client.RunAsync(definition, new Data()); Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);
        var finished = await client.ResumeAsync(waiting.InstanceId, "raise", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(each ? new[] { "finally", "finally", "catch" } : new[] { "finally", "catch" }, effects.Values.ToArray());
        Assert.Empty(await client.GetOpenWaitsAsync(new(InstanceId: waiting.InstanceId)));
    }

    [Fact]
    public async Task ThrowRunsSagaCompensationBeforeItsHandlerWithoutRegisteringTheThrowAsCompleted()
    {
        var effects = new Effects();
        await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.AddSingleton(effects).BuildServiceProvider();
        var definition = new WorkflowBuilder<Data, object>([], "ThrowSagaUndo").Saga(s => s
            .Step<Echo>(a => a.CompensateWith<Mark>(u => u.Input(x => x.Text).From(ctx => "undo")))
            .Throw<BusinessError>(ctx => new("private") { Code = 73 }))
            .OnError<BusinessError, CodeFault>(e => e.Step<Mark>(s => s.Input(x => x.Text).From(ctx => "handler"))
                .Compensate().ThenContinue()).Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(new[] { "undo", "handler" }, effects.Values.ToArray());
    }

    [Workflow("InvalidRethrowChild", Version = 1)]
    public sealed class InvalidChild : IWorkflow<Data> { public void Build(IWorkflowBuilder<Data> builder) => builder.Rethrow(); }
    public sealed class Effects { public ConcurrentQueue<string> Values { get; } = new(); }
    public sealed class Mark(Effects effects) : IAsyncActivity
    { [ActivityInput] public string Text { get; set; } = ""; public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) { effects.Values.Enqueue(Text); return Task.CompletedTask; } }
}

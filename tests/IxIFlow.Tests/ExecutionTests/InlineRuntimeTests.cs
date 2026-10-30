using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "F-17")]
public sealed class InlineRuntimeTests
{
    [Fact]
    public async Task NestedInputsAreCopiedAndPreviousDoesNotAliasMappedOutputs()
    {
        await using var services = Services();

        var definition = Workflow.Create<Caller>("IsolatedInputs").Use<Mutating, Callee>(u => u
                .Input(x => x.Items).From(c => c.WorkflowData.Items)
                .Output(x => x.Items).To(c => c.WorkflowData.Results))
            .Step<Mutate>(a => a.Input(x => x.Items).From(c => c.PreviousStep.Items)).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Caller());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<Caller>(result.WorkflowData);
        Assert.Equal(new[] { 1 }, data.Items);
        Assert.Equal(new[] { 1, 2 }, data.Results);
    }

    [Fact]
    public async Task NoOutputMappingsDiscardMutationsAndNestedCallsReturnFinalData()
    {
        await using var services = Services();

        var definition = Workflow.Create<Caller>("PrivateMutations").Use<Mutating, Callee>(u => u
                .Input(x => x.Items).From(c => c.WorkflowData.Items))
            .Step<Copy>(a => a.Input(x => x.Value).From(c => c.PreviousStep.Items.Count)
                .Output(x => x.Value).To(c => c.WorkflowData.Value)).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Caller());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(new[] { 1 }, Assert.IsType<Caller>(result.WorkflowData).Items);
        Assert.Equal(2, Assert.IsType<Caller>(result.WorkflowData).Value);
    }

    [Fact]
    public async Task RequiredInputsAndTypedDefaultsUseTheExactContractAndFailureIsCatchable()
    {
        await using var services = Services();

        var definition = Workflow.Create<Caller>("RequiredInline").Step<Copy>()
            .Try(b => b.Use<RequiredCallee, RequiredData>(u => u.Input(x => x.Text).From(c => c.WorkflowData.Text)))
            .Catch<WorkflowEngineException>(b => b.Step<Copy>(a => a.Input(x => x.Value).From(c => 9)
                .Output(x => x.Value).To(c => c.WorkflowData.Value))).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var failedInput = await client.RunAsync(definition, new Caller { Text = "" });
        Assert.True(failedInput.Status == WorkflowExecutionStatus.Success, failedInput.ErrorMessage);
        Assert.Equal(9, Assert.IsType<Caller>(failedInput.WorkflowData).Value);

        var defaults = Workflow.Create<Caller>("InlineDefaults").Use<RequiredCallee, RequiredData>(u => u
            .Input(x => x.Text).From(c => "valid").Output(x => x.Value).To(c => c.WorkflowData.Value)).Build();

        var success = await client.RunAsync(defaults, new Caller());
        Assert.Equal(7, Assert.IsType<Caller>(success.WorkflowData).Value);
    }

    [Fact]
    public async Task OuterCatchUsesCallerDataAndInlineFinallyUsesCalleeData()
    {
        var probe = new Probe();
        await using var services = Services(probe);

        var definition = Workflow.Create<Caller>("InlineFailure").Step<Copy>()
            .Try(b => b.Use<Failing, Callee>(u => u.Input(x => x.Value).From(c => 17)
                .Output(x => x.Value).To(c => c.WorkflowData.Value)))
            .Catch<InvalidOperationException>(b => b.Step<Copy>(a => a.Input(x => x.Value).From(c => c.WorkflowData.Value + 1)
                .Output(x => x.Value).To(c => c.WorkflowData.Value))).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Caller { Value = 3 });
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(4, Assert.IsType<Caller>(result.WorkflowData).Value);
        Assert.Equal(new[] { 17 }, probe.Values);
        var traces = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(result.InstanceId);
        Assert.Contains(traces, t => t.Entry.Payload is InlineTrace { Cause: ItemEndCause.UnwoundByOuterHandler });
    }

    [Fact]
    public async Task SagaCompensationInsideInlineUsesItsFrameBeforeOuterCatch()
    {
        var probe = new Probe();
        await using var services = Services(probe);

        var definition = Workflow.Create<Caller>("InlineCompensation").Step<Copy>()
            .Try(b => b.Use<Compensating, Callee>(u => u.Input(x => x.Value).From(c => 17)))
            .Catch<InvalidOperationException>(b => b.Step<Copy>(a => a.Input(x => x.Value).From(c => c.WorkflowData.Value + 1)
                .Output(x => x.Value).To(c => c.WorkflowData.Value))).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Caller { Value = 3 });
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(new[] { 17 }, probe.Values);
        Assert.Equal(4, Assert.IsType<Caller>(result.WorkflowData).Value);
    }

    [Fact]
    public async Task CancellationRunsInlineFinallyAndDiscardsOutputs()
    {
        var probe = new Probe();
        await using var services = Services(probe);

        var definition = Workflow.Create<Caller>("CancelInline").Use<WaitingFinally, Callee>(u => u
            .Input(x => x.Value).From(c => 12).Output(x => x.Value).To(c => c.WorkflowData.Value)).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Caller { Value = 3 });
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        var cancelled = await client.CancelAsync(idle.InstanceId, new CancellationReason { ReasonCode = "test" });
        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.Equal(new[] { 12 }, probe.Values);
        Assert.Equal(3, Assert.IsType<Caller>(cancelled.WorkflowData).Value);
    }

    [Fact]
    public async Task ParallelForEachInsideInlineAndInlineInsideBranchMergeOnlyTheirOwnNamespaces()
    {
        await using var services = Services();

        var definition = Workflow.Create<Caller>("NestedForks").Step<Copy>().Parallel(p => p
            .Do(b => b.Use<ParallelItems, Callee>(u => u.Input(x => x.Items).From(c => c.WorkflowData.Items)
                .Output(x => x.Results).To(c => c.WorkflowData.ItemResults)))
            .Do(b => b.Step<Copy>(a => a.Input(x => x.Value).From(c => 10)
                .Output(x => x.Value).To(c => c.WorkflowData.Value)))).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Caller { Items = [2, 3] });
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<Caller>(result.WorkflowData);
        Assert.Equal(10, data.Value);
        Assert.Equal(new[] { 2, 3 }, data.ItemResults.Select(x => x.Value));
    }

    [Fact]
    public async Task StateMachineInsideInlineAndInlineInsideFlowchartPreserveDataAndPreviousTypes()
    {
        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();

        var definition = Workflow.Create<Caller>("GraphInline").Flowchart(f => f.Start("call")
            .Node("call", b => b.Use<Machine, Callee>(u => u.Input(x => x.Value).From(c => 4)
                .Output(x => x.Value).To(c => c.WorkflowData.Value)))).Build();

        var idle = await client.RunAsync(definition, new Caller());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        var result = await client.ResumeAsync(idle.InstanceId, "inside", new Signal { Value = 8 });
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(8, Assert.IsType<Caller>(result.WorkflowData).Value);
        var traces = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(idle.InstanceId);
        Assert.Single(traces, t => t.Entry.EntryType == TraceEntryType.InlineEntered);
        Assert.Single(traces, t => t.Entry.Payload is InlineTrace { Cause: ItemEndCause.Completed });
    }

    internal static ServiceProvider Services(Probe? probe = null)
    {
        var services = new ServiceCollection().AddLogging();

        services.AddIxIFlow(o =>
        {
            o.Debugging = true;
            o.VerifyCheckpointParts = true;
        });

        services.AddSingleton(probe ?? new Probe());

        return services.BuildServiceProvider();
    }

    public sealed class Caller
    {
        public int Value { get; set; }
        public string Text { get; set; } = "";
        public List<int> Items { get; set; } = [1];
        public List<int> Results { get; set; } = [];
        public List<ItemResult<int>> ItemResults { get; set; } = [];
    }

    public sealed class Callee
    {
        public int Value { get; set; }
        public List<int> Items { get; set; } = [];
        public List<ItemResult<int>> Results { get; set; } = [];
    }

    public sealed class RequiredData
    {
        [WorkflowInput, Required] public string Text { get; set; } = "";
        [WorkflowInputOutput, DefaultValue(7)] public int Value { get; set; }
    }

    public sealed class Signal
    {
        public int Value { get; set; }
    }

    public sealed class Probe
    {
        public List<int> Values { get; } = [];
    }

    public sealed class Copy : IAsyncActivity
    {
        public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class Mutate : IAsyncActivity
    {
        public List<int> Items { get; set; } = [];

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            Items.Add(2);

            return Task.CompletedTask;
        }
    }

    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("inline failed");
    }

    public sealed class Record(Probe probe) : IAsyncActivity
    {
        public int Value { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            probe.Values.Add(Value);

            return Task.CompletedTask;
        }
    }

    [Workflow("Mutating")]
    public sealed class Mutating : IWorkflow<Callee>
    {
        public void Build(IWorkflowBuilder<Callee> b) => b.Step<Mutate>(a =>
            a.Input(x => x.Items).From(c => c.WorkflowData.Items).Output(x => x.Items).To(c => c.WorkflowData.Items));
    }

    [Workflow("Required")]
    public sealed class RequiredCallee : IWorkflow<RequiredData>
    {
        public void Build(IWorkflowBuilder<RequiredData> b)
        {
        }
    }

    [Workflow("Failing")]
    public sealed class Failing : IWorkflow<Callee>
    {
        public void Build(IWorkflowBuilder<Callee> b) => b.Step<Copy>().Try(x => x.Step<Fail>()).Catch<ArgumentException>(_ =>
        {
        }).Finally(x => x.Step<Record>(a => a.Input(x => x.Value).From(c => c.WorkflowData.Value)));
    }

    [Workflow("WaitingFinally")]
    public sealed class WaitingFinally : IWorkflow<Callee>
    {
        public void Build(IWorkflowBuilder<Callee> b) => b.Step<Copy>().Try(x => x.WaitFor<Signal>("inside")).Catch<ArgumentException>(_ =>
        {
        }).Finally(x => x.Step<Record>(a => a.Input(x => x.Value).From(c => c.WorkflowData.Value)));
    }

    [Workflow("Compensating")]
    public sealed class Compensating : IWorkflow<Callee>
    {
        public void Build(IWorkflowBuilder<Callee> b) => b.Step<Copy>().Saga(s => s.Step<Copy>(a => a
            .CompensateWith<Record>(c => c.Input(x => x.Value).From(c => c.WorkflowData.Value))).Step<Fail>());
    }

    [Workflow("ParallelItems")]
    public sealed class ParallelItems : IWorkflow<Callee>
    {
        public void Build(IWorkflowBuilder<Callee> b) => b.Step<Copy>().ForEach(c => c.WorkflowData.Items,
            (body, item) => body.Result(c => c.Item(item)), s => s.Parallel().Into(c => c.WorkflowData.Results));
    }

    [Workflow("Machine")]
    public sealed class Machine : IWorkflow<Callee>
    {
        public void Build(IWorkflowBuilder<Callee> b) => b.StateMachine(m => m.Start("a").State("a",
            s => s.On<Signal>("inside",
                t => t.Output(x => x.Value).To(c => c.WorkflowData.Value).When(c => c.WorkflowData.Value == 8, "end"))));
    }
}
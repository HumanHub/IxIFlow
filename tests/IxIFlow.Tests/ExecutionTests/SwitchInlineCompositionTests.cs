using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "W-03"), Trait("Spec", "F-17")]
public sealed class SwitchInlineCompositionTests
{
    [Theory]
    [InlineData(1, "case")]
    [InlineData(2, "default")]
    public async Task InlineBodyCanSuspendInsideSwitchAndReturnItsPrivateData(int route, string expected)
    {
        var definition = Workflow.Create<Data>("InlineWithSwitch")
            .Use<Routing, Data>(u => u.Input(d => d.Route).From(c => c.WorkflowData.Route)
                .Output(d => d.Text).To(c => c.WorkflowData.Text))
            .Step<Echo>(s => s.Input(a => a.Text).From(c => c.PreviousStep.Previous)
                .Output(a => a.Text).To(c => c.WorkflowData.Previous)).Build();

        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data { Route = route });
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        Assert.Equal("", Assert.IsType<Data>(idle.WorkflowData).Text);
        var result = await client.ResumeAsync(idle.InstanceId, "selected", new Signal());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(expected, data.Text);
        Assert.Equal("callee-entry", data.Previous);
        Assert.Contains(definition.StepTable, s => s.Kind == WorkflowStepType.Switch && s.ScopePath.Contains("/inline"));
        await AssertTrace(services, result.InstanceId, expected);
    }

    [Theory]
    [InlineData(1, "case")]
    [InlineData(2, "default")]
    public async Task SwitchBodyCanSuspendInsideInlineAndRestoreItsEntryPrevious(int route, string expected)
    {
        void Call(IWorkflowBuilder<Data, Echo> body) => body.Use<WaitingCall, Data>(u => u
                .Input(d => d.Route).From(c => c.WorkflowData.Route)
                .Output(d => d.Text).To(c => c.WorkflowData.Text))
            .Step<Echo>(s => s.Input(a => a.Text).From(c => c.PreviousStep.Text)
                .Output(a => a.Text).To(c => c.WorkflowData.InlinePrevious));

        var definition = Workflow.Create<Data>("SwitchWithInline")
            .Step<Echo>(s => s.Input(a => a.Text).From(c => "outer-entry"))
            .Switch(c => c.WorkflowData.Route, cases => cases.Case(1, Call).Default(Call))
            .Step<Echo>(s => s.Input(a => a.Text).From(c => c.PreviousStep.Text)
                .Output(a => a.Text).To(c => c.WorkflowData.Previous)).Build();

        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data { Route = route });
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        var result = await client.ResumeAsync(idle.InstanceId, "selected", new Signal());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(expected, data.Text);
        Assert.Equal(expected, data.InlinePrevious);
        Assert.Equal("outer-entry", data.Previous);
        Assert.Contains(definition.StepTable, s => s.Kind == WorkflowStepType.InlineWorkflow && s.ScopePath.Contains("/case/"));
        Assert.Contains(definition.StepTable, s => s.Kind == WorkflowStepType.InlineWorkflow && s.ScopePath.EndsWith("/default"));
        await AssertTrace(services, result.InstanceId, expected);
    }

    [Fact]
    public void RootSwitchRetainsTheDeclaredContractAroundInlineCalls()
    {
        var definition = Workflow.Create<Data>("ContractSwitch")
            .Input(d => d.Route).Output(d => d.Text)
            .Switch(c => c.WorkflowData.Route, cases => cases.Case(1, body => body
                .Use<WaitingCall, Data>(u => u.Input(d => d.Route).From(c => c.WorkflowData.Route)
                    .Output(d => d.Text).To(c => c.WorkflowData.Text)))).Build();

        var contract = WorkflowDefinitionValidator.GetContract(definition);
        Assert.Equal([nameof(Data.Route)], contract.Inputs.Select(m => m.Name));
        Assert.Equal([nameof(Data.Text)], contract.Outputs.Select(m => m.Name));
    }

    private static async Task AssertTrace(ServiceProvider services, string instanceId, string expected)
    {
        var trace = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(instanceId);
        Assert.Single(trace, t => t.Entry.EntryType == TraceEntryType.InlineEntered);
        Assert.Single(trace, t => t.Entry.EntryType == TraceEntryType.InlineExited);

        var selected = Assert.IsType<SwitchTrace>(Assert.Single(trace,
            t => t.Entry.EntryType == TraceEntryType.SwitchSelected).Entry.Payload);

        Assert.Equal(expected == "case" ? SwitchPath.Case : SwitchPath.Default, selected.Path);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddIxIFlow(o =>
        {
            o.Debugging = true;
            o.VerifyCheckpointParts = true;
        });

        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        public int Route { get; set; }
        public string Text { get; set; } = "";
        public string Previous { get; set; } = "";
        public string InlinePrevious { get; set; } = "";
    }

    public sealed class Signal;

    public sealed class Echo : IAsyncActivity
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Workflow("Routing")]
    public sealed class Routing : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> b) => b
            .Step<Echo>(s => s.Input(a => a.Text).From(c => "callee-entry"))
            .Switch(c => c.WorkflowData.Route, cases => cases
                .Case(1, body => body.Step<Echo>(s => s.Input(a => a.Text).From(c => "case")
                    .Output(a => a.Text).To(c => c.WorkflowData.Text)).WaitFor<Signal>("selected"))
                .Default(body => body.Step<Echo>(s => s.Input(a => a.Text).From(c => "default")
                    .Output(a => a.Text).To(c => c.WorkflowData.Text)).WaitFor<Signal>("selected")))
            .Step<Echo>(s => s.Input(a => a.Text).From(c => c.PreviousStep.Text)
                .Output(a => a.Text).To(c => c.WorkflowData.Previous));
    }

    [Workflow("WaitingCall")]
    public sealed class WaitingCall : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> b) => b
            .Step<Echo>(s => s.Input(a => a.Text).From(c => c.WorkflowData.Route == 1 ? "case" : "default")
                .Output(a => a.Text).To(c => c.WorkflowData.Text)).WaitFor<Signal>("selected");
    }
}
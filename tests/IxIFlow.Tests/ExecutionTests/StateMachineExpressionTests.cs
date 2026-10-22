using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class StateMachineTests
{
    [Fact]
    public async Task EngineExceptionInTriggerSuggestionInputReachesCatchAndFinally()
    {
        var repository = new InMemoryWorkflowStateRepository();
        await using var services = Services(repository);
        var definition = new WorkflowBuilder<Data, object>([], "StateInputEngineFailure")
            .Try(b => b.StateMachine(m => m.Start("a").State("a", s => s
                .Entry(e => e.Request<Ask>("answer"))
                .OnReply<Signal>("answer", "end")
                .On<Signal>("go", t => t.Input(x => x.Number).From(c => SuggestionInputThrows()).Otherwise("end")))))
            .Catch<WorkflowEngineException>(c => c.Step<Copy>(a => a.Input(x => x.Text).From(c => "catch")
                .Output(x => x.Text).To(c => c.WorkflowData.Exit)))
            .Finally(b => b.Step<Copy>(a => a.Input(x => x.Text).From(c => c.WorkflowData.Exit + "/finally")
                .Output(x => x.Text).To(c => c.WorkflowData.Final))).Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal("catch/finally", Assert.IsType<Data>(result.WorkflowData).Final);
        Assert.Empty(ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(result.InstanceId))!.ExecutionStateJson).Waits);
    }

    [Theory]
    [InlineData("event"), InlineData("reply")]
    public async Task EngineExceptionsInTriggerOutputAssignmentsReachCatchAndFinally(string kind)
    {
        var repository = new InMemoryWorkflowStateRepository();
        await using var services = Services(repository);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<OutputFailureData, object>([], "StateOutputEngineFailure" + kind)
            .Try(b => b.StateMachine(m => m.Start("a").State("a", s =>
            {
                if (kind == "reply") s.Entry(e => e.Request<OutputAsk>("go", a => a
                    .Output(x => x.Token).To(c => c.WorkflowData.Token)))
                    .OnReply<Signal>("go", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"));
                else s.On<Signal>("go", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"));
                s.After(TimeSpan.FromDays(1), "end");
            })))
            .Catch<WorkflowEngineException>(c => c.Step<Copy>(a => a.Input(x => x.Text).From(c => "catch")
                .Output(x => x.Text).To(c => c.WorkflowData.Marks)))
            .Finally(b => b.Step<Copy>(a => a.Input(x => x.Text).From(c => c.WorkflowData.Marks + "/finally")
                .Output(x => x.Text).To(c => c.WorkflowData.Marks))).Build();
        var idle = await client.RunAsync(definition, new OutputFailureData());
        var result = kind == "reply"
            ? await client.ReplyAsync(Assert.IsType<OutputFailureData>(idle.WorkflowData).Token, new Signal { Number = 7 })
            : await client.ResumeAsync(idle.InstanceId, "go", new Signal { Number = 7 });
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal("catch/finally", Assert.IsType<OutputFailureData>(result.WorkflowData).Marks);
        Assert.Empty(ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(idle.InstanceId))!.ExecutionStateJson).Waits);
    }

    [Fact]
    public async Task EngineExceptionInIntakeConditionRejectsTheEventAndLeavesTheStateWaiting()
    {
        var repository = new InMemoryWorkflowStateRepository();
        await using var services = Services(repository);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("StateIntakeEngineFailure").StateMachine(m => m.Start("a")
            .State("a", s => s.On<Signal>("go", t => t.Matches((value, c) => IntakeConditionThrows()).Otherwise("end"))))
            .Build();
        var idle = await client.RunAsync(definition, new Data());
        var wait = Assert.Single(await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)));
        var result = await client.ResumeAsync(idle.InstanceId, "go", new Signal());
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        Assert.Equal(EventRejection.NotMatched, Assert.IsType<EventReceipt>(result.Event).Rejection);
        Assert.Equal(wait.WaitId, Assert.Single(await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId))).WaitId);
        await client.CancelAsync(idle.InstanceId, new CancellationReason { ReasonCode = "test-complete" });
    }

    private static bool IntakeConditionThrows() =>
        throw new WorkflowEngineException(WorkflowErrorKind.PayloadUnavailable, "authored predicate");

    private static int SuggestionInputThrows() =>
        throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "authored suggestion input");

    public sealed class OutputFailureData
    {
        public string Token { get; set; } = "";
        public string Marks { get; set; } = "";
        public int Number
        {
            get => 0;
            set { if (value != 0) throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "authored output setter"); }
        }
    }

    public sealed class OutputAsk : IRequestActivity<Signal>
    {
        public string Token { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Token = context.Reply!.Token; return Task.CompletedTask; }
    }
}

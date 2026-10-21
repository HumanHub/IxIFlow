using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class StateMachineTests
{
    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task FalseGuardsKeepTheOriginalReplySuggestionAndTokenIncludingAfterRecovery(bool recover)
    {
        var repository = new ReplySuggestionStore();
        await using var services = Services(repository);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ReplySuggestionData>("StateReplySuggestion" + recover)
            .StateMachine(m => m.Start("a").State("a", s => s
                .Entry(e => e.Request<OutputAsk>("answer", a => a.Output(x => x.Token).To(c => c.WorkflowData.Token)))
                .OnReply<Signal>("answer", t => t.Input(x => x.Number).From(c => 10 / c.WorkflowData.Divisor)
                    .Output(x => x.Number).To(c => c.WorkflowData.Answer).Otherwise("end"))
                .On<Signal>("change", t => t.Input(x => x.Number).From(c => c.WorkflowData.Divisor)
                    .Output(x => x.Number).To(c => c.WorkflowData.Divisor)
                    .When(c => c.WorkflowData.Divisor > 0, "end")
                    .When(c => c.PreviousStep.Number < 0, "end")))).Build();
        var idle = await client.RunAsync(definition, new ReplySuggestionData { Divisor = 1 });
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        var token = Assert.IsType<ReplySuggestionData>(idle.WorkflowData).Token;
        var initial = await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId));
        var reply = Assert.Single(initial, w => w.Kind == WaitKind.Reply);
        var change = Assert.Single(initial, w => w.Key == "change");
        Assert.Equal(ReplyToken.Create(idle.InstanceId, reply.WaitId), token);
        var suggestion = reply.DefaultPayloadJson!.InlineText;
        Assert.Equal(10, JsonSerializer.Deserialize<Signal>(suggestion)!.Number);
        Assert.Equal(1, JsonSerializer.Deserialize<Signal>(change.DefaultPayloadJson!.InlineText)!.Number);

        repository.FailRearm = recover;
        WorkflowExecutionResult rearmed;
        if (recover)
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.ResumeAsync(idle.InstanceId, "change", new Signal()));
            Assert.Equal(WorkflowErrorKind.Infrastructure, error.Kind);
            Assert.IsType<IOException>(error.InnerException);
            Assert.True(repository.RearmInterrupted);
            var saved = ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(idle.InstanceId))!.ExecutionStateJson);
            Assert.Equal(change.WaitId, Assert.IsType<AcceptedWaitState>(saved.Continuations[0].AcceptedWait).WaitId);
            Assert.Equal(reply.WaitId, Assert.Single(saved.Waits, w => w.IsReply).Id);
            Assert.Equal(suggestion, Assert.Single(saved.Waits, w => w.IsReply).DefaultPayloadJson!.InlineText);
            repository.FailRearm = false;
            rearmed = await client.RecoverAsync(idle.InstanceId);
        }
        else rearmed = await client.ResumeAsync(idle.InstanceId, "change", new Signal());
        Assert.True(rearmed.Status == WorkflowExecutionStatus.Suspended, rearmed.ErrorMessage);
        Assert.Equal(0, Assert.IsType<ReplySuggestionData>(rearmed.WorkflowData).Divisor);
        Assert.Equal(token, Assert.IsType<ReplySuggestionData>(rearmed.WorkflowData).Token);
        var waits = await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId));
        Assert.Equal(2, waits.Count);
        var retained = Assert.Single(waits, w => w.Kind == WaitKind.Reply);
        Assert.Equal(reply.WaitId, retained.WaitId);
        Assert.Equal(token, ReplyToken.Create(idle.InstanceId, retained.WaitId));
        Assert.Equal(suggestion, retained.DefaultPayloadJson!.InlineText);
        var fresh = Assert.Single(waits, w => w.Key == "change");
        Assert.NotEqual(change.WaitId, fresh.WaitId);
        Assert.Equal(0, JsonSerializer.Deserialize<Signal>(fresh.DefaultPayloadJson!.InlineText)!.Number);

        var answered = await client.ReplyAsync(token, JsonSerializer.Deserialize<Signal>(suggestion)!);
        Assert.True(answered.Status == WorkflowExecutionStatus.Success, answered.ErrorMessage);
        Assert.Equal(EventOutcome.Consumed, answered.Event!.Outcome);
        Assert.Equal(10, Assert.IsType<ReplySuggestionData>(answered.WorkflowData).Answer);
        Assert.Empty(await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)));
        Assert.Equal(WaitCloseReason.Answered, (await repository.GetWaitAsync(idle.InstanceId, reply.WaitId))!.CloseReason);
    }

    public sealed class ReplySuggestionData
    {
        public string Token { get; set; } = "";
        public int Divisor { get; set; }
        public int Answer { get; set; }
    }

    private sealed class ReplySuggestionStore : InMemoryWorkflowStateRepository
    {
        public bool FailRearm { get; set; }
        public bool RearmInterrupted { get; private set; }

        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var instance = commit.ReadInstance(commit.ReplacesParts ? null : await GetWorkflowInstanceAsync(commit.InstanceId));
            if (FailRearm && ExecutionCheckpoint.Read(instance.ExecutionStateJson).Continuations[0].Status == ContinuationStatus.Waiting &&
                JsonSerializer.Deserialize<ReplySuggestionData>(instance.WorkflowDataJson.InlineText)!.Divisor == 0)
            {
                RearmInterrupted = true;
                throw new IOException("Interrupted reply-suggestion rearm commit");
            }
            return await base.CommitWorkflowInstanceAsync(commit);
        }
    }
}

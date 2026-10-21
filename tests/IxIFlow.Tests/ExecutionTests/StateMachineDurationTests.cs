using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class StateMachineTests
{
    [Theory]
    [InlineData(false, "ordinary"), InlineData(false, "engine"), InlineData(false, "interruption")]
    [InlineData(true, "ordinary"), InlineData(true, "engine"), InlineData(true, "interruption")]
    public async Task ThrowingAfterDuringInitialArmOrRearmRunsCatchAndFinallyAndClosesStateWaits(bool rearm, string error)
    {
        var repository = new InMemoryWorkflowStateRepository();
        await using var services = Services(repository);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "StateDurationFailure" + rearm + error)
            .Try(b => b.StateMachine(m => m.Start("a").State("a", s => s
                .Entry(e => e.Request<Ask>("answer"))
                .Exit(e => e.Step<Copy>(a => a.Input(x => x.Text).From(c => "exit")
                    .Output(x => x.Text).To(c => c.WorkflowData.Exit)))
                .OnReply<Signal>("answer", "end")
                .On<Signal>("go", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number)
                    .When(c => false, "end"))
                .After(TimeSpan.FromDays(1), "end")
                .After(c => DurationOrThrow(c.WorkflowData.Number == (rearm ? 1 : 0), error), "end"))))
            .Catch<WorkflowEngineException, GuardFault>(c => c
                .Step<Copy>(a => a.Input(x => x.Text).From(c => c.Fault.StepId)
                    .Output(x => x.Text).To(c => c.WorkflowData.Final))
                .Step<Copy>(a => a.Input(x => x.Text).From(c => "catch")
                    .Output(x => x.Text).To(c => c.WorkflowData.Exit)))
            .Catch<InvalidOperationException>(c => c.Step<Copy>(a => a.Input(x => x.Text).From(c => "catch")
                .Output(x => x.Text).To(c => c.WorkflowData.Exit)))
            .Finally(b => b.Step<Copy>(a => a.Input(x => x.Text).From(c => c.WorkflowData.Exit + "/finally")
                .Output(x => x.Text).To(c => c.WorkflowData.Action))).Build();
        var result = await client.RunAsync(definition, new Data());
        var priorWaits = new List<StoredWait>();
        if (rearm)
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            var saved = ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(result.InstanceId))!.ExecutionStateJson);
            Assert.Equal(4, saved.Waits.Count);
            foreach (var wait in saved.Waits)
                priorWaits.Add((await repository.GetWaitAsync(result.InstanceId, wait.Id))!);
            result = await client.ResumeAsync(result.InstanceId, "go", new Signal { Number = 1 });
        }
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal("catch", data.Exit);
        Assert.Equal("catch/finally", data.Action);
        if (error != "ordinary") Assert.Equal("root/0/sequence/0/state/a/on/3/0", data.Final);
        var finished = ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(result.InstanceId))!.ExecutionStateJson);
        Assert.Empty(finished.Waits);
        Assert.Empty(finished.BoundReplies);
        Assert.Empty(await repository.GetOpenWaitsAsync(new(InstanceId: result.InstanceId)));
        Assert.Empty((await repository.GetPendingEventsAsync(result.InstanceId, 0, 100)).Deadlines!);
        var reply = Assert.Single(await repository.GetOpenWaitsAsync(new(InstanceId: result.InstanceId) { IncludeClosed = true }),
            wait => wait.Kind == WaitKind.Reply);
        Assert.False(reply.IsOpen);
        Assert.Equal(WaitCloseReason.Cancelled, reply.CloseReason);
        foreach (var wait in priorWaits)
        {
            var closed = (await repository.GetWaitAsync(result.InstanceId, wait.WaitId))!;
            Assert.False(closed.IsOpen);
            Assert.Equal(wait.Key == "go" ? WaitCloseReason.Answered : WaitCloseReason.Cancelled, closed.CloseReason);
        }
    }

    [Fact]
    public void AfterContextPreparationFailureRemainsAnEngineFailure()
    {
        using var services = Services();
        var definition = Workflow.Create<Data>("StateDurationPreparation").StateMachine(m => m.Start("a")
            .State("a", s => s.After(c => DurationOrThrow(false, "engine"), "end"))).Build();
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var trigger = scopes.Step("root/0/state/a/on/0/0");
        var error = new WorkflowEngineException(WorkflowErrorKind.PayloadUnavailable, "preparation failed");
        trigger.Runtime = trigger.Runtime with { Context = _ => throw error };
        var continuation = new ContinuationState { Stack =
        [
            new() { ScopeId = "root", Graph = new() { Node = "a", Visits = 1 } },
            new() { ScopeId = "root/0/state/a", Kind = FrameKind.State, Node = "a", NextStepIndex = 1 }
        ] };
        var checkpoint = new ExecutionCheckpoint { RootData = new Data(), Continuations = [continuation] };
        checkpoint.Trace.Configure(new WorkflowInstance { InstanceId = "duration-preparation" }, checkpoint,
            ExecutionLimits.Default, null, scopes);
        Assert.Same(error, Assert.Throws<WorkflowEngineException>(() => GraphTransitions.Arm(scopes, checkpoint,
            continuation, scopes.Steps("root/0/state/a").Single(s => s.StepType == WorkflowStepType.Arm),
            services, DateTime.UtcNow, DateTime.UtcNow)));
        Assert.False(new ContinuationTransitions(services, TimeProvider.System)
            .CaptureFailure(scopes, checkpoint, continuation, error));
        Assert.Null(error.StepId);
    }

    private static TimeSpan DurationOrThrow(bool fail, string error) => !fail ? TimeSpan.FromDays(1) : throw error switch
    {
        "ordinary" => new InvalidOperationException("invalid schedule"),
        "interruption" => new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "invalid schedule"),
        _ => new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "invalid schedule")
    };
}

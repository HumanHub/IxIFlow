using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

public sealed partial class StateMachineScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task EngineExceptionInTriggerSuggestionInputReachesCatchAndFinally(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var probe = new Probe();
        await using var services = Services(store, repository, probe: probe);

        var definition = new WorkflowBuilder<Data, object>([], "StateInputEngineFailure")
            .Try(b => b.StateMachine(m => m.Start("a").State("a", s => s
                .Entry(e => e.Request<Ask>("answer", a => a.Input(x => x.Name).From(c => "answer")))
                .OnReply<Signal>("answer", "end")
                .On<Signal>("go", t => t.Input(x => x.Number).From(c => SuggestionInputThrows()).Otherwise("end")))))
            .Catch<WorkflowEngineException>(c => c.Step<Mark>(a => a.Input(x => x.Name).From(c => "catch")))
            .Finally(b => b.Step<Mark>(a => a.Input(x => x.Name).From(c => "finally"))).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(new[] { "catch", "finally" }, probe.Marks);
        Assert.Empty(ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(result.InstanceId))!.ExecutionStateJson).Waits);
        Assert.Empty(await repository.GetOpenWaitsAsync(new(InstanceId: result.InstanceId)));
    }

    public static IEnumerable<object[]> EngineOutputFailures => Cases("event", "reply");

    [Theory, MemberData(nameof(EngineOutputFailures))]
    public async Task EngineExceptionsInTriggerOutputAssignmentsReachCatchAndFinally(string profile, string kind)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var probe = new Probe();
        await using var services = Services(store, repository, probe: probe);
        var client = services.GetRequiredService<IWorkflowClient>();

        var definition = new WorkflowBuilder<OutputFailureData, object>([], "StateOutputEngineFailure" + kind)
            .Try(b => b.StateMachine(m => m.Start("a").State("a", s =>
            {
                if (kind == "reply")
                    s.Entry(e => e.Request<Ask>("go", a => a.Input(x => x.Name).From(c => "go")))
                        .OnReply<Signal>("go", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"));
                else
                    s.On<Signal>("go", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number).Otherwise("end"));

                s.After(TimeSpan.FromDays(1), "end");
            })))
            .Catch<WorkflowEngineException>(c => c.Step<Mark>(a => a.Input(x => x.Name).From(c => "catch")))
            .Finally(b => b.Step<Mark>(a => a.Input(x => x.Name).From(c => "finally"))).Build();

        var idle = await client.RunAsync(definition, new OutputFailureData());

        var result = kind == "reply"
            ? await client.ReplyAsync(probe.Tokens["go"], new Signal { Number = 7 })
            : await client.ResumeAsync(idle.InstanceId, "go", new Signal { Number = 7 });

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(new[] { "catch", "finally" }, probe.Marks);
        Assert.Empty(ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(idle.InstanceId))!.ExecutionStateJson).Waits);
        Assert.Empty(await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)));
        Assert.Empty((await repository.GetPendingEventsAsync(idle.InstanceId, 0, 100)).Deadlines!);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task EngineExceptionInIntakeConditionRejectsTheEventAndLeavesTheStateWaiting(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        await using var services = Services(store, repository);
        var client = services.GetRequiredService<IWorkflowClient>();

        var definition = Workflow.Create<Data>("StateIntakeEngineFailure").StateMachine(m => m.Start("a")
                .State("a", s => s.On<Signal>("go", t => t.Matches((value, c) => IntakeConditionThrows()).Otherwise("end"))))
            .Build();

        var idle = await client.RunAsync(definition, new Data());
        var wait = Assert.Single(await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId)));
        var result = await client.ResumeAsync(idle.InstanceId, "go", new Signal());
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        EventAssert.Rejected(result, EventRejection.NotMatched);
        Assert.Equal(wait.WaitId, Assert.Single(await repository.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId))).WaitId);
        await client.CancelAsync(idle.InstanceId, new CancellationReason { ReasonCode = "test-complete" });
    }

    private static bool IntakeConditionThrows() =>
        throw new WorkflowEngineException(WorkflowErrorKind.PayloadUnavailable, "authored predicate");

    private static int SuggestionInputThrows() =>
        throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "authored suggestion input");

    public sealed class OutputFailureData
    {
        public int Number
        {
            get => 0;
            set
            {
                if (value != 0)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "authored output setter");
            }
        }
    }
}
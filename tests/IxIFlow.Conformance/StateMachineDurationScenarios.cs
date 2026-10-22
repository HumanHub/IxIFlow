using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

public sealed partial class StateMachineScenarios
{
    public static IEnumerable<object[]> DurationFailures => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { false, true }.SelectMany(rearm => new[] { "ordinary", "engine", "interruption" }
            .Select(error => new object[] { profile, rearm, error })));

    [Theory, MemberData(nameof(DurationFailures))]
    public async Task ThrowingAfterDuringInitialArmOrRearmRunsCatchAndFinallyAndClosesStateWaits(
        string profile, bool rearm, string error)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var probe = new Probe();
        await using var services = Services(store, repository, probe: probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "StateDurationFailure" + rearm + error)
            .Try(b => b.StateMachine(m => m.Start("a").State("a", s => s
                .Entry(e => e.Request<Ask>("answer", a => a.Input(x => x.Name).From(c => "answer")))
                .Exit(e => e.Step<Mark>(a => a.Input(x => x.Name).From(c => "exit")))
                .OnReply<Signal>("answer", "end")
                .On<Signal>("go", t => t.Output(x => x.Number).To(c => c.WorkflowData.Number)
                    .When(c => false, "end"))
                .After(TimeSpan.FromDays(1), "end")
                .After(c => DurationOrThrow(c.WorkflowData.Number == (rearm ? 1 : 0), error), "end"))))
            .Catch<WorkflowEngineException, DurationFault>(c => c.Step<Mark>(a => a.Input(x => x.Name).From(c => "catch")
                .Input(x => x.Value).From(c => c.Fault.StepId)))
            .Catch<InvalidOperationException>(c => c.Step<Mark>(a => a.Input(x => x.Name).From(c => "catch")))
            .Finally(b => b.Step<Mark>(a => a.Input(x => x.Name).From(c => "finally"))).Build();
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
        Assert.Equal(new[] { "catch", "finally" }, probe.Marks);
        if (error != "ordinary") Assert.Equal("root/0/sequence/0/state/a/on/3/0", probe.Values["catch"]);
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

    public sealed class DurationFault { public string StepId { get; set; } = ""; }

    private static TimeSpan DurationOrThrow(bool fail, string error) => !fail ? TimeSpan.FromDays(1) : throw error switch
    {
        "ordinary" => new InvalidOperationException("invalid schedule"),
        "interruption" => new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "invalid schedule"),
        _ => new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "invalid schedule")
    };
}

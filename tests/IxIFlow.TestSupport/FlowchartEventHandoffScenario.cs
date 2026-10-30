using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Infrastructure;

public static class FlowchartEventHandoffScenario
{
    public static async Task VerifyAsync(IServiceProvider services, Func<string, Task> makeTimeoutDue,
        bool interrupted = false, TimeSpan? timeout = null)
    {
        // Run through scoped operations so only the explicitly chosen owners can advance the
        // instance. This forces the race without a recovery scanner or wall-clock scheduling.
        await using var scope = services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>();
        var store = services.GetRequiredService<IWorkflowStateRepository>();

        var definition = new WorkflowBuilder<Data, object>([], "FlowchartEventHandoff")
            .WaitFor<Go>("go")
            .Flowchart(f => f.Start("a").Node("a", n => n.WaitFor<Go>("answer", configure: w => w
                    .TimeoutAfter(timeout ?? TimeSpan.FromMilliseconds(100))
                    .OnTimeout(b => b.Transition("b").WithId("timeout-transition"))),
                e => e.When(c => c.PreviousStep.Name == "never", "a")).Node("b"))
            .WithId("chart").Build();

        var idle = await runner.StartAsync(definition, new Data(), new(), default);
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        DebugCommandReceipt? receipt = null;

        await runner.SetBreakpointsAsync(idle.InstanceId,
            [new("landing", "chart/a/decision", Condition: "context.PreviousStep.Name == \"never\"")],
            "set", default, accepted: value => receipt = value);

        Assert.NotNull(receipt);
        await runner.DeliverAsync(idle.InstanceId, "go", new Go(), "go-event");

        // The breakpoint wake wins the lease and also consumes the caller's queued event.
        var host = services.GetRequiredService<WorkflowHost>();

        var first = await host.WithDebugCommandResultAsync(() => receipt,
            () => runner.RecoverDebugCommandAsync(receipt, default));

        Assert.Equal(WorkflowExecutionStatus.Suspended, first.Status);
        Assert.Equal(EventOutcome.Consumed, (await runner.ReadEventAsync(idle.InstanceId, "go-event"))!.Outcome);
        var saved = (await store.GetWorkflowInstanceAsync(idle.InstanceId))!;
        await makeTimeoutDue(idle.InstanceId);

        if (interrupted)
        {
            // A crash after the event decision but before the final idle commit still needs
            // recovery, even though its event has already been consumed.
            var lease = await StoreTestSupport.LeaseAsync(store, idle.InstanceId,
                await StoreTestSupport.ClaimAsync(services));

            saved.Status = WorkflowStatus.Running;
            await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved, lease.Epoch, saved.Revision));
            await StoreTestSupport.ReleaseAsync(store, idle.InstanceId);
        }

        // The delayed event caller must observe that handoff, rather than acquire a second
        // lease for its already-decided event and consume an unrelated, now-due timeout.
        var result = await runner.TakeEventAsync(idle.InstanceId, "go-event", default);
        Assert.Equal(interrupted ? WorkflowExecutionStatus.Paused : WorkflowExecutionStatus.Suspended, result.Status);
        Assert.Equal(EventOutcome.Consumed, result.Event!.Outcome);

        if (!interrupted)
            Assert.Equal(saved.Revision, (await store.GetWorkflowInstanceAsync(idle.InstanceId))!.Revision);

        var timeoutResult = await runner.RecoverAsync(idle.InstanceId, default);
        Assert.Equal(WorkflowExecutionStatus.Paused, timeoutResult.Status);

        Assert.Contains((await services.GetRequiredService<IWorkflowOperations>().InspectAsync(idle.InstanceId))!
            .DebugState.Stop!.Diagnostics, diagnostic => diagnostic.Code == "BPC002");

        await runner.SetBreakpointsAsync(idle.InstanceId, [], "clear", default);

        await runner.RequestDebugAsync(idle.InstanceId, DebugCommandKind.Continue, "finish", default,
            accepted: value => receipt = value);

        Assert.Equal(WorkflowExecutionStatus.Success,
            (await runner.RecoverDebugCommandAsync(receipt!, default)).Status);

        var transition = definition.StepTable.Single(s => s.AuthorId == "timeout-transition").CatalogId;

        Assert.Single(await store.GetTraceAsync(idle.InstanceId),
            entry => entry.Entry.StepId == transition && entry.Entry.EntryType == TraceEntryType.StepEntered);
    }

    public sealed class Data;

    public sealed class Go
    {
        public string Name { get; set; } = "";
    }
}
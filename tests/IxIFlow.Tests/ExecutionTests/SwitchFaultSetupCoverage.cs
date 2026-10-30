using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static IxIFlow.Tests.ExecutionTests.SwitchTests;

namespace IxIFlow.Tests.ExecutionTests;

internal static class SwitchFaultSetupCoverage
{
    internal static async Task ExecuteRecoverAndDebug()
    {
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time);
        var registry = new WorkflowCatalog();

        var child = Workflow.Create<Data>("child").Step<Echo>(a =>
                a.Input(x => x.Text).From(ctx => ctx.WorkflowData.Text + "|child").Output(x => x.Text).To(ctx => ctx.WorkflowData.Text))
            .Build();

        await registry.RegisterWorkflowAsync(child);

        var definition = Workflow.Create<Data>("FaultCapabilities").Step<Seed>().Try(t => t.Step<Fail>())
            .Catch<InvalidOperationException, Fault>(caught => caught.Sequence(body => body.Delay(TimeSpan.FromSeconds(1)).WithId("delay")
                .Named("Recovery delay")
                .Request<Probe>("request", a => a.Input(x => x.Text).From(ctx => ctx.Fault.Message),
                    subjectExpression: ctx => ctx.Fault.Message).WithId("request")
                .WaitFor<Go>("request", matchesExpression: (evt, ctx) => evt.Text == ctx.PreviousStep.Text && ctx.Fault.Message == "fault",
                    configure: w => w.DefaultPayload(new Go { Text = "default" }).Input(x => x.Text).From(ctx => ctx.Fault.Message)
                        .Output(x => x.Text).To(ctx => ctx.WorkflowData.Previous)).WithId("reply")
                .Invoke<Data>("child", 1,
                    setup => setup.CancelWithParent(false).Input(x => x.Text).From(ctx => ctx.Fault.Message).Output(x => x.Text)
                        .To(ctx => ctx.WorkflowData.Text)).WithId("invoke")
                .Flowchart(g => g.Start("wait").Node("wait", b => b.WaitFor<Go>("go",
                        configure: w => w.TimeoutAfter(TimeSpan.FromSeconds(1))
                            .OnTimeout(b => b.Switch(ctx => ctx.Fault.Message, c => c.Case("fault", _ =>
                            {
                            })).WithId("timeout-switch").Transition("done"))).WithId("graph-wait"))
                    .Node("done")))).Build();

        await registry.RegisterWorkflowAsync(definition);
        Assert.False(ParallelRules.Flatten(definition.Steps).Single(s => s.Id == "invoke").CancelWithParent);
        string id;

        await using (var first = SwitchReviewFixTests.Services(repository, registry, time))
        {
            var run = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, run.Status);
            id = run.InstanceId;
            Assert.Contains(definition.StepTable, s => s.Name == "Recovery delay");
        }

        time.Advance(TimeSpan.FromSeconds(2));

        await using (var second = SwitchReviewFixTests.Services(repository, registry, time))
        {
            var client = second.GetRequiredService<IWorkflowClient>();
            var ops = second.GetRequiredService<IWorkflowOperations>();
            await ops.SetBreakpointsAsync(id, [new("request", "request")], "test");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.RecoverAsync(id)).Status);
            var before = (await ops.InspectAsync(id))!;
            Assert.Equal("request", Assert.Single(before.Threads).AuthorStepId);
            await ops.SetBreakpointsAsync(id, [], "test");

            Assert.Equal(WorkflowExecutionStatus.Paused,
                (await ops.StepAsync(id, before.Threads[0].ContinuationId, DebugStepKind.Over, "step-request")).Status);

            Assert.Equal("reply", Assert.Single((await ops.InspectAsync(id))!.Threads).AuthorStepId);
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await ops.ContinueAsync(id, "continue")).Status);
            Assert.True(ReplyToken.TryRead(Probe.Tokens[id], out _, out var waitId, out _));
            var wait = (await ((IWorkflowInbox)repository).GetWaitAsync(id, waitId))!;

            var reader = new PayloadReader(async reference => (await repository.PayloadStore.GetAsync(reference))!.Value,
                new PayloadBudget(long.MaxValue));

            Assert.Equal("fault", JsonSerializer.Deserialize<Go>(await reader.ReadAsync(wait.DefaultPayloadJson!))!.Text);
        }

        await using (var third = SwitchReviewFixTests.Services(repository, registry, time))
        {
            var client = third.GetRequiredService<IWorkflowClient>();

            // The request token carries the subject; replying through it exercises subject matching too.
            Assert.True(ReplyToken.TryRead(Probe.Tokens[id], out var instance, out _, out var subject));
            Assert.Equal(id, instance);
            Assert.Equal(ReplyToken.Digest("fault"), subject);
            var ops = third.GetRequiredService<IWorkflowOperations>();
            await ops.SetBreakpointsAsync(id, [new("graph", "graph-wait")], "test");
            var result = await client.ReplyAsync(Probe.Tokens[id], new Go { Text = "fault" });
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            var saved = (await repository.GetWorkflowInstanceAsync(id))!;

            var reader = new PayloadReader(async reference => (await repository.PayloadStore.GetAsync(reference))!.Value,
                new PayloadBudget(long.MaxValue));

            var checkpoint = ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(saved, reader));

            if (checkpoint.Waits.SingleOrDefault(w => w.ChildWorkflowId != null)?.ChildWorkflowId is { } childId)
            {
                Assert.Equal(WorkflowExecutionStatus.Success,
                    (await client.AwaitAsync(childId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10))).Status);
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            while ((await client.AwaitAsync(id, cancellationToken: deadline.Token)).Status != WorkflowExecutionStatus.Paused)
                await Task.Delay(10, deadline.Token);

            result = await client.AwaitAsync(id);
            Assert.Equal("graph-wait", Assert.Single((await ops.InspectAsync(id))!.Threads).AuthorStepId);
            Assert.Equal("fault|child", Assert.IsType<Data>(result.WorkflowData).Text);
            Assert.Equal("fault", Assert.IsType<Data>(result.WorkflowData).Previous);
            await ops.SetBreakpointsAsync(id, [], "test");
            await ops.RequestContinueAsync(id, "open-graph-wait");

            while (!(await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: id))).Any(w => w.Key == "go"))
                await Task.Delay(10, deadline.Token);

            Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.AwaitAsync(id)).Status);
        }

        time.Advance(TimeSpan.FromSeconds(2));
        await using var fourth = SwitchReviewFixTests.Services(repository, registry, time);
        var operations = fourth.GetRequiredService<IWorkflowOperations>();
        await operations.SetBreakpointsAsync(id, [new("timeout", "timeout-switch")], "test");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await fourth.GetRequiredService<IWorkflowClient>().RecoverAsync(id)).Status);
        var selected = (await operations.InspectAsync(id))!;
        Assert.Equal("timeout-switch", Assert.Single(selected.Threads).AuthorStepId);
        await operations.SetBreakpointsAsync(id, [], "test");

        Assert.Equal(WorkflowExecutionStatus.Paused,
            (await operations.StepAsync(id, selected.Threads[0].ContinuationId, DebugStepKind.Over, "step-switch")).Status);

        Assert.Equal(WorkflowExecutionStatus.Success, (await operations.ContinueAsync(id, "finish")).Status);
        Assert.Contains(await operations.GetTraceAsync(id), t => t.Entry.Payload is SwitchTrace { Path: SwitchPath.Case });
        Assert.True(Probe.Tokens.TryRemove(id, out _));
    }

    public sealed class Probe : IRequestActivity<Go>
    {
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Tokens = new();
        public string Text { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Assert.Equal("fault", Text);
            Assert.True(Tokens.TryAdd(context.WorkflowInstanceId, context.Reply!.Token));

            return Task.CompletedTask;
        }
    }
}
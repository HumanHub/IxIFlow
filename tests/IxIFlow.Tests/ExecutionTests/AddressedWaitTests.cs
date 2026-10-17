using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Every wait has its own engine-issued ID, listed with a token that answers only that wait (D-03,
/// D-14, D-33). An event addressed by key satisfies only a wait that was open when it arrived, so
/// nothing sent earlier is carried over to a later wait.
/// </summary>
[Trait("Feature", "waits")]
public sealed class AddressedWaitTests
{
    [Fact]
    [Trait("Spec", "D-14")]
    public async Task AnOpenWaitIsListedWithItsTypeSchemaDeadlineAndToken()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(ApprovalWorkflow("ListedApproval", TimeSpan.FromHours(4)), new LoopData());

        var wait = Assert.Single(await engine.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));

        Assert.Equal((WaitKind.Event, "approval"), (wait.Kind, wait.Key));
        Assert.Equal(WorkflowTypeIdentity.StableName(typeof(Approval)), wait.EventType);
        Assert.Contains("\"Approved\"", wait.EventSchema);
        Assert.NotNull(wait.DeadlineUtc);
        Assert.True(ReplyToken.TryRead(wait.Token, out var instanceId, out var waitId, out _));
        Assert.Equal((started.InstanceId, wait.WaitId), (instanceId, waitId));
        // Other instances' waits are listed by key too, and a key nobody waits for lists nothing.
        Assert.Contains(await engine.GetOpenWaitsAsync(new WaitQuery(Key: "approval")),
            item => item.WaitId == wait.WaitId);
        Assert.Empty(await engine.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId, Key: "other")));
    }

    [Fact]
    [Trait("Spec", "D-14")]
    public async Task PagesListOnlyWaitsWithoutARequestAndSkipNone()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            ApprovalWorkflow("ChildApproval", null));
        // The child's own wait is listed; its parent's wait for the child is never listed (D-17).
        await engine.RunAsync(Workflow.Create<LoopData>("ParentOfChildApproval")
            .Step<StartActivity>()
            .Invoke<LoopData>("ChildApproval", 1)
            .Build(), new LoopData());
        await services.GetRequiredService<IWorkflowStateRepository>().WaitingInstanceAsync("ChildApproval", "approval");
        await engine.RunAsync(ApprovalWorkflow("SecondApproval", null), new LoopData());

        var listed = new List<OpenWait>();
        string? after = null;
        while (await engine.GetOpenWaitsAsync(new WaitQuery(Key: "approval", AfterWaitId: after, Limit: 1)) is
               [var wait])
        {
            listed.Add(wait);
            after = wait.WaitId;
        }

        Assert.Equal(2, listed.Count);
        Assert.All(listed, wait => Assert.Equal(WaitKind.Event, wait.Kind));
        Assert.Empty(await engine.GetOpenWaitsAsync(new WaitQuery(Kind: WaitKind.Child)));
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public async Task ATokenAnswersOnlyItsWaitAndALateReplyGetsTheReason()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(LoopWorkflow(), new LoopData { Target = 2 });
        var first = Assert.Single(await engine.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));

        var answered = await engine.ReplyAsync(first.Token, new Approval { Approved = true });
        EventAssert.Consumed(answered);
        Assert.Equal(1, Data(answered).Count);

        // The next iteration waits again under a new ID; the first token cannot answer it, even when
        // the same approval is sent again under a new event ID.
        var second = Assert.Single(await engine.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));
        Assert.NotEqual(first.WaitId, second.WaitId);
        var resent = await engine.ReplyAsync(first.Token, new Approval { Approved = true });
        EventAssert.Rejected(resent, EventRejection.WaitAnswered);
        Assert.Equal(WorkflowExecutionStatus.Suspended, resent.Status);
        Assert.Equal(1, Data(resent).Count);

        var completed = await engine.ReplyAsync(second.Token, new Approval { Approved = true });
        EventAssert.Consumed(completed);
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(2, Data(completed).Count);

        // After the instance finishes, a token is still told why its wait closed.
        EventAssert.Rejected(await engine.ReplyAsync(second.Token, new Approval { Approved = true }),
            EventRejection.WaitAnswered);
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public async Task AReplyAfterTheDeadlineIsRefusedAsTimedOut()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(
            ApprovalWorkflow("ShortApproval", TimeSpan.FromMilliseconds(100)), new LoopData());
        var wait = Assert.Single(await engine.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));
        await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));

        // A screen still showing the wait gets the reason it closed, after the instance finished too.
        var late = await engine.ReplyAsync(wait.Token, new Approval { Approved = true });

        EventAssert.Rejected(late, EventRejection.WaitTimedOut);
        Assert.Equal("timeout", Data(late).Outcome);
        Assert.Empty(await engine.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public async Task ATokenForAWaitThatTimedOutWhileTheInstanceRunsOnIsRefusedAsTimedOut()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<LoopData>("TimedOutWhileAnotherWaits")
            .Step<StartActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.WaitFor<Approval>("approval", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMilliseconds(100))
                    .OnTimeout(path => path.Step<StartActivity>())))
                .Do(branch => branch.WaitFor<Approval>("final")))
            .Build();
        var started = await engine.RunAsync(definition, new LoopData());
        var expired = Assert.Single(
            await engine.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId, Key: "approval")));
        // The host's timer closes the first wait; the other branch keeps the instance waiting.
        while ((await engine.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId, Key: "approval"))).Count > 0)
            await Task.Delay(20);
        // The timeout's own pass may still be running; the reply is sent once the instance is idle again.
        await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Idle).WaitAsync(TimeSpan.FromSeconds(10));

        var late = await engine.ReplyAsync(expired.Token, new Approval { Approved = true });

        EventAssert.Rejected(late, EventRejection.WaitTimedOut);
        Assert.Equal(WorkflowExecutionStatus.Suspended, late.Status);
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public async Task AnEventSentBeforeItsWaitOpensIsNeverCarriedOverToIt()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(LoopWorkflow(), new LoopData { Target = 2 });
        await engine.ResumeAsync(started.InstanceId, "approval", new Approval { Approved = true });

        // Between iterations nothing waits for the key, so a resend is refused, not kept for later.
        var finished = await engine.ResumeAsync(started.InstanceId, "approval", new Approval { Approved = true });
        EventAssert.Consumed(finished);
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        var afterwards = await engine.ResumeAsync(started.InstanceId, "approval", new Approval { Approved = true });
        EventAssert.Closed(afterwards);
        Assert.Equal(2, Data(afterwards).Count);
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public async Task AnEventWhileNoWaitIsOpenIsRefusedAndTheNextWaitIgnoresIt()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var gate = services.GetRequiredService<StepGate>();
        var definition = Workflow.Create<LoopData>("GatedThenWaits")
            .Step<GatedActivity>()
            .WaitFor<Approval>("approval")
            .Build();
        var started = await engine.StartAsync(definition, new LoopData());
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The wait does not exist yet, so the event is refused on arrival, not kept for it.
        var early = await engine.ResumeAsync(started, "approval", new Approval { Approved = true });
        EventAssert.Rejected(early, EventRejection.NoOpenWait);
        gate.Release.TrySetResult();
        var waiting = await engine.AwaitAsync(started).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);
        Assert.Single(await engine.GetOpenWaitsAsync(new WaitQuery(InstanceId: started)));
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public async Task TextThatIsNotAReplyTokenIsRefused()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();

        await EngineAssert.FailsAsync(WorkflowErrorKind.InvalidRequest,
            () => engine.ReplyAsync("not-a-token", new Approval()));
        var unknown = ReplyToken.Create(Guid.NewGuid().ToString("N"), "wait");
        await EngineAssert.FailsAsync(WorkflowErrorKind.InvalidRequest,
            () => engine.ReplyAsync(unknown, new Approval()));
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public async Task ATokenTheInstanceNeverIssuedIsRefusedAsUnknown()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(ApprovalWorkflow("UnknownWait", null), new LoopData());

        var forged = await engine.ReplyAsync(ReplyToken.Create(started.InstanceId, Guid.NewGuid().ToString("N")),
            new Approval { Approved = true });

        EventAssert.Rejected(forged, EventRejection.UnknownWait);
        Assert.Equal(WorkflowExecutionStatus.Suspended, forged.Status);
    }

    // An event sent again under its ID, as a redelivered message would be, is answered with its first outcome and
    // changes nothing: the next iteration's wait stays open, and only a new event answers it (D-02, D-33).
    [Fact]
    [Trait("Spec", "D-02")]
    public async Task AnEventSentAgainUnderItsIdDoesNotAnswerTheNextIterationsWait()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var store = services.GetRequiredService<IWorkflowStateRepository>();
        var started = await engine.RunAsync(LoopWorkflow(), new LoopData { Target = 2 });

        var first = await engine.ResumeAsync(started.InstanceId, "approval", new Approval(), "delivery-1");
        var revision = (await store.GetWorkflowInstanceAsync(started.InstanceId))!.Revision;
        var again = await engine.ResumeAsync(started.InstanceId, "approval", new Approval(), "delivery-1");

        Assert.Equal(WorkflowExecutionStatus.Suspended, again.Status);
        Assert.Equal(first.Event, again.Event);
        Assert.Equal(1, Data(again).Count);
        Assert.Equal(revision, (await store.GetWorkflowInstanceAsync(started.InstanceId))!.Revision);
        var next = await engine.ResumeAsync(started.InstanceId, "approval", new Approval(), "delivery-2");
        Assert.Equal(WorkflowExecutionStatus.Success, next.Status);
        Assert.Equal(2, Data(next).Count);
    }

    // An event ID names one event: sending another under it is refused, whatever the instance's state.
    [Fact]
    [Trait("Spec", "D-02")]
    public async Task AnEventIdSentAgainWithDifferentContentIsRefused()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(LoopWorkflow(), new LoopData { Target = 2 });
        await engine.ResumeAsync(started.InstanceId, "approval", new Approval { Approved = true }, "delivery-1");

        await EngineAssert.FailsAsync(WorkflowErrorKind.InvalidRequest, () =>
            engine.ResumeAsync(started.InstanceId, "approval", new Approval { Approved = false }, "delivery-1"));
    }

    private static WorkflowDefinition ApprovalWorkflow(string name, TimeSpan? timeout) =>
        Workflow.Create<LoopData>(name)
            .Step<StartActivity>()
            .WaitFor<Approval>("approval", configure: wait =>
            {
                if (timeout is { } after)
                    wait.TimeoutAfter(after).OnTimeout(path => path.Step<MarkActivity>(step => step
                        .Input(activity => activity.Value).From(_ => "timeout")
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)));
            })
            .Build();

    private static WorkflowDefinition LoopWorkflow() =>
        Workflow.Create<LoopData>("ApprovalPerIteration")
            .Step<StartActivity>()
            .WhileDo(ctx => ctx.WorkflowData.Count < ctx.WorkflowData.Target, body => body
                .WaitFor<Approval>("approval")
                .Step<IncrementActivity>(step => step
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Count)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Count)))
            .Build();

    private static LoopData Data(WorkflowExecutionResult result) => Assert.IsType<LoopData>(result.WorkflowData);

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<StepGate>();
        services.AddTransient<GatedActivity>();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    public sealed class LoopData
    {
        public int Count { get; set; }
        public int Target { get; set; }
        public string Outcome { get; set; } = "";
    }

    public sealed class Approval
    {
        public bool Approved { get; set; }
    }

    public sealed class StartActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class IncrementActivity : IAsyncActivity
    {
        public int Count { get; set; }
        public int Result { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Count + 1;
            return Task.CompletedTask;
        }
    }

    public sealed class MarkActivity : IAsyncActivity
    {
        public string Value { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class StepGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class GatedActivity(StepGate gate) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken);
        }
    }
}

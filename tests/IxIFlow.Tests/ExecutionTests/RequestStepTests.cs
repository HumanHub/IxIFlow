using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// A request activity sends a token, and only an answer carrying it can satisfy the request's
/// <c>WaitFor</c> (D-16, D-33). The answer's wait opens before the activity runs, an early answer is
/// kept for the <c>WaitFor</c>, and a request and its wait are paired when the workflow is built.
/// </summary>
[Trait("Feature", "waits")]
public sealed class RequestStepTests
{
    [Fact]
    [Trait("Spec", "D-16")]
    public async Task ARequestSendsItsTokenAndItsWaitForTakesTheAnswer()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var outbox = services.GetRequiredService<Outbox>();
        var definition = Workflow.Create<OrderData>("OneApprover")
            .Step<StartActivity>()
            .Request<RequestApproval>("approval", step => step
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount))
            .WaitFor<ApprovalDecision>("approval", configure: wait => wait
                .Output(decision => decision.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();

        var started = await engine.RunAsync(definition, new OrderData { Amount = 10 });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var (token, amount) = Assert.Single(outbox.Sent);
        Assert.Equal(10, amount);

        var answered = await engine.ReplyAsync(token, new ApprovalDecision { Approved = true });

        EventAssert.Consumed(answered);
        Assert.Equal(WorkflowExecutionStatus.Success, answered.Status);
        Assert.True(Data(answered).Approved);
        // A second click is refused as already answered, even after the instance has finished.
        EventAssert.Rejected(await engine.ReplyAsync(token, new ApprovalDecision { Approved = true }),
            EventRejection.WaitAnswered);
    }

    [Fact]
    [Trait("Spec", "D-16")]
    public async Task AnAnswerThatArrivesBeforeItsWaitForIsKeptForIt()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var outbox = services.GetRequiredService<Outbox>();
        var gate = services.GetRequiredService<StepGate>();
        var definition = Workflow.Create<OrderData>("PaymentThenCheck")
            .Step<StartActivity>()
            .Request<RequestApproval>("payment", step => step
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount))
            .Step<GatedActivity>()
            .WaitFor<ApprovalDecision>("payment", configure: wait => wait
                .Output(decision => decision.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();
        var started = await engine.StartAsync(definition, new OrderData { Amount = 5 });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The answer comes while the next step runs; it is stored at once and kept for the WaitFor.
        var answer = engine.ReplyAsync(Assert.Single(outbox.Sent).Token, new ApprovalDecision { Approved = true });
        while (await services.GetRequiredService<IWorkflowStateRepository>()
                   .GetPendingEventsAsync(started, 0, 10) is { Events.Count: 0 })
            await Task.Delay(10);
        gate.Release.TrySetResult();

        var answered = await answer.WaitAsync(TimeSpan.FromSeconds(10));
        EventAssert.Consumed(answered);
        Assert.Equal(WorkflowExecutionStatus.Success, answered.Status);
        Assert.True(Data(answered).Approved);
    }

    [Fact]
    [Trait("Spec", "D-16")]
    public async Task AnAnswerTheConditionRefusesLeavesTheWaitOpen()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var outbox = services.GetRequiredService<Outbox>();
        var definition = Workflow.Create<OrderData>("PendingThenPaid")
            .Step<StartActivity>()
            .Request<RequestApproval>("payment")
            .WaitFor<ApprovalDecision>("payment", (decision, _) => decision.Status != "pending", wait => wait
                .Output(decision => decision.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();
        await engine.RunAsync(definition, new OrderData());
        var token = Assert.Single(outbox.Sent).Token;

        var pending = await engine.ReplyAsync(token, new ApprovalDecision { Status = "pending" });
        EventAssert.Rejected(pending, EventRejection.NotMatched);
        Assert.Equal(WorkflowExecutionStatus.Suspended, pending.Status);

        var paid = await engine.ReplyAsync(token, new ApprovalDecision { Status = "paid", Approved = true });
        EventAssert.Consumed(paid);
        Assert.Equal(WorkflowExecutionStatus.Success, paid.Status);
    }

    [Fact]
    [Trait("Spec", "D-16")]
    public async Task AnAnswerAboutSomethingElseIsRefused()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var outbox = services.GetRequiredService<Outbox>();
        var definition = Workflow.Create<OrderData>("ApproveThisAmount")
            .Step<StartActivity>()
            .Request<RequestApproval>("approval", step => step
                    .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount),
                subject: ctx => $"order-1:{ctx.WorkflowData.Amount}")
            .WaitFor<ApprovalDecision>("approval")
            .Build();
        var started = await engine.RunAsync(definition, new OrderData { Amount = 1000 });
        var token = Assert.Single(outbox.Sent).Token;
        Assert.True(ReplyToken.TryRead(token, out _, out var waitId, out var subject));
        Assert.Equal(ReplyToken.Digest("order-1:1000"), subject);

        // An answer that claims to confirm another amount does not satisfy the wait.
        var other = ReplyToken.Create(started.InstanceId, waitId, ReplyToken.Digest("order-1:10"));
        EventAssert.Rejected(await engine.ReplyAsync(other, new ApprovalDecision { Approved = true }),
            EventRejection.SubjectMismatch);

        EventAssert.Consumed(await engine.ReplyAsync(token, new ApprovalDecision { Approved = true }));
    }

    [Fact]
    [Trait("Spec", "D-16")]
    public async Task EachIterationSendsItsOwnTokenAndAnEarlierOneIsRefused()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var outbox = services.GetRequiredService<Outbox>();
        var definition = Workflow.Create<OrderData>("ApprovalPerLine")
            .Step<StartActivity>()
            .WhileDo(ctx => ctx.WorkflowData.Lines < 2, body => body
                .Request<RequestApproval>("line")
                .WaitFor<ApprovalDecision>("line")
                .Step<CountLineActivity>(step => step
                    .Input(activity => activity.Lines).From(ctx => ctx.WorkflowData.Lines)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Lines)))
            .Build();
        await engine.RunAsync(definition, new OrderData());
        var first = Assert.Single(outbox.Sent).Token;

        EventAssert.Consumed(await engine.ReplyAsync(first, new ApprovalDecision { Approved = true }));
        Assert.Equal(2, outbox.Sent.Count);
        var second = outbox.Sent[1].Token;
        Assert.NotEqual(first, second);
        var resent = await engine.ReplyAsync(first, new ApprovalDecision { Approved = true });
        EventAssert.Rejected(resent, EventRejection.WaitAnswered);
        Assert.Equal(1, Data(resent).Lines);

        var done = await engine.ReplyAsync(second, new ApprovalDecision { Approved = true });
        EventAssert.Consumed(done);
        Assert.Equal(2, Data(done).Lines);
    }

    [Fact]
    [Trait("Spec", "D-16")]
    public async Task AnEventByKeyCannotAnswerARequest()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<OrderData>("KeyedAnswer")
            .Step<StartActivity>()
            .Request<RequestApproval>("approval")
            .WaitFor<ApprovalDecision>("approval")
            .Build();
        var started = await engine.RunAsync(definition, new OrderData());

        var keyed = await engine.ResumeAsync(started.InstanceId, "approval", new ApprovalDecision { Approved = true });

        EventAssert.Rejected(keyed, EventRejection.NoOpenWait);
        Assert.Equal(WorkflowExecutionStatus.Suspended, keyed.Status);
    }

    [Fact]
    [Trait("Spec", "D-16")]
    public async Task AnAnswerAfterTheRequestTimedOutIsRefusedAsTimedOut()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var outbox = services.GetRequiredService<Outbox>();
        var definition = Workflow.Create<OrderData>("ShortRequest")
            .Step<StartActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch
                    .Request<RequestApproval>("approval")
                    .WaitFor<ApprovalDecision>("approval", configure: wait => wait
                        .TimeoutAfter(TimeSpan.FromMilliseconds(100))
                        .OnTimeout(path => path.Step<StartActivity>())))
                .Do(branch => branch.WaitFor<ApprovalDecision>("final")))
            .Build();
        var started = await engine.RunAsync(definition, new OrderData());
        var token = Assert.Single(outbox.Sent).Token;
        Assert.True(ReplyToken.TryRead(token, out _, out var waitId, out _));
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        while ((await repository.GetWaitAsync(started.InstanceId, waitId))!.IsOpen)
            await Task.Delay(20);

        var late = await engine.ReplyAsync(token, new ApprovalDecision { Approved = true });

        EventAssert.Rejected(late, EventRejection.WaitTimedOut);
        Assert.Equal(WorkflowExecutionStatus.Suspended, late.Status);
    }

    [Fact]
    [Trait("Spec", "D-16")]
    public void ARequestAndItsWaitForArePairedWhenTheWorkflowIsBuilt()
    {
        // No WaitFor for the request.
        EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<OrderData>("NoWait")
            .Step<StartActivity>().Request<RequestApproval>("approval").Build());
        // A WaitFor for another answer type.
        EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<OrderData>("WrongType")
            .Step<StartActivity>().Request<RequestApproval>("approval").WaitFor<OtherAnswer>("approval").Build());
        // A WaitFor outside the loop that sends the request would take another iteration's answer.
        EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<OrderData>("OutsideLoop")
            .Step<StartActivity>()
            .WhileDo(ctx => ctx.WorkflowData.Lines < 2, body => body.Request<RequestApproval>("line"))
            .WaitFor<ApprovalDecision>("line")
            .Build());
        // Two requests with one name in one body.
        EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<OrderData>("Twice")
            .Step<StartActivity>()
            .Request<RequestApproval>("approval").Request<RequestApproval>("approval")
            .WaitFor<ApprovalDecision>("approval").Build());
        // An activity that declares no answer cannot run as a request.
        EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => Workflow.Create<OrderData>("NotARequest")
            .Step<StartActivity>().Request<StartActivity>("approval"));
    }

    private static OrderData Data(WorkflowExecutionResult result) => Assert.IsType<OrderData>(result.WorkflowData);

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Outbox>();
        services.AddSingleton<StepGate>();
        services.AddTransient<RequestApproval>();
        services.AddTransient<GatedActivity>();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    public sealed class OrderData
    {
        public decimal Amount { get; set; }
        public bool Approved { get; set; }
        public int Lines { get; set; }
    }

    public sealed class ApprovalDecision
    {
        public bool Approved { get; set; }
        public string Status { get; set; } = "";
    }

    public sealed class OtherAnswer;

    /// <summary>What the request activities sent, as an email or callback would carry it.</summary>
    public sealed class Outbox
    {
        private readonly List<(string Token, decimal Amount)> _sent = [];

        public IReadOnlyList<(string Token, decimal Amount)> Sent
        {
            get { lock (_sent) return _sent.ToArray(); }
        }

        public void Send(string token, decimal amount)
        {
            lock (_sent)
                _sent.Add((token, amount));
        }
    }

    public sealed class RequestApproval(Outbox outbox) : IRequestActivity<ApprovalDecision>
    {
        public decimal Amount { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            outbox.Send(context.Reply!.Token, Amount);
            return Task.CompletedTask;
        }
    }

    public sealed class StartActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class CountLineActivity : IAsyncActivity
    {
        public int Lines { get; set; }
        public int Result { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Lines + 1;
            return Task.CompletedTask;
        }
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

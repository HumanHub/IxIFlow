using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using IxIFlow.Recipes;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// The wait and reply recipes of spec D-17, run as the samples define them in every profile (J-02).
/// Each scenario asserts what became of every event it sends.
/// </summary>
[Trait("Spec", "D-17")]
public sealed class WaitRecipeScenarios
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OneApproverAnswersOnceAndASecondClickIsRefused(string profile)
    {
        var mailer = new RecordingMailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var engine = cluster.AddHost();

        var started = await engine.RunAsync(OneApproverRecipe.Definition(TimeSpan.FromHours(4)),
            new Order { OrderId = "order-1", Amount = 10 });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var request = mailer.To("manager");
        Assert.Equal(("order-1", 10m), (request.OrderId, request.Amount));

        var approved = await engine.ReplyAsync(request.ReplyToken, new ApprovalDecision { Approved = true, By = "dana" });
        EventAssert.Consumed(approved);
        Assert.Equal(("approved", "dana"), (Data<Order>(approved).Outcome, Data<Order>(approved).ApprovedBy));

        var again = await engine.ReplyAsync(request.ReplyToken, new ApprovalDecision { Approved = true, By = "dana" });
        EventAssert.Rejected(again, EventRejection.WaitAnswered);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OneApproverEscalatesAndRefusesAClickAfterTheTimeout(string profile)
    {
        var mailer = new RecordingMailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var engine = cluster.AddHost();

        var started = await engine.RunAsync(OneApproverRecipe.Definition(TimeSpan.FromMilliseconds(200)),
            new Order { OrderId = "order-2", Amount = 10 });
        var escalated = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(Patience);
        Assert.True(Data<Order>(escalated).Escalated);

        var late = await engine.ReplyAsync(mailer.To("manager").ReplyToken, new ApprovalDecision { Approved = true });
        EventAssert.Rejected(late, EventRejection.WaitTimedOut);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task APaymentCallbackDuringTheFraudCheckIsKeptForItsWait(string profile)
    {
        var payments = new RecordingPayments();
        var fraud = new GatedFraudChecks();
        await using var cluster = await ClusterAsync(profile, payments: payments, fraud: fraud);
        var engine = cluster.AddHost();

        var started = await engine.StartAsync(PaymentRecipe.Definition(TimeSpan.FromHours(1)),
            new Payment { OrderId = "order-3", Amount = 25 });
        await fraud.Entered.Task.WaitAsync(Patience);

        // The provider calls back while the fraud check runs: first "pending", then "paid".
        var pending = engine.ReplyAsync(payments.Token, new PaymentReceived { Status = "pending" });
        var paid = engine.ReplyAsync(payments.Token, new PaymentReceived { Status = "paid" });
        await WaitUntilStoredAsync(cluster, started, 2);
        fraud.Release.TrySetResult();

        EventAssert.Rejected(await pending.WaitAsync(Patience), EventRejection.NotMatched);
        var result = await paid.WaitAsync(Patience);
        EventAssert.Consumed(result);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(("paid", false), (Data<Payment>(result).PaymentStatus, Data<Payment>(result).Cancelled));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task APendingCallbackIsRefusedAndTheWaitStaysOpenForPaid(string profile)
    {
        var payments = new RecordingPayments();
        await using var cluster = await ClusterAsync(profile, payments: payments);
        var engine = cluster.AddHost();

        var started = await engine.RunAsync(PaymentRecipe.Definition(TimeSpan.FromHours(1)),
            new Payment { OrderId = "order-4", Amount = 25 });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var pending = await engine.ReplyAsync(payments.Token, new PaymentReceived { Status = "pending" });
        EventAssert.Rejected(pending, EventRejection.NotMatched);
        Assert.Equal(WorkflowExecutionStatus.Suspended, pending.Status);
        var paid = await engine.ReplyAsync(payments.Token, new PaymentReceived { Status = "paid" });
        EventAssert.Consumed(paid);
        Assert.Equal("paid", Data<Payment>(paid).PaymentStatus);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AnyApproverKeepsWaitingAfterARejectionAndTheFirstApprovalWins(string profile)
    {
        var mailer = new RecordingMailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var engine = cluster.AddHost();
        var started = await engine.RunAsync(AnyApproverRecipe.Definition(TimeSpan.FromHours(4)),
            new Order { OrderId = "order-5", Amount = 500 });

        var rejected = await engine.ReplyAsync(mailer.To("manager").ReplyToken, new ApprovalDecision { Approved = false });
        EventAssert.Consumed(rejected);
        Assert.Equal(WorkflowExecutionStatus.Suspended, rejected.Status);

        var approved = await engine.ReplyAsync(mailer.To("director").ReplyToken, new ApprovalDecision { Approved = true });
        EventAssert.Consumed(approved);
        Assert.Equal(WorkflowExecutionStatus.Success, approved.Status);
        Assert.Equal("approved", Data<Order>(approved).Outcome);
        Assert.Equal(started.InstanceId, approved.InstanceId);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AnyApproverRefusesTheOtherApproversLateClickAsCancelled(string profile)
    {
        var mailer = new RecordingMailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var engine = cluster.AddHost();
        await engine.RunAsync(AnyApproverRecipe.Definition(TimeSpan.FromHours(4)),
            new Order { OrderId = "order-6", Amount = 500 });

        EventAssert.Consumed(await engine.ReplyAsync(mailer.To("director").ReplyToken,
            new ApprovalDecision { Approved = true }));

        var late = await engine.ReplyAsync(mailer.To("manager").ReplyToken, new ApprovalDecision { Approved = true });
        EventAssert.Rejected(late, EventRejection.WaitCancelled);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AnyApproverTakesTheRejectionPathWhenEveryWaitTimesOut(string profile)
    {
        var mailer = new RecordingMailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var engine = cluster.AddHost();

        var started = await engine.RunAsync(AnyApproverRecipe.Definition(TimeSpan.FromMilliseconds(200)),
            new Order { OrderId = "order-7", Amount = 500 });
        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(Patience);

        Assert.Equal("rejected", Data<Order>(finished).Outcome);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task EveryApproverContinuesOnlyWhenBothHaveAnswered(string profile)
    {
        var mailer = new RecordingMailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var engine = cluster.AddHost();
        await engine.RunAsync(EveryApproverRecipe.Definition(), new Order { OrderId = "order-8" });

        var first = await engine.ReplyAsync(mailer.To("director").ReplyToken, new ApprovalDecision { Approved = true });
        EventAssert.Consumed(first);
        Assert.Equal(WorkflowExecutionStatus.Suspended, first.Status);
        var second = await engine.ReplyAsync(mailer.To("manager").ReplyToken, new ApprovalDecision { Approved = false });
        EventAssert.Consumed(second);
        Assert.Equal(WorkflowExecutionStatus.Success, second.Status);
        Assert.Equal((false, true, "both answered"),
            (Data<Order>(second).ManagerApproved, Data<Order>(second).DirectorApproved, Data<Order>(second).Outcome));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AnEarlierLinesApprovalCannotApproveALaterLine(string profile)
    {
        var mailer = new RecordingMailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var engine = cluster.AddHost();
        await engine.RunAsync(ApprovalPerLineRecipe.Definition(),
            new OrderLines { OrderId = "order-9", Lines = [10, 1000] });
        var first = mailer.Sent[0];
        Assert.Equal(10, first.Amount);

        EventAssert.Consumed(await engine.ReplyAsync(first.ReplyToken, new ApprovalDecision { Approved = true }));
        var second = mailer.Sent[1];
        Assert.Equal(1000, second.Amount);

        // The $10 approval, sent again under a new event ID, cannot approve the $1000 line.
        var resent = await engine.ReplyAsync(first.ReplyToken, new ApprovalDecision { Approved = true });
        EventAssert.Rejected(resent, EventRejection.WaitAnswered);
        Assert.Equal(1, Data<OrderLines>(resent).Approved);

        var done = await engine.ReplyAsync(second.ReplyToken, new ApprovalDecision { Approved = false });
        EventAssert.Consumed(done);
        Assert.Equal((2, 1), (Data<OrderLines>(done).Next, Data<OrderLines>(done).Approved));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ATaskListAnswersAWaitAndAStaleScreenIsToldItClosed(string profile)
    {
        await using var cluster = await ClusterAsync(profile);
        var engine = cluster.AddHost();
        var started = await engine.RunAsync(ConfirmShipmentRecipe.Definition(), new Shipment { OrderId = "order-10" });

        var task = Assert.Single(await engine.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));
        Assert.Equal("ship", task.Key);
        Assert.NotNull(task.EventSchema);

        var confirmed = await engine.ReplyAsync(task.Token, new ShipmentConfirmed { TrackingNumber = "1Z999" });
        EventAssert.Consumed(confirmed);
        Assert.Equal("1Z999", Data<Shipment>(confirmed).TrackingNumber);

        var stale = await engine.ReplyAsync(task.Token, new ShipmentConfirmed { TrackingNumber = "1Z000" });
        EventAssert.Rejected(stale, EventRejection.WaitAnswered);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ANotificationCompletesAnOpenWaitAndIsRefusedBeforeIt(string profile)
    {
        var shipping = new GatedShipping();
        await using var cluster = await ClusterAsync(profile, shipping: shipping);
        var engine = cluster.AddHost();
        var started = await engine.StartAsync(DeliveryRecipe.Definition(), new Shipment { OrderId = "order-11" });
        await shipping.Entered.Task.WaitAsync(Patience);

        // The carrier reports delivery before the order waits for it: refused, not kept for later.
        var early = await engine.ResumeAsync(started, "delivered", new ParcelDelivered { At = DateTime.UtcNow });
        EventAssert.Rejected(early, EventRejection.NoOpenWait);
        shipping.Release.TrySetResult();
        Assert.Equal(WorkflowExecutionStatus.Suspended,
            (await engine.AwaitAsync(started).WaitAsync(Patience)).Status);

        var delivered = await engine.ResumeAsync(started, "delivered", new ParcelDelivered { At = DateTime.UtcNow });
        EventAssert.Consumed(delivered);
        Assert.True(Data<Shipment>(delivered).Delivered);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AFraudWorkflowCancelsTheOrderAndALateClickIsRefusedAsCancelled(string profile)
    {
        var mailer = new RecordingMailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var engine = cluster.AddHost();
        var order = await engine.RunAsync(OneApproverRecipe.Definition(TimeSpan.FromHours(4)),
            new Order { OrderId = "order-12", Amount = 900 });

        var fraud = await engine.RunAsync(FraudRecipe.Definition(),
            new FraudCase { OrderInstanceId = order.InstanceId, Reason = "stolen card" });
        Assert.Equal(WorkflowExecutionStatus.Success, fraud.Status);
        Assert.Equal(WorkflowStatus.Cancelled, (await cluster.GetInstanceAsync(order.InstanceId))!.Status);

        var late = await engine.ReplyAsync(mailer.To("manager").ReplyToken, new ApprovalDecision { Approved = true });
        EventAssert.Rejected(late, EventRejection.WaitCancelled);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AChildsRequestIsAnsweredOnTheChildAndTheParentContinues(string profile)
    {
        var mailer = new RecordingMailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var engine = cluster.AddHost();
        await cluster.Registry.RegisterWorkflowAsync(ChildApprovalRecipe.Child());
        var parent = await engine.RunAsync(ChildApprovalRecipe.Parent(), new Order { OrderId = "order-13", Amount = 70 });
        Assert.Equal(WorkflowExecutionStatus.Suspended, parent.Status);
        var request = mailer.To("purchasing");
        Assert.True(ReplyToken.TryRead(request.ReplyToken, out var childId, out _, out _));
        Assert.NotEqual(parent.InstanceId, childId);

        var answered = await engine.ReplyAsync(request.ReplyToken, new ApprovalDecision { Approved = true });
        EventAssert.Consumed(answered);
        Assert.Equal(childId, answered.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, answered.Status);

        await cluster.RecoverOnceAsync();
        var finished = await engine.AwaitAsync(parent.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(Patience);
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.True(Data<Order>(finished).Approved);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ACallbackReceivedBeforeTheDeadlineBeatsItWhenTheOwnerWasBusy(string profile)
    {
        var payments = new RecordingPayments();
        var fraud = new GatedFraudChecks();
        await using var cluster = await ClusterAsync(profile, payments: payments, fraud: fraud);
        var engine = cluster.AddHost();
        var started = await engine.StartAsync(PaymentRecipe.Definition(TimeSpan.FromMilliseconds(500)),
            new Payment { OrderId = "order-14", Amount = 40 });
        await fraud.Entered.Task.WaitAsync(Patience);

        // The callback arrives in time; the fraud check then runs well past the payment's deadline.
        var paid = engine.ReplyAsync(payments.Token, new PaymentReceived { Status = "paid" });
        await WaitUntilStoredAsync(cluster, started, 1);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        fraud.Release.TrySetResult();

        var result = await paid.WaitAsync(Patience);
        EventAssert.Consumed(result);
        Assert.Equal(("paid", false), (Data<Payment>(result).PaymentStatus, Data<Payment>(result).Cancelled));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AnAnswerReachesTheInstanceOnAHostThatDidNotSendTheRequest(string profile)
    {
        var mailer = new RecordingMailer();
        await using var cluster = await ClusterAsync(profile, mailer);
        var sender = cluster.AddHost();
        var other = cluster.AddHost();
        var definition = OneApproverRecipe.Definition(TimeSpan.FromHours(4));
        await cluster.Registry.RegisterWorkflowAsync(definition);

        // The host that sent the request is gone; another host takes the answer.
        var started = await sender.RunAsync(definition, new Order { OrderId = "order-15", Amount = 30 });
        var answered = await other.ReplyAsync(mailer.To("manager").ReplyToken,
            new ApprovalDecision { Approved = true, By = "lee" });

        EventAssert.Consumed(answered);
        Assert.Equal(started.InstanceId, answered.InstanceId);
        Assert.Equal("approved", Data<Order>(answered).Outcome);
        Assert.Single(mailer.Sent);
    }

    private static async Task<ConformanceCluster> ClusterAsync(string profile, RecordingMailer? mailer = null,
        RecordingPayments? payments = null, GatedFraudChecks? fraud = null, GatedShipping? shipping = null) =>
        await ConformanceCluster.CreateAsync(profile, services =>
        {
            services.AddWaitRecipes();
            services.AddSingleton<IApprovalMailer>(mailer ?? new RecordingMailer());
            services.AddSingleton<IPaymentProvider>(payments ?? new RecordingPayments());
            services.AddSingleton<IFraudChecks>(fraud ?? GatedFraudChecks.Open());
            services.AddSingleton<IShipping>(shipping ?? GatedShipping.Open());
        });

    // The owner is busy, so an event sent now is stored and waits for its next pass.
    private static async Task WaitUntilStoredAsync(ConformanceCluster cluster, string instanceId, int count)
    {
        var inbox = cluster.NewRepository();
        var deadline = DateTime.UtcNow + Patience;
        while ((await inbox.GetPendingEventsAsync(instanceId, 0, 10)).Events.Count < count)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Fewer than {count} events were stored for '{instanceId}'");
            await Task.Delay(10);
        }
    }

    private static T Data<T>(WorkflowExecutionResult result) => Assert.IsType<T>(result.WorkflowData);

    private sealed class RecordingMailer : IApprovalMailer
    {
        private readonly List<ApprovalRequest> _sent = [];

        public IReadOnlyList<ApprovalRequest> Sent
        {
            get { lock (_sent) return _sent.ToArray(); }
        }

        public ApprovalRequest To(string approver) => Assert.Single(Sent, request => request.Approver == approver);

        public Task SendAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            lock (_sent)
                _sent.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPayments : IPaymentProvider
    {
        private string? _token;

        public string Token => _token ?? throw new InvalidOperationException("No payment was created");

        public Task<string> CreatePaymentAsync(decimal amount, string replyToken, CancellationToken cancellationToken)
        {
            _token = replyToken;
            return Task.FromResult("pay-1");
        }
    }

    private sealed class GatedFraudChecks : IFraudChecks
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static GatedFraudChecks Open()
        {
            var checks = new GatedFraudChecks();
            checks.Release.SetResult();
            return checks;
        }

        public async Task<bool> IsSuspiciousAsync(string orderId, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return false;
        }
    }

    private sealed class GatedShipping : IShipping
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static GatedShipping Open()
        {
            var shipping = new GatedShipping();
            shipping.Release.SetResult();
            return shipping;
        }

        public async Task<string> ShipAsync(string orderId, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return $"parcel-{orderId}";
        }
    }
}

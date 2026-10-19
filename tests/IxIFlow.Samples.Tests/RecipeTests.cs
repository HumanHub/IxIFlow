using System.Collections.Concurrent;
using IxIFlow.Core;
using IxIFlow.Recipes;
using IxIFlow.Samples;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Samples.Tests;

public sealed class RecipeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneApproverEitherAnswersOnceOrTimesOut(bool timeout)
    {
        var time = new FakeTimeProvider();
        var gateway = new Gateway();
        await using var services = Services(gateway, time);
        var client = SampleTests.Client(services);
        var started = await client.RunAsync(OneApproverRecipe.Definition(TimeSpan.FromHours(1)), new Order { OrderId = "order" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var request = Assert.Single(gateway.Approvals);
        WorkflowExecutionResult result;
        if (timeout)
        {
            time.Advance(TimeSpan.FromHours(1));
            result = await client.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(Assert.IsType<Order>(result.WorkflowData).Escalated);
            Assert.Equal(EventRejection.WaitTimedOut, (await client.ReplyAsync(request.ReplyToken, new ApprovalDecision())).Event!.Rejection);
        }
        else
        {
            result = await client.ReplyAsync(request.ReplyToken, new ApprovalDecision { Approved = true, By = "manager" });
            Assert.True(Assert.IsType<Order>(result.WorkflowData).Approved);
            Assert.Equal(EventRejection.WaitAnswered, (await client.ReplyAsync(request.ReplyToken, new ApprovalDecision())).Event!.Rejection);
        }
        if (!timeout)
            result = await client.ResumeAsync(started.InstanceId, "dispatched", new OrderDispatched());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(timeout ? "" : "approved", Assert.IsType<Order>(result.WorkflowData).Outcome);
    }

    [Fact]
    public async Task PaymentRejectsPendingAndAcceptsPaid()
    {
        var gateway = new Gateway();
        await using var services = Services(gateway);
        var client = SampleTests.Client(services);
        await client.RunAsync(PaymentRecipe.Definition(TimeSpan.FromHours(1)), new Payment { OrderId = "order", Amount = 40 });
        var token = Assert.Single(gateway.Payments);
        Assert.Equal(EventRejection.NotMatched, (await client.ReplyAsync(token, new PaymentReceived { Status = "pending" })).Event!.Rejection);
        var paid = await client.ReplyAsync(token, new PaymentReceived { Status = "paid" });
        Assert.True(paid.Status == WorkflowExecutionStatus.Success, paid.ErrorMessage);
        Assert.Equal("paid", Assert.IsType<Payment>(paid.WorkflowData).PaymentStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelApproversJoinOnAnyApprovalOrEveryAnswer(bool every)
    {
        var gateway = new Gateway();
        await using var services = Services(gateway);
        var client = SampleTests.Client(services);
        await client.RunAsync(every ? EveryApproverRecipe.Definition() : AnyApproverRecipe.Definition(TimeSpan.FromHours(1)), new Order());
        Assert.Equal(2, gateway.Approvals.Count);
        var first = gateway.Approvals.Single(request => request.Approver == "manager");
        var other = gateway.Approvals.Single(request => request.Approver == "director");
        var result = await client.ReplyAsync(first.ReplyToken, new ApprovalDecision { Approved = true });
        if (every)
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            result = await client.ReplyAsync(other.ReplyToken, new ApprovalDecision { Approved = true });
            Assert.Equal("both answered", Assert.IsType<Order>(result.WorkflowData).Outcome);
        }
        else
        {
            Assert.Equal("approved", Assert.IsType<Order>(result.WorkflowData).Outcome);
            Assert.Equal(EventRejection.WaitCancelled, (await client.ReplyAsync(other.ReplyToken, new ApprovalDecision())).Event!.Rejection);
        }
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task EachLoopIterationHasItsOwnReplyToken()
    {
        var gateway = new Gateway();
        await using var services = Services(gateway);
        var client = SampleTests.Client(services);
        await client.RunAsync(ApprovalPerLineRecipe.Definition(), new OrderLines { Lines = [10m, 20m] });
        var first = Assert.Single(gateway.Approvals).ReplyToken;
        await client.ReplyAsync(first, new ApprovalDecision { Approved = true });
        Assert.Equal(2, gateway.Approvals.Count);
        var second = gateway.Approvals.Last().ReplyToken;
        Assert.NotEqual(first, second);
        Assert.Equal(EventRejection.WaitAnswered, (await client.ReplyAsync(first, new ApprovalDecision())).Event!.Rejection);
        var end = await client.ReplyAsync(second, new ApprovalDecision { Approved = true });
        Assert.True(end.Status == WorkflowExecutionStatus.Success, end.ErrorMessage);
        Assert.Equal(2, Assert.IsType<OrderLines>(end.WorkflowData).Approved);
    }

    [Fact]
    public async Task TaskInboxFindsATokenAndCarrierPublishesByKey()
    {
        var gateway = new Gateway();
        await using var services = Services(gateway);
        var client = SampleTests.Client(services);
        var start = await client.RunAsync(ConfirmShipmentRecipe.Definition(), new Shipment());
        var wait = Assert.Single(await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: start.InstanceId)));
        var confirmed = await client.ReplyAsync(wait.Token, new ShipmentConfirmed { TrackingNumber = "track-1" });
        Assert.True(confirmed.Status == WorkflowExecutionStatus.Success, confirmed.ErrorMessage);
        Assert.Equal("track-1", Assert.IsType<Shipment>(confirmed.WorkflowData).TrackingNumber);
        var delivery = await client.RunAsync(DeliveryRecipe.Definition(), new Shipment { OrderId = "order" });
        var sent = await client.PublishAsync("delivered", new ParcelDelivered(), "carrier-1");
        Assert.Single(sent);
        var end = await client.AwaitAsync(delivery.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(end.Status == WorkflowExecutionStatus.Success, end.ErrorMessage);
        var data = Assert.IsType<Shipment>(end.WorkflowData);
        Assert.True(data.Delivered);
        Assert.Equal("parcel-order", data.Parcel);
    }

    [Fact]
    public async Task FraudCancelsAnOrderAndClosesItsWait()
    {
        var gateway = new Gateway();
        await using var services = Services(gateway);
        var client = SampleTests.Client(services);
        var order = await client.RunAsync(OneApproverRecipe.Definition(TimeSpan.FromHours(1)), new Order());
        var token = Assert.Single(gateway.Approvals).ReplyToken;
        var fraud = await client.RunAsync(FraudRecipe.Definition(), new FraudCase { OrderInstanceId = order.InstanceId, Reason = "Flagged" });
        Assert.True(fraud.Status == WorkflowExecutionStatus.Success, fraud.ErrorMessage);
        var cancelled = await client.AwaitAsync(order.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.Equal(EventRejection.WaitCancelled, (await client.ReplyAsync(token, new ApprovalDecision())).Event!.Rejection);
    }

    [Fact]
    public async Task ChildApprovalRoutesItsReplyAndReturnsItsOutput()
    {
        var gateway = new Gateway();
        await using var services = Services(gateway);
        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(ChildApprovalRecipe.Child());
        var client = SampleTests.Client(services);
        var start = await client.RunAsync(ChildApprovalRecipe.Parent(), new Order { OrderId = "order", Amount = 40 });
        await gateway.WaitForApprovalsAsync(1);
        await client.ReplyAsync(Assert.Single(gateway.Approvals).ReplyToken, new ApprovalDecision { Approved = true });
        var end = await client.AwaitAsync(start.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(end.Status == WorkflowExecutionStatus.Success, end.ErrorMessage);
        Assert.True(Assert.IsType<Order>(end.WorkflowData).Approved);
    }

    [Fact]
    public async Task SequentialAndNestedListsKeepTheirResults()
    {
        await using var services = Services(new Gateway());
        var client = SampleTests.Client(services);
        var basket = await client.RunAsync(PriceLinesRecipe.Definition(), new Basket
        {
            Lines = [new BasketLine { Quantity = 2, UnitPrice = 5 }, new BasketLine { Quantity = 3, UnitPrice = 2 }]
        });
        Assert.True(basket.Status == WorkflowExecutionStatus.Success, basket.ErrorMessage);
        var data = Assert.IsType<Basket>(basket.WorkflowData);
        Assert.Equal(16m, data.Total);
        Assert.Equal(new[] { 10m, 6m }, data.LineTotals.Select(result => result.Value));
        var invoices = await client.RunAsync(InvoiceCustomersRecipe.Definition(), new Invoicing
        {
            Customers = [new Customer { Name = "Ada", Lines = [new BasketLine { Sku = "pen" }] }]
        });
        Assert.True(invoices.Status == WorkflowExecutionStatus.Success, invoices.ErrorMessage);
        Assert.Equal(["Ada #1: pen"], Assert.IsType<Invoicing>(invoices.WorkflowData).InvoiceLines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalListsKeepOrderAndRespectTheirConcurrencyCap(bool every)
    {
        var gateway = new Gateway();
        await using var services = Services(gateway);
        var client = SampleTests.Client(services);
        await client.RunAsync(every ? EveryApproverFromListRecipe.Definition(1) : AnyApproverFromListRecipe.Definition(),
            new ApprovalRound { Approvers = ["manager", "director"] });
        Assert.Equal(every ? 1 : 2, gateway.Approvals.Count);
        var first = gateway.Approvals.First().ReplyToken;
        var result = await client.ReplyAsync(first, new ApprovalDecision { Approved = true, By = "manager" });
        if (every)
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            Assert.Equal(2, gateway.Approvals.Count);
            result = await client.ReplyAsync(gateway.Approvals.Last().ReplyToken, new ApprovalDecision { Approved = false, By = "director" });
        }
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var decisions = Assert.IsType<ApprovalRound>(result.WorkflowData).Decisions;
        Assert.Equal(2, decisions.Count);
        Assert.Equal("manager", decisions[0].Value!.By);
        Assert.Equal(every ? ItemStatus.Completed : ItemStatus.Cancelled, decisions[1].Status);
    }

    [Fact]
    public async Task CappedChildListStartsTheNextChildOnlyAfterTheFirstFinishes()
    {
        var gateway = new Gateway();
        await using var services = Services(gateway);
        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(ChargeCustomersRecipe.Child());
        var client = SampleTests.Client(services);
        var start = await client.RunAsync(ChargeCustomersRecipe.Parent(1), new Billing { Customers = ["Ada", "Grace"] });
        await gateway.WaitForApprovalsAsync(1);
        Assert.Single(gateway.Approvals);
        await client.ReplyAsync(gateway.Approvals.First().ReplyToken, new ApprovalDecision { Approved = true });
        await gateway.WaitForApprovalsAsync(2);
        Assert.Equal(2, gateway.Approvals.Count);
        await client.ReplyAsync(gateway.Approvals.Last().ReplyToken, new ApprovalDecision { Approved = false });
        var end = await client.AwaitAsync(start.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(end.Status == WorkflowExecutionStatus.Success, end.ErrorMessage);
        Assert.Equal(new[] { true, false }, Assert.IsType<Billing>(end.WorkflowData).Paid.Select(result => result.Value));
    }

    [Fact]
    public async Task SagaRefundsBeforeRetryAndGivesTheNewPaymentANewToken()
    {
        var time = new TimerRecordingClock();
        var gateway = new Gateway { StockFailures = 1 };
        await using var services = Services(gateway, time);
        var client = SampleTests.Client(services);
        var running = client.RunAsync(SagaPaymentRecipe.Definition(), new Payment { OrderId = "order", Amount = 40 });
        await time.WaitForTimerDueAtAsync(time.GetUtcNow() + TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        var start = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Suspended, start.Status);
        Assert.Equal(["pay-1"], gateway.Refunds.ToArray());
        Assert.Equal(2, gateway.Payments.Count);
        var tokens = gateway.Payments.ToArray();
        Assert.NotEqual(tokens[0], tokens[1]);
        Assert.Equal(EventRejection.WaitCancelled, (await client.ReplyAsync(tokens[0], new PaymentReceived { Status = "paid" })).Event!.Rejection);
        var end = await client.ReplyAsync(tokens[1], new PaymentReceived { Status = "paid" });
        Assert.True(end.Status == WorkflowExecutionStatus.Success, end.ErrorMessage);
        Assert.Equal("pay-2", Assert.IsType<Payment>(end.WorkflowData).PaymentReference);
    }

    private static ServiceProvider Services(Gateway gateway, TimeProvider? time = null)
    {
        var services = Hosting.Services();
        services.AddSingleton(time ?? new FakeTimeProvider());
        services.AddWaitRecipes();
        services.AddSingleton<IApprovalMailer>(gateway);
        services.AddSingleton<IPaymentProvider>(gateway);
        services.AddSingleton<IPaymentRefunds>(gateway);
        services.AddSingleton<IStock>(gateway);
        services.AddSingleton<IFraudChecks>(gateway);
        services.AddSingleton<IShipping>(gateway);
        return services.BuildServiceProvider();
    }

    private sealed class Gateway : IApprovalMailer, IPaymentProvider, IPaymentRefunds, IStock, IFraudChecks, IShipping
    {
        public ConcurrentQueue<ApprovalRequest> Approvals { get; } = new();
        public ConcurrentQueue<string> Payments { get; } = new();
        public ConcurrentQueue<string> Refunds { get; } = new();
        public int StockFailures { get; set; }
        private readonly SemaphoreSlim _sent = new(0);

        public async Task WaitForApprovalsAsync(int count)
        {
            while (Approvals.Count < count)
                await _sent.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        public Task SendAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Approvals.Enqueue(request);
            _sent.Release();
            return Task.CompletedTask;
        }

        public Task<string> CreatePaymentAsync(decimal amount, string replyToken, CancellationToken cancellationToken)
        {
            Payments.Enqueue(replyToken);
            return Task.FromResult("pay-" + Payments.Count);
        }

        public Task RefundAsync(string reference, CancellationToken cancellationToken)
        {
            Refunds.Enqueue(reference);
            return Task.CompletedTask;
        }

        public Task ReserveAsync(string orderId, CancellationToken cancellationToken) =>
            StockFailures-- > 0 ? throw new StockUnavailable("No stock") : Task.CompletedTask;

        public Task<bool> IsSuspiciousAsync(string orderId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<string> ShipAsync(string orderId, CancellationToken cancellationToken) => Task.FromResult("parcel-" + orderId);
    }
}

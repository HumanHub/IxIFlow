using System.Collections.Concurrent;
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Recipes;
using IxIFlow.Samples;
using IxIFlow.Samples.Generated;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Samples.Tests;

public sealed class YamlSampleTests
{
    internal static readonly IReadOnlyDictionary<string, Func<WorkflowDefinition>> YamlDefinitions =
        new Dictionary<string, Func<WorkflowDefinition>>
        {
            ["restock"] = Workflow_restock_workflow.Build,
            ["all"] = Workflow_parallel_quotes_workflow.Build,
            ["any"] = Workflow_parallel_quotes_any_workflow.Build,
            ["conditional"] = Workflow_parallel_quotes_conditional_workflow.Build,
            ["failure"] = Workflow_failed_quote_workflow.Build,
            ["retry"] = Workflow_retry_quote_workflow.Build,
            ["reminder"] = Workflow_reminder_workflow.Build,
            ["approval"] = Workflow_approval_workflow.Build,
            ["payment"] = Workflow_saga_payment_workflow.Build
        };

    [Theory]
    [InlineData("restock", 0)]
    [InlineData("restock", 5)]
    [InlineData("all", 0)]
    [InlineData("any", 0)]
    [InlineData("conditional", 0)]
    [InlineData("failure", 0)]
    [InlineData("retry", 0)]
    [InlineData("reminder", 0)]
    public async Task YamlAndFluentHaveTheSameSavedFingerprintAndResult(string sample, int initial)
    {
        var fluent = sample switch
        {
            "restock" => Restock.Definition(),
            "all" or "any" or "conditional" => ParallelQuotes.Definition(sample),
            "failure" => FailedQuote.Definition(),
            "retry" => RetryQuote.Definition(),
            "reminder" => Reminder.Definition(),
            _ => throw new ArgumentException("Unknown sample", nameof(sample))
        };
        var first = await Execute(fluent);
        var second = await Execute(YamlDefinitions[sample]());
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(JsonSerializer.Serialize(first.Data), JsonSerializer.Serialize(second.Data));

        async Task<(object Data, string Fingerprint)> Execute(WorkflowDefinition definition)
        {
            var time = new FakeTimeProvider();
            await using var services = SampleTests.Services(time);
            var client = SampleTests.Client(services);
            object data = sample switch
            {
                "restock" => new StockData { Count = initial },
                "failure" => new FailureData(),
                "retry" => new RetryData(),
                "reminder" => new ReminderData(),
                _ => new QuoteData()
            };
            var result = await client.RunAsync(definition, data);
            if (sample == "reminder")
            {
                Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
                time.Advance(TimeSpan.FromMinutes(59));
                Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.AwaitAsync(result.InstanceId)).Status);
                time.Advance(TimeSpan.FromMinutes(1));
                result = await client.AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.True(result.IsSuccess, result.ErrorMessage);
            switch (result.WorkflowData)
            {
                case StockData stock:
                    Assert.Equal(initial == 0 ? 3 : 6, stock.Count);
                    break;
                case QuoteData quote:
                    if (sample == "all") Assert.Equal(17.5m, quote.Amount);
                    else Assert.Contains(quote.Amount, new[] { 12.5m, 15m });
                    break;
                case FailureData failure:
                    Assert.Equal("Quote expired", failure.Message);
                    Assert.True(failure.Audited);
                    break;
                case RetryData retry:
                    Assert.Equal(2, retry.Attempt);
                    Assert.Equal("Primary unavailable", retry.EarlierError);
                    Assert.Equal("primary endpoint", retry.EarlierNote);
                    Assert.NotEmpty(retry.InvocationId);
                    Assert.NotEmpty(retry.AttemptId);
                    Assert.NotEmpty(retry.EarlierAttemptId);
                    Assert.NotEqual(retry.EarlierAttemptId, retry.AttemptId);
                    // Runtime IDs differ between instances; compare their roles after checking their values.
                    retry.InvocationId = "invocation";
                    retry.EarlierAttemptId = "first attempt";
                    retry.AttemptId = "second attempt";
                    break;
                case ReminderData reminder:
                    Assert.Equal(time.GetUtcNow(), reminder.SentAt);
                    break;
            }
            return (result.WorkflowData!, await Fingerprint(services, result.InstanceId));
        }
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("timeout")]
    public async Task RequestReplyAndTimeoutMatchFluent(string answer)
    {
        var first = await Execute(OneApproverRecipe.Definition(TimeSpan.FromHours(1)));
        var second = await Execute(YamlDefinitions["approval"]());
        Assert.Equal(first, second);

        async Task<(string Data, string Fingerprint)> Execute(WorkflowDefinition definition)
        {
            var time = new FakeTimeProvider();
            var gateway = new Gateway();
            await using var services = Services(gateway, time);
            var client = SampleTests.Client(services);
            var start = await client.RunAsync(definition, new Order { OrderId = "order-1", Amount = 40m });
            Assert.Equal(WorkflowExecutionStatus.Suspended, start.Status);
            var request = Assert.Single(gateway.Approvals);
            Assert.Equal("manager", request.Approver);
            Assert.Equal("order-1", request.OrderId);
            Assert.Equal(40m, request.Amount);
            Assert.True(ReplyToken.TryRead(request.ReplyToken, out _, out _, out var digest));
            Assert.Equal(ReplyToken.Digest("order-1:40"), digest);
            WorkflowExecutionResult result;
            if (answer == "timeout")
            {
                time.Advance(TimeSpan.FromMinutes(59));
                Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.AwaitAsync(start.InstanceId)).Status);
                time.Advance(TimeSpan.FromMinutes(1));
                result = await client.AwaitAsync(start.InstanceId, WorkflowAwaitCondition.Finished)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(EventRejection.WaitTimedOut,
                    (await client.ReplyAsync(request.ReplyToken, new ApprovalDecision())).Event!.Rejection);
            }
            else
            {
                result = await client.ReplyAsync(request.ReplyToken,
                    new ApprovalDecision { Approved = answer == "approve", By = "manager" });
                Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
                Assert.Equal(EventRejection.WaitAnswered,
                    (await client.ReplyAsync(request.ReplyToken, new ApprovalDecision())).Event!.Rejection);
                result = await client.ResumeAsync(start.InstanceId, "dispatched", new OrderDispatched());
            }
            Assert.True(result.IsSuccess, result.ErrorMessage);
            var order = Assert.IsType<Order>(result.WorkflowData);
            Assert.Equal(answer == "approve", order.Approved);
            Assert.Equal(answer == "timeout", order.Escalated);
            Assert.Equal(answer == "timeout" ? "" : answer == "approve" ? "approved" : "rejected", order.Outcome);
            Assert.Equal(answer == "timeout" ? "" : "manager", order.ApprovedBy);
            return (JsonSerializer.Serialize(order), await Fingerprint(services, result.InstanceId));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SagaCompensationAndRetryMatchFluent(bool stockFails)
    {
        var first = await Execute(SagaPaymentRecipe.Definition());
        var second = await Execute(YamlDefinitions["payment"]());
        Assert.Equal(first, second);

        async Task<(string Data, string Fingerprint)> Execute(WorkflowDefinition definition)
        {
            var gateway = new Gateway { FailStock = stockFails };
            await using var services = Services(gateway, new FakeTimeProvider());
            var client = SampleTests.Client(services);
            var start = await client.RunAsync(definition, new Payment { OrderId = "order-1", Amount = 40m });
            Assert.Equal(WorkflowExecutionStatus.Suspended, start.Status);
            var tokens = gateway.Payments.ToArray();
            Assert.Equal(stockFails ? 2 : 1, tokens.Length);
            Assert.Equal(stockFails ? ["pay-1"] : Array.Empty<string>(), gateway.Refunds.ToArray());
            if (stockFails)
            {
                Assert.NotEqual(tokens[0], tokens[1]);
                Assert.Equal(EventRejection.WaitCancelled,
                    (await client.ReplyAsync(tokens[0], new PaymentReceived { Status = "paid" })).Event!.Rejection);
            }
            Assert.Equal(EventRejection.NotMatched,
                (await client.ReplyAsync(tokens[^1], new PaymentReceived { Status = "pending" })).Event!.Rejection);
            var result = await client.ReplyAsync(tokens[^1], new PaymentReceived { Status = "paid" });
            Assert.True(result.IsSuccess, result.ErrorMessage);
            var payment = Assert.IsType<Payment>(result.WorkflowData);
            Assert.Equal(stockFails ? "pay-2" : "pay-1", payment.PaymentReference);
            Assert.Equal("paid", payment.PaymentStatus);
            return (JsonSerializer.Serialize(payment), await Fingerprint(services, result.InstanceId));
        }
    }

    private static async Task<string> Fingerprint(IServiceProvider services, string instanceId)
    {
        var saved = await services.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(instanceId);
        using var checkpoint = JsonDocument.Parse(saved!.ExecutionStateJson);
        return checkpoint.RootElement.GetProperty("DefinitionFingerprint").GetString()!;
    }

    private static ServiceProvider Services(Gateway gateway, FakeTimeProvider time)
    {
        var services = Hosting.Services();
        services.AddSingleton<TimeProvider>(time);
        services.AddWaitRecipes();
        services.AddSingleton<IApprovalMailer>(gateway);
        services.AddSingleton<IPaymentProvider>(gateway);
        services.AddSingleton<IPaymentRefunds>(gateway);
        services.AddSingleton<IStock>(gateway);
        return services.BuildServiceProvider();
    }

    private sealed class Gateway : IApprovalMailer, IPaymentProvider, IPaymentRefunds, IStock
    {
        public ConcurrentQueue<ApprovalRequest> Approvals { get; } = new();
        public ConcurrentQueue<string> Payments { get; } = new();
        public ConcurrentQueue<string> Refunds { get; } = new();
        public bool FailStock { get; set; }

        public Task SendAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Approvals.Enqueue(request);
            return Task.CompletedTask;
        }

        public Task<string> CreatePaymentAsync(decimal amount, string replyToken, CancellationToken cancellationToken)
        {
            Assert.Equal(40m, amount);
            Payments.Enqueue(replyToken);
            return Task.FromResult("pay-" + Payments.Count);
        }

        public Task RefundAsync(string reference, CancellationToken cancellationToken)
        {
            Refunds.Enqueue(reference);
            return Task.CompletedTask;
        }

        public Task ReserveAsync(string orderId, CancellationToken cancellationToken)
        {
            Assert.Equal("order-1", orderId);
            if (FailStock)
            {
                FailStock = false;
                throw new StockUnavailable("Restock first");
            }
            return Task.CompletedTask;
        }
    }
}

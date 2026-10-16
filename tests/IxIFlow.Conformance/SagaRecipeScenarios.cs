using IxIFlow.Core;
using IxIFlow.Recipes;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// Recipe 8 of spec D-17, a payment requested inside a saga, run as the sample defines it in every
/// profile (J-02). It asserts what became of every callback it sends.
/// </summary>
[Trait("Spec", "D-17")]
[Trait("Spec", "D-16")]
public sealed class SagaRecipeScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ARefundedPaymentsCallbackIsRefusedAndTheRetrysPaymentCompletesTheOrder(string profile)
    {
        var payments = new Payments();
        var stock = new Stock(failures: 1);
        await using var cluster = await ConformanceCluster.CreateAsync(profile, services =>
        {
            services.AddWaitRecipes();
            services.AddSingleton<IPaymentProvider>(payments);
            services.AddSingleton<IPaymentRefunds>(payments);
            services.AddSingleton<IStock>(stock);
        });
        var engine = cluster.AddHost();

        var started = await engine.RunAsync(SagaPaymentRecipe.Definition(),
            new Payment { OrderId = "order-8", Amount = 40 });

        // The first reservation failed: its payment was refunded and the retry created a second one.
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal(2, payments.Created.Count);
        Assert.Equal(["pay-1"], payments.Refunded);
        var (first, second) = (payments.Created[0], payments.Created[1]);
        Assert.NotEqual(first, second);

        EventAssert.Rejected(await engine.ReplyAsync(first, new PaymentReceived { Status = "paid" }),
            EventRejection.WaitCancelled);
        var paid = await engine.ReplyAsync(second, new PaymentReceived { Status = "paid" });

        EventAssert.Consumed(paid);
        Assert.Equal(WorkflowExecutionStatus.Success, paid.Status);
        var payment = Assert.IsType<Payment>(paid.WorkflowData);
        Assert.Equal(("paid", "pay-2"), (payment.PaymentStatus, payment.PaymentReference));
    }

    private sealed class Payments : IPaymentProvider, IPaymentRefunds
    {
        private readonly List<string> _created = [];
        private readonly List<string> _refunded = [];

        /// <summary>The reply token of each payment created, in order.</summary>
        public IReadOnlyList<string> Created
        {
            get { lock (_created) return _created.ToArray(); }
        }

        public IReadOnlyList<string> Refunded
        {
            get { lock (_refunded) return _refunded.ToArray(); }
        }

        public Task<string> CreatePaymentAsync(decimal amount, string replyToken, CancellationToken cancellationToken)
        {
            lock (_created)
            {
                _created.Add(replyToken);
                return Task.FromResult($"pay-{_created.Count}");
            }
        }

        public Task RefundAsync(string reference, CancellationToken cancellationToken)
        {
            lock (_refunded)
                _refunded.Add(reference);
            return Task.CompletedTask;
        }
    }

    private sealed class Stock(int failures) : IStock
    {
        private int _attempts;

        public Task ReserveAsync(string orderId, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _attempts) <= failures
                ? throw new StockUnavailable($"No stock for {orderId}")
                : Task.CompletedTask;
    }
}

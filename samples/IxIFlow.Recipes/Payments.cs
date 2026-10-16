using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Recipes;

/// <summary>
/// Creates payments with a provider that calls back when the payment settles. The reply token goes
/// into the payment's metadata; the provider's webhook answers with
/// <c>IWorkflowClient.ReplyAsync(token, received)</c>.
/// </summary>
public interface IPaymentProvider
{
    Task<string> CreatePaymentAsync(decimal amount, string replyToken, CancellationToken cancellationToken);
}

/// <summary>Refunds a payment the provider created.</summary>
public interface IPaymentRefunds
{
    Task RefundAsync(string reference, CancellationToken cancellationToken);
}

/// <summary>Reserves an order's stock.</summary>
public interface IStock
{
    /// <summary>Reserves the order's items, or throws <see cref="StockUnavailable"/>.</summary>
    Task ReserveAsync(string orderId, CancellationToken cancellationToken);
}

public sealed class StockUnavailable(string message) : Exception(message);

/// <summary>Checks an order for fraud while its payment is pending.</summary>
public interface IFraudChecks
{
    Task<bool> IsSuspiciousAsync(string orderId, CancellationToken cancellationToken);
}

/// <summary>The provider's callback. A "pending" callback can come before "paid".</summary>
public sealed class PaymentReceived
{
    public string Status { get; set; } = "";
}

public sealed class Payment
{
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
    public string PaymentReference { get; set; } = "";
    public string PaymentStatus { get; set; } = "";
    public bool Suspicious { get; set; }
    public bool Cancelled { get; set; }
}

public sealed class CreatePayment(IPaymentProvider provider) : IRequestActivity<PaymentReceived>
{
    public decimal Amount { get; set; }
    public string Reference { get; set; } = "";

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        Reference = await provider.CreatePaymentAsync(Amount, context.Reply!.Token, cancellationToken);
}

public sealed class CheckFraud(IFraudChecks checks) : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public bool Suspicious { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        Suspicious = await checks.IsSuspiciousAsync(OrderId, cancellationToken);
}

public sealed class RefundPayment(IPaymentRefunds refunds) : IAsyncActivity
{
    public string Reference { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        refunds.RefundAsync(Reference, cancellationToken);
}

public sealed class ReserveStock(IStock stock) : IAsyncActivity
{
    public string OrderId { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        stock.ReserveAsync(OrderId, cancellationToken);
}

public sealed class CancelPayment : IAsyncActivity
{
    public bool Cancelled { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Cancelled = true;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Recipe 2, request now and wait later (spec D-17), and recipe 11, on time beats the deadline: create
/// the payment, check for fraud while it is pending, then wait for the provider's callback. A callback
/// that arrives during the fraud check is kept for the wait; a "pending" callback is refused by the
/// wait's condition and the wait stays open for "paid". A callback received before the deadline wins
/// even when the fraud check runs past it.
/// </summary>
public static class PaymentRecipe
{
    public static WorkflowDefinition Definition(TimeSpan paymentTimeout) =>
        Workflow.Create<Payment>("PaymentThenFraudCheck")
            .Step<ValidateOrder>()
            .Request<CreatePayment>("payment", step => step
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(activity => activity.Reference).To(ctx => ctx.WorkflowData.PaymentReference))
            .Step<CheckFraud>(step => step
                .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(activity => activity.Suspicious).To(ctx => ctx.WorkflowData.Suspicious))
            .WaitFor<PaymentReceived>("payment", (received, _) => received.Status != "pending", wait => wait
                .Output(received => received.Status).To(ctx => ctx.WorkflowData.PaymentStatus)
                .TimeoutAfter(paymentTimeout)
                .OnTimeout(path => path.Step<CancelPayment>(step => step
                    .Output(activity => activity.Cancelled).To(ctx => ctx.WorkflowData.Cancelled))))
            .Build();
}

/// <summary>
/// Recipe 8, payment in a saga (spec D-17): the payment request is a saga step, refunded when a later
/// step fails. The refund runs whether or not the provider already called back. Compensation closes the
/// payment's wait, so a later callback for that payment is refused as cancelled. The saga's retry creates
/// a new payment with a new token, which the old token cannot answer.
/// </summary>
public static class SagaPaymentRecipe
{
    public static WorkflowDefinition Definition() =>
        Workflow.Create<Payment>("PaymentInASaga")
            .Step<ValidateOrder>()
            .Saga(saga => saga
                .Request<CreatePayment>("payment", step => step
                    .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(activity => activity.Reference).To(ctx => ctx.WorkflowData.PaymentReference)
                    .CompensateWith<RefundPayment>(refund => refund
                        .Input(activity => activity.Reference).From(ctx => ctx.CurrentStep.Reference)))
                .Step<ReserveStock>(step => step
                    .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .WaitFor<PaymentReceived>("payment", (received, _) => received.Status != "pending", wait => wait
                    .Output(received => received.Status).To(ctx => ctx.WorkflowData.PaymentStatus)))
            .OnError<StockUnavailable>(error => error.Compensate().ThenRetry(1))
            .Build();
}

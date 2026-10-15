using IxIFlow.Core;

namespace IxIFlow.Tests.SyntaxTests.Models;

// =====================================================
// ACTIVITIES
// =====================================================
public class NoopActivity : IAsyncActivity
{
    public string Name { get; set; } = "NoopActivity";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class ValidateOrderAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
    public string CustomerName { get; set; } = "";
    public bool IsValid { get; set; }
    public List<string> ValidationErrors { get; set; } = new();

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class CheckInventoryAsyncActivity : IAsyncActivity
{
    public string ProductId { get; set; } = "";
    public bool IsOrderValid { get; set; }
    public bool InStock { get; set; }
    public decimal FinalPrice { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class ReserveInventoryAsyncActivity : IAsyncActivity
{
    public string ProductId { get; set; } = "";
    public bool InStock { get; set; }
    public string ReservationId { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class CalculatePricingAsyncActivity : IAsyncActivity
{
    public string ReservationId { get; set; } = "";
    public decimal BasePrice { get; set; }
    public decimal FinalPrice { get; set; }
    public object Discounts { get; set; } = null!;

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class ProcessPaymentAsyncActivity : IAsyncActivity
{
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = "";
    public string TransactionId { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class ChargePaymentAsyncActivity : IAsyncActivity
{
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public string PaymentStatus { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class RefundPaymentAsyncActivity : IAsyncActivity
{
    public string TransactionId { get; set; } = "";
    public decimal RefundAmount { get; set; }
    public bool RefundSuccessful { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class ReleaseInventoryAsyncActivity : IAsyncActivity
{
    public string ReservationId { get; set; } = "";
    public bool ReleasedSuccessfully { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class SendEmailAsyncActivity : IAsyncActivity
{
    public string EmailAddress { get; set; } = "";
    public object NotificationData { get; set; } = null!;
    public string OrderId { get; set; } = "";
    public bool EmailSent { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class SendSmsAsyncActivity : IAsyncActivity
{
    public string PhoneNumber { get; set; } = "";
    public object NotificationData { get; set; } = null!;
    public string OrderId { get; set; } = "";
    public bool SmsSent { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class SendConfirmationAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public string ShipmentId { get; set; } = "";
    public object ProcessedData { get; set; } = null!;
    public bool ConfirmationSent { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class ProcessValidOrderAsyncActivity : IAsyncActivity
{
    public object OrderData { get; set; } = null!;
    public bool ValidationResult { get; set; }
    public object ProcessedOrder { get; set; } = null!;

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class RejectOrderAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public List<string> ValidationErrors { get; set; } = new();
    public string RejectionReason { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class PrepareDataAsyncActivity : IAsyncActivity
{
    public object RawData { get; set; } = null!;
    public object PreparedData { get; set; } = null!;

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class ProcessValidatedDataAsyncActivity : IAsyncActivity
{
    public bool IsValid { get; set; }
    public object ProcessingData { get; set; } = null!;
    public object FinalResult { get; set; } = null!;

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class RiskyAsyncActivity : IAsyncActivity
{
    public object Data { get; set; } = null!;
    public object Result { get; set; } = null!;

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class ErrorLoggingAsyncActivity : IAsyncActivity
{
    public string ErrorMessage { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    public DateTime LoggedAt { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class BusinessErrorHandlerAsyncActivity : IAsyncActivity
{
    public List<string> BusinessErrors { get; set; } = new();
    public object HandledErrors { get; set; } = null!;

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class CleanupAsyncActivity : IAsyncActivity
{
    public string WorkflowId { get; set; } = "";
    public object CleanupResult { get; set; } = null!;

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class ProcessOrderAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public object ProcessedOrder { get; set; } = null!;
    public decimal OrderAmount { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class CompleteOrderAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public object ProcessedData { get; set; } = null!;
    public DateTime CompletedAt { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class FinalizeOrderAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public DateTime FinalizedAt { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class CancelOrderAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public bool Cancelled { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class CreateShipmentAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public string ReservationId { get; set; } = "";
    public string ShipmentId { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class CancelShipmentAsyncActivity : IAsyncActivity
{
    public string ShipmentId { get; set; } = "";
    public bool Cancelled { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class HandleSagaFailureAsyncActivity : IAsyncActivity
{
    public string ErrorMessage { get; set; } = "";
    public bool FailureHandled { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class UpdateAnalyticsAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public bool Success { get; set; }
    public bool AnalyticsUpdated { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class PrepareNotificationDataAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public object NotificationData { get; set; } = null!;

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class VerifyNotificationsAsyncActivity : IAsyncActivity
{
    public bool EmailStatus { get; set; }
    public bool SmsStatus { get; set; }
    public bool AllNotificationsSent { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class InitializeBatchAsyncActivity : IAsyncActivity
{
    public object BatchData { get; set; } = null!;
    public List<object> BatchItems { get; set; } = new();
    public int ProcessedCount { get; set; }
    public List<object> RemainingItems { get; set; } = new();
    public int ProcessedItems { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class ProcessBatchItemAsyncActivity : IAsyncActivity
{
    public List<object> BatchItems { get; set; } = new();
    public int ProcessedItems { get; set; }
    public List<object> RemainingItems { get; set; } = new();

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class CalculatePricingAsyncActivityMock : IAsyncActivity
{
    public string ProductId { get; set; } = "";
    public bool InStock { get; set; }
    public decimal FinalPrice { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class UseDefaultPricingAsyncActivity : IAsyncActivity
{
    public string ProductId { get; set; } = "";
    public decimal DefaultPrice { get; set; }
    public string SomeMessage { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class LogPaymentException : IAsyncActivity
{
    public string ErrorMessage { get; set; } = "";
    public bool Logged { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class SendCancellationAsyncActivity : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class HandleDeclinedPaymentAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public string DeclineReason { get; set; } = "";
    public bool Handled { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class LogUnknownStatusAsyncActivity : IAsyncActivity
{
    public string Status { get; set; } = "";
    public string OrderId { get; set; } = "";
    public bool Logged { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

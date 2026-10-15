namespace IxIFlow.Tests.SyntaxTests.Models;

// =====================================================
// DATA MODELS
// =====================================================

public class OrderWorkflowData
{
    public string WorkflowId { get; set; } = Guid.NewGuid().ToString();
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
    public string ProductId { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public string PaymentMethod { get; set; } = "";
    public decimal BasePrice { get; set; }

    // Results from activities
    public bool ValidationResult { get; set; }
    public List<string> ValidationErrors { get; set; } = new();
    public bool InventoryStatus { get; set; }
    public string ReservationId { get; set; } = "";
    public decimal FinalPrice { get; set; }
    public string TransactionId { get; set; } = "";
    public string ShipmentId { get; set; } = "";
    public bool EmailStatus { get; set; }
    public bool SmsStatus { get; set; }
    public bool ConfirmationStatus { get; set; }
    public DateTime CompletedTimestamp { get; set; }
    public bool ErrorHandled { get; set; }
    public string ErrorMessage { get; set; } = "";
    public object ProcessedOrder { get; set; } = null!;
    public object NotificationInfo { get; set; } = null!;
    public bool NotificationComplete { get; set; }
    public bool AnalyticsStatus { get; set; }
    public bool RequiresSecondApproval { get; set; }
    public bool Approved { get; set; }
    public string PaymentStatus { get; set; }
}

public class ValidationWorkflowData
{
    public string WorkflowId { get; set; } = "";
    public object DataToValidate { get; set; } = null!;
    public bool ValidationResult { get; set; }
    public List<string> ValidationErrors { get; set; } = new();
}

public class BatchWorkflowData
{
    public object BatchInput { get; set; } = null!;
    public List<object> RemainingItems { get; set; } = new();
    public int ProcessedCount { get; set; }
    public bool AnalyticsStatus { get; set; }
}

public class ConfirmOrderEventData
{
    public string OrderId { get; set; } = "";
    public string ConfirmationCode { get; set; } = "";
    public bool IsConfirmed { get; set; }
    public DateTime ConfirmedAt { get; set; }
}

namespace IxIFlow.Tests.SyntaxTests.Models;

// =====================================================
// EVENTS
// =====================================================

public class ApprovalEventData
{
    public string OrderId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public bool Approved { get; set; }
    public bool Declined { get; set; }
    public bool RequiresDoubleApproval { get; set; }
}

public class SecondApprovalEventData
{
    public string OrderId { get; set; } = "";
    public bool Approved { get; set; }
    public bool Declined { get; set; }
}

public class PaymentCorrectedEventData
{
    public string OrderId { get; set; } = "";
    public string CorrectedPaymentMethod { get; set; } = "";
}

public class SystemAvailableEventData
{
    public string SystemName { get; set; } = "";
    public bool IsAvailable { get; set; }
}
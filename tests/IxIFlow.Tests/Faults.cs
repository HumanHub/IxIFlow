namespace IxIFlow.Tests;

public sealed class MessageFault
{
    public string Message { get; set; } = "";
}

public sealed class PaymentFault
{
    public string Message { get; set; } = "";
    public decimal PaymentAmount { get; set; }
}

public sealed class BusinessFault
{
    public List<string> BusinessErrors { get; set; } = [];
}

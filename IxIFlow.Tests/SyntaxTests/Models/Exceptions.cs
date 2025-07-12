namespace IxIFlow.Tests.SyntaxTests.Models;

// =====================================================
// EXCEPTIONS
// =====================================================

public class PaymentException : Exception
{
    public PaymentException() : base("Payment exception")
    {
    }
}

public class PaymentTimeoutException : Exception
{
    public PaymentTimeoutException() : base("Payment timed out")
    {
    }
}

public class InsufficientFundsException : Exception
{
    public InsufficientFundsException() : base("Insufficient funds")
    {
    }
}

public class InventoryException : Exception
{
    public InventoryException(string message) : base(message)
    {
    }
}

public class EmailException : Exception
{
    public EmailException(string message) : base(message)
    {
    }
}

public class BusinessException : Exception
{
    public BusinessException(List<string> errors) : base("Business validation failed")
    {
        BusinessErrors = errors;
    }

    public List<string> BusinessErrors { get; }
}

public class PricingException : Exception
{
    public PricingException(string message) : base(message)
    {
    }
}


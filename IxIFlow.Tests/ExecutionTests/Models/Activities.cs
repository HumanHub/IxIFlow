using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests.Models;

// =====================================================
// WORKFLOW EXECUTION DATA MODELS
// =====================================================

/// <summary>
///     Order workflow data for testing workflow patterns
/// </summary>
public class OrderWorkflowData
{
    public string OrderId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string ProductId { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;

    // Output fields
    public bool ValidationResult { get; set; }
    public List<string> ValidationErrors { get; set; } = new();
    public string? ProcessedOrder { get; set; }
    public string? ErrorMessage { get; set; }
    public bool AnalyticsStatus { get; set; }
    public string InventoryStatus { get; set; } = string.Empty;
    public bool HasInventory { get; set; }
    public string ReservationId { get; set; } = string.Empty;
    public decimal FinalPrice { get; set; }
    public string TransactionId { get; set; } = string.Empty;
}

/// <summary>
///     Simple workflow data for basic sequence testing
/// </summary>
public class SequenceTestData
{
    public string TestId { get; set; } = string.Empty;
    public int StepCount { get; set; }
    public List<string> ExecutionLog { get; set; } = new();
    public string FinalResult { get; set; } = string.Empty;
}

/// <summary>
///     Loop execution test data model
/// </summary>
public class LoopTestData
{
    public string TestId { get; set; } = string.Empty;
    public int Counter { get; set; }
    public int MaxIterations { get; set; }
    public int InnerCounter { get; set; }
    public int InnerMaxIterations { get; set; }
    public int TargetSum { get; set; }
    public string PreLoopValue { get; set; } = string.Empty;
    public string PostLoopValue { get; set; } = string.Empty;
    public List<string> ExecutionLog { get; set; } = new();
    public List<int> IterationResults { get; set; } = new();
    public string TestSummary { get; set; } = string.Empty;
}

/// <summary>
///     Exception handling test data model
/// </summary>
public class ExceptionTestData
{
    public string OrderId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string TransactionId { get; set; } = string.Empty;
    public bool ErrorHandled { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public bool ValidationResult { get; set; }
    public bool PaymentProcessed { get; set; }
    public decimal RefundAmount { get; set; }
    public bool RefundProcessed { get; set; }
    public bool FinallyExecuted { get; set; }
}

// =====================================================
// BASIC TEST ACTIVITIES
// =====================================================

/// <summary>
///     Order validation activity for testing
/// </summary>
public class ValidateOrderAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = string.Empty;
    public decimal Amount { get; set; }

    public bool IsValid { get; set; }
    public bool ValidateOrderCompleted { get; set; }
    public List<string> ValidationErrors { get; set; } = new();

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        ValidationErrors.Clear();

        if (string.IsNullOrWhiteSpace(OrderId))
            ValidationErrors.Add("OrderId cannot be empty");

        if (Amount <= 0)
            ValidationErrors.Add("Amount must be positive");

        IsValid = ValidationErrors.Count == 0;
        ValidateOrderCompleted = true;
        await Task.CompletedTask;
    }
}

/// <summary>
///     Inventory check activity for testing
/// </summary>
public class CheckInventoryAsyncActivity : IAsyncActivity
{
    public string ProductId { get; set; } = string.Empty;
    public bool IsOrderValid { get; set; }

    public bool InStock { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        InStock = IsOrderValid && !string.IsNullOrEmpty(ProductId);
        await Task.CompletedTask;
    }
}

/// <summary>
///     Inventory reservation activity for testing
/// </summary>
public class ReserveInventoryAsyncActivity : IAsyncActivity
{
    public string ProductId { get; set; } = string.Empty;
    public bool InStock { get; set; }

    public string ReservationId { get; set; } = string.Empty;

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        ReservationId = InStock ? $"RES-{ProductId}-{DateTime.UtcNow:yyyyMMddHHmmss}" : string.Empty;
        await Task.CompletedTask;
    }
}

/// <summary>
///     Pricing calculation activity for testing
/// </summary>
public class CalculatePricingAsyncActivity : IAsyncActivity
{
    public string ReservationId { get; set; } = string.Empty;
    public decimal BasePrice { get; set; }

    public decimal FinalPrice { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        FinalPrice = !string.IsNullOrEmpty(ReservationId) ? BasePrice * 1.1m : 0; // Add 10% markup
        await Task.CompletedTask;
    }
}

/// <summary>
///     Payment processing activity for testing
/// </summary>
public class ProcessPaymentAsyncActivity : IAsyncActivity
{
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;

    public string TransactionId { get; set; } = string.Empty;

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        TransactionId = Amount > 0 ? $"TXN-{DateTime.UtcNow:yyyyMMddHHmmss}" : string.Empty;
        await Task.CompletedTask;
    }
}

/// <summary>
///     Analytics update activity for testing
/// </summary>
public class UpdateAnalyticsAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = string.Empty;
    public bool Success { get; set; }

    public bool AnalyticsUpdated { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        AnalyticsUpdated = !string.IsNullOrEmpty(OrderId);
        await Task.CompletedTask;
    }
}

/// <summary>
///     Simple step execution activity for testing
/// </summary>
public class StepExecutionAsyncActivity : IAsyncActivity
{
    public string StepName { get; set; } = string.Empty;
    public int StepNumber { get; set; }

    public string StepOutput { get; set; } = string.Empty;
    public DateTime CompletedAt { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        StepOutput = $"STEP-{StepNumber}-{StepName}-COMPLETED";
        CompletedAt = DateTime.UtcNow;
        await Task.CompletedTask;
    }
}

/// <summary>
///     Saga workflow data for testing saga patterns
/// </summary>
public class SagaTestData
{
    public string OrderId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string ProductId { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;

    // Validation results
    public bool ValidationResult { get; set; }

    // Step tracking
    public bool Step1Completed { get; set; }
    public bool Step2Completed { get; set; }
    public bool Step3Completed { get; set; }

    // Compensation tracking
    public bool Step1Compensated { get; set; }
    public bool Step2Compensated { get; set; }
    public bool Step3Compensated { get; set; }

    // Step outputs
    public string ReservationId { get; set; } = string.Empty;
    public decimal FinalPrice { get; set; }
    public string TransactionId { get; set; } = string.Empty;

    // Post-saga workflow tracking
    public bool AnalyticsUpdated { get; set; }

    // Error tracking
    public string ErrorMessage { get; set; } = string.Empty;
    public bool SagaFailed { get; set; }
    public List<string> CompensationLog { get; set; } = new();
    public bool ErrorHandlerExecuted { get; set; }
    public int RetryAttempts { get; set; }
    public string SuspensionMessage { get; set; }

    // OutcomeOn testing properties
    public bool CashReceived { get; set; }
    public bool LoggedUnknown { get; set; }
}


// =====================================================
// LOOP TEST ACTIVITIES
// =====================================================

/// <summary>
///     Activity that initializes loop tests
/// </summary>
public class InitializeLoopTestAsyncActivity : IAsyncActivity
{
    public string TestId { get; set; } = string.Empty;

    public int InitialValue { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        InitialValue = 0;
        await Task.CompletedTask;
    }
}

/// <summary>
///     Activity that performs loop iterations
/// </summary>
public class LoopIterationAsyncActivity : IAsyncActivity
{
    public int CurrentCounter { get; set; }
    public int IterationNumber { get; set; }
    public List<int> IterationResults { get; set; } = new();

    public int NewCounter { get; set; }
    public int IterationResult { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        NewCounter = CurrentCounter + 1;
        IterationResult = IterationNumber;
        IterationResults.Add(IterationResult);
        await Task.CompletedTask;
    }
}

/// <summary>
///     Activity that finalizes loop tests
/// </summary>
public class FinalizeLoopTestAsyncActivity : IAsyncActivity
{
    public int FinalCounter { get; set; }
    public List<int> IterationResults { get; set; } = new();

    public string Summary { get; set; } = string.Empty;

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Summary = $"Completed {IterationResults.Count} iterations, final counter: {FinalCounter}";
        await Task.CompletedTask;
    }
}

// =====================================================
// EXCEPTION TEST ACTIVITIES AND EXCEPTIONS
// =====================================================

/// <summary>
///     Custom exceptions for testing
/// </summary>
public class BusinessValidationException : Exception
{
    public BusinessValidationException()
    {
    }

    public BusinessValidationException(string message) : base(message)
    {
    }
}

public class PaymentProcessingException : Exception
{
    public PaymentProcessingException()
    {
    }

    public PaymentProcessingException(string message, decimal paymentAmount) : base(message)
    {
        PaymentAmount = paymentAmount;
    }

    public decimal PaymentAmount { get; }
}

public class InventoryUnavailableException : Exception
{
    public InventoryUnavailableException()
    {
    }

    public InventoryUnavailableException(string message) : base(message)
    {
    }
}

public class CriticalSystemException : Exception
{
    public CriticalSystemException()
    {
    }

    public CriticalSystemException(string message) : base(message)
    {
    }
}

/// <summary>
///     Activity that validates orders and throws BusinessValidationException for invalid data
/// </summary>
public class ValidateOrderWithExceptionActivity : IAsyncActivity
{
    public string OrderId { get; set; } = string.Empty;
    public decimal Amount { get; set; }

    public bool IsValid { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        if (Amount <= 0)
            throw new BusinessValidationException($"Invalid order amount: {Amount}. Amount must be greater than 0.");

        IsValid = true;
        await Task.CompletedTask;
    }
}

/// <summary>
///     Activity that processes payments and throws PaymentProcessingException for certain amounts
/// </summary>
public class ProcessPaymentWithExceptionActivity : IAsyncActivity
{
    public string OrderId { get; set; } = string.Empty;
    public decimal Amount { get; set; }

    public string TransactionId { get; set; } = string.Empty;
    public bool PaymentProcessed { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        if (Amount >= 150m)
            throw new PaymentProcessingException($"Payment processing failed for amount {Amount}", Amount);

        TransactionId = $"TXN-{OrderId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        PaymentProcessed = true;
        await Task.CompletedTask;
    }
}

/// <summary>
///     Activity that logs errors in catch blocks
/// </summary>
public class LogErrorActivity : IAsyncActivity
{
    public string Message { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;

    public bool Logged { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Logged = true;
        await Task.CompletedTask;
    }
}

/// <summary>
///     Activity that handles payment errors and processes refunds
/// </summary>
public class HandlePaymentErrorActivity : IAsyncActivity
{
    public string OrderId { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public decimal RefundAmount { get; set; }

    public bool Handled { get; set; }
    public bool RefundProcessed { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        // Actually use the RefundAmount parameter
        Handled = true;
        RefundProcessed = RefundAmount > 0;
        await Task.CompletedTask;
    }
}

/// <summary>
///     Activity for cleanup operations in finally blocks
/// </summary>
public class CleanupActivity : IAsyncActivity
{
    public string WorkflowId { get; set; } = string.Empty;

    public bool CleanupResult { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        CleanupResult = true;
        await Task.CompletedTask;
    }
}

// =====================================================
// SAGA TEST ACTIVITIES AND DATA MODELS
// =====================================================

/// <summary>
/// Event data for order confirmation during saga suspend/resume operations
/// </summary>
public class OrderConfirmationEvent
{
    public string OrderId { get; set; } = string.Empty;
    public string ConfirmedProductId { get; set; } = string.Empty;
    public decimal ConfirmedAmount { get; set; }
    public bool IsConfirmed { get; set; }
    public string ConfirmedBy { get; set; } = string.Empty;
    public DateTime ConfirmedAt { get; set; } = DateTime.UtcNow;
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
///     Saga step 1: Reserve inventory (with compensation)
/// </summary>
public class SagaReserveInventoryAsyncActivity : IAsyncActivity
{
    public string ProductId { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;

    public string ReservationId { get; set; } = string.Empty;
    public bool Step1Completed { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(OrderId))
            throw new Exception("Inventory reservation failed: OrderId is required");

        ReservationId = $"RES-{ProductId}-{OrderId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        Step1Completed = true;
        Console.WriteLine($"SAGA STEP 1: Reserved inventory - {ReservationId}");
        await Task.CompletedTask;
    }
}

/// <summary>
///     Smart inventory reservation activity that generates ReservationId based on payment method for OutcomeOn testing
/// </summary>
public class SagaSmartReserveInventoryAsyncActivity : IAsyncActivity
{
    public string ProductId { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;

    public string ReservationId { get; set; } = string.Empty;
    public bool Step1Completed { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(OrderId))
            throw new Exception("Inventory reservation failed: OrderId is required");

        // Generate ReservationId based on payment method for OutcomeOn pattern testing
        var prefix = PaymentMethod.ToUpper() switch
        {
            "CARD" => "CARD",
            "CREDITCARD" => "CARD",
            "CASH" => "CASH",
            "CHECK" => "CHCK",
            _ => "UNKN"
        };

        ReservationId = $"{prefix}-{ProductId}-{OrderId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        Step1Completed = true;
        Console.WriteLine($"SAGA STEP 1: Smart reserved inventory - {ReservationId} (PaymentMethod: {PaymentMethod})");
        await Task.CompletedTask;
    }
}

/// <summary>
///     Compensation for SagaReserveInventoryAsyncActivity
/// </summary>
public class ReleaseInventoryCompensationAsyncActivity : IAsyncActivity
{
    public string ReservationId { get; set; } = string.Empty;

    public bool Step1Compensated { get; set; }
    public string UselessProperty { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Console.WriteLine(
                    "COMPENSATION ACTIVITY DEBUG: ReleaseInventoryCompensationAsyncActivity.ExecuteAsync() starting");
        Console.WriteLine($"  - ReservationId input: '{ReservationId}'");
        Console.WriteLine($"  - Step1Compensated initial value: {Step1Compensated}");

        // Only compensate and set flag if we have a valid reservation ID
        if (!string.IsNullOrWhiteSpace(ReservationId))
        {
            Step1Compensated = true;
            Console.WriteLine("  - ReservationId is valid, setting Step1Compensated = true");
            Console.WriteLine($"COMPENSATION STEP 1: Released inventory - {ReservationId}");
        }
        else
        {
            Step1Compensated = false;
            Console.WriteLine("  - ReservationId is empty/null, setting Step1Compensated = false");
            Console.WriteLine("  - WARNING: No inventory to release because ReservationId is empty");
        }

        Console.WriteLine($"  - Step1Compensated final value: {Step1Compensated}");
        Console.WriteLine(
            "\ud83d\udd0d COMPENSATION ACTIVITY DEBUG: ReleaseInventoryCompensationAsyncActivity.ExecuteAsync() completed");

        await Task.CompletedTask;
    }
}

/// <summary>
///     Saga step 2: Calculate pricing (with compensation)
/// </summary>
public class SagaCalculatePricingAsyncActivity : IAsyncActivity
{
    public string ReservationId { get; set; } = string.Empty;
    public decimal Amount { get; set; }

    public decimal FinalPrice { get; set; }
    public bool Step2Completed { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        if (Amount <= 0)
            throw new Exception("Pricing calculation failed: Amount must be positive");

        FinalPrice = Amount * 1.1m; // Add 10% markup
        Step2Completed = true;
        Console.WriteLine($"SAGA STEP 2: Calculated pricing - ${FinalPrice}");
        await Task.CompletedTask;
    }
}

/// <summary>
///     Compensation for SagaCalculatePricingAsyncActivity
/// </summary>
public class RevertPricingCompensationAsyncActivity : IAsyncActivity
{
    public decimal FinalPrice { get; set; }

    public bool Step2Compensated { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        // Only compensate and set flag if we have a valid final price
        if (FinalPrice > 0)
        {
            Step2Compensated = true;
            Console.WriteLine($"COMPENSATION STEP 2: Reverted pricing - ${FinalPrice}");
        }
        else
        {
            Step2Compensated = false;
        }

        await Task.CompletedTask;
    }
}

/// <summary>
///     Saga step 3: Process payment (can fail to test compensation)
/// </summary>
public class SagaProcessPaymentAsyncActivity : IAsyncActivity
{
    public decimal FinalPrice { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public bool ShouldFail { get; set; } // For testing compensation

    public string TransactionId { get; set; } = string.Empty;
    public bool Step3Completed { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        if (ShouldFail)
        {
            Console.WriteLine($"SAGA STEP 3: Payment failed for ${FinalPrice}");
            throw new PaymentProcessingException($"Payment processing failed for amount {FinalPrice}", FinalPrice);
        }

        TransactionId = $"TXN-{DateTime.UtcNow:yyyyMMddHHmmss}";
        Step3Completed = true;
        Console.WriteLine($"SAGA STEP 3: Processed payment - {TransactionId}");
        await Task.CompletedTask;
    }
}

/// <summary>
///     Compensation for SagaProcessPaymentAsyncActivity
/// </summary>
public class RefundPaymentCompensationAsyncActivity : IAsyncActivity
{
    public string TransactionId { get; set; } = string.Empty;
    public decimal FinalPrice { get; set; }

    public bool Step3Compensated { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        // Only compensate and set flag if we have a valid transaction ID
        if (!string.IsNullOrWhiteSpace(TransactionId))
        {
            Step3Compensated = true;
            Console.WriteLine($"COMPENSATION STEP 3: Refunded payment - {TransactionId}");
        }
        else
        {
            Step3Compensated = false;
        }

        await Task.CompletedTask;
    }
}

/// <summary>
///     Saga error handling activity
/// </summary>
public class HandleSagaFailureAsyncActivity : IAsyncActivity
{
    public string ErrorMessage { get; set; } = string.Empty;

    public bool FailureHandled { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        FailureHandled = true;
        Console.WriteLine($"SAGA ERROR HANDLED: {ErrorMessage}");
        await Task.CompletedTask;
    }
}

/// <summary>
///     Saga error handling activity
/// </summary>
public class SagaOnErrorLogActivity : IAsyncActivity
{
    public bool ErrorHandlerExecuted { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        ErrorHandlerExecuted = true;
        Console.WriteLine("SAGA OnError Handler Executed.");
        await Task.CompletedTask;
    }
}

// =====================================================
// ADDITIONAL SAGA TEST ACTIVITIES AND EXCEPTIONS
// =====================================================

/// <summary>
///     Additional exceptions for comprehensive saga testing
/// </summary>
public class ValidationException : Exception
{
    public ValidationException()
    {
    }

    public ValidationException(string message) : base(message)
    {
    }
}

public class InsufficientFundsException : Exception
{
    public InsufficientFundsException()
    {
    }

    public InsufficientFundsException(string message, decimal required, decimal available) : base(message)
    {
        RequiredAmount = required;
        AvailableAmount = available;
    }

    public decimal RequiredAmount { get; }
    public decimal AvailableAmount { get; }
}

public class SystemUnavailableException : Exception
{
    public SystemUnavailableException()
    {
    }

    public SystemUnavailableException(string message, string systemName) : base(message)
    {
        SystemName = systemName;
    }

    public string SystemName { get; }
}

public class TimeoutException : Exception
{
    public TimeoutException()
    {
    }

    public TimeoutException(string message, TimeSpan timeout) : base(message)
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
}

/// <summary>
///     Event for system availability notifications
/// </summary>
public class SystemAvailableEvent
{
    public string SystemName { get; set; } = string.Empty;
    public DateTime AvailableAt { get; set; } = DateTime.UtcNow;
    public string Message { get; set; } = string.Empty;
}

/// <summary>
///     Retryable payment activity that can succeed after failures
/// </summary>
public class SagaRetryablePaymentAsyncActivity : IAsyncActivity
{
    public decimal FinalPrice { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public int RetryAttempts { get; set; } // OUTPUT: Final retry count for workflow data

    public string TransactionId { get; set; } = string.Empty;
    public bool Step3Completed { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        // Read from saga-level retry metadata
        var currentAttempt = context.Metadata.TryGetValue("Saga:CurrentAttempt", out var attemptObj)
            ? (int)attemptObj
            : 0; // Default to 0 for first saga attempt

        Console.WriteLine($"RETRYABLE PAYMENT: Saga CurrentAttempt from engine metadata: {currentAttempt}");

        // Use 0-based logic - fail on attempt 0, succeed on attempt 1+
        if (currentAttempt == 0)
        {
            RetryAttempts = currentAttempt; // OUTPUT for workflow data
            Console.WriteLine($"RETRYABLE PAYMENT: First attempt (0) failed for ${FinalPrice}");
            throw new PaymentProcessingException(
                $"Payment processing failed for amount {FinalPrice} (will succeed on retry)", FinalPrice);
        }

        TransactionId = $"TXN-RETRY-{DateTime.UtcNow:yyyyMMddHHmmss}";
        Step3Completed = true;
        RetryAttempts = currentAttempt; // OUTPUT for workflow data
        Console.WriteLine($"RETRYABLE PAYMENT: Succeeded on retry attempt {currentAttempt} - {TransactionId}");
        await Task.CompletedTask;
    }
}

/// <summary>
///     Activity that throws a custom exception for default behavior testing
/// </summary>
public class SagaThrowCustomExceptionAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = string.Empty;

    public bool Step2Completed { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"CUSTOM EXCEPTION: Throwing unhandled exception for order {OrderId}");
        throw new InvalidOperationException($"Custom saga exception for order {OrderId} - no explicit handler defined");
    }
}

/// <summary>
///     Activity that can throw different types of errors based on test scenario
/// </summary>
public class SagaStepWithMultipleErrorsAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = string.Empty;
    public string TestScenario { get; set; } = string.Empty;
    public int RetryAttempts { get; set; } // OUTPUT: Final retry count for workflow data

    public bool Step1Completed { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        // Get current attempt from activity context metadata (engine provides this)
        var currentAttempt = 1; // Default if no metadata
        var maxAttempts = 1; // Default if no metadata

        if (context.Metadata.TryGetValue("CurrentAttempt", out var attempt))
            currentAttempt = (int)attempt;
        if (context.Metadata.TryGetValue("MaxAttempts", out var max))
            maxAttempts = (int)max;

        Console.WriteLine($"MULTI-ERROR STEP: Scenario '{TestScenario}', Engine Attempt {currentAttempt}/{maxAttempts}");

        switch (TestScenario.ToLower())
        {
            case "timeout":
                // 0-based attempts - fail on attempts 0 & 1, succeed on attempt 2
                if (currentAttempt <= 1)
                {
                    Console.WriteLine($"TIMEOUT RETRY: Engine attempt {currentAttempt}, will throw TimeoutException");
                    throw new TimeoutException($"Timeout occurred on engine attempt {currentAttempt}",
                        TimeSpan.FromSeconds(30));
                }

                Console.WriteLine($"TIMEOUT SUCCESS: Engine attempt {currentAttempt}, completing successfully");
                break;

            case "validation":
                throw new ValidationException($"Validation failed for order {OrderId}");

            case "insufficient_funds":
                throw new InsufficientFundsException($"Insufficient funds for order {OrderId}", 100m, 50m);
        }

        Step1Completed = true;
        RetryAttempts = currentAttempt; // OUTPUT: Record final successful attempt count
        Console.WriteLine($"MULTI-ERROR STEP: Completed successfully for order {OrderId} on engine attempt {currentAttempt}");
        await Task.CompletedTask;
    }
}

/// <summary>
///     Activity that depends on external systems and can fail due to system unavailability
/// </summary>
public class SagaSystemDependentAsyncActivity : IAsyncActivity
{
    public string SystemName { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;

    public bool Step2Completed { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        // Simulate system unavailability
        Console.WriteLine($"SYSTEM DEPENDENT: {SystemName} is unavailable for order {OrderId}");
        throw new SystemUnavailableException($"System {SystemName} is currently unavailable", SystemName);
    }
}

// =====================================================
// SUSPEND/RESUME TEST ACTIVITIES AND DATA MODELS
// =====================================================

/// <summary>
///     Suspend/Resume workflow data for testing suspend/resume patterns
/// </summary>
public class SuspendResumeTestData
{
    public string OrderId { get; set; } = string.Empty;
    public string ProcessedOrder { get; set; } = string.Empty;
    public DateTime CompletedTimestamp { get; set; }

    // Step completion tracking
    public bool ProcessOrderCompleted { get; set; }
    public bool ValidateOrderCompleted { get; set; }
    public bool CompleteOrderCompleted { get; set; }

    // Approval tracking
    public bool RequiresApproval { get; set; }
    public bool ApprovalStatus { get; set; }
    public bool RequiresSecondApproval { get; set; }
    public string ApproverName { get; set; } = string.Empty;
}

/// <summary>
///     Event data for approval events in suspend/resume tests
/// </summary>
public class ApprovalEvent
{
    public string OrderId { get; set; } = string.Empty;
    public bool Approved { get; set; }
    public bool Declined { get; set; }
    public bool RequiresDoubleApproval { get; set; }
    public string ApproverName { get; set; } = string.Empty;
    public DateTime ApprovedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
///     Event data for second approval events in suspend/resume tests
/// </summary>
public class SecondApprovalEvent
{
    public string OrderId { get; set; } = string.Empty;
    public bool Approved { get; set; }
    public bool Declined { get; set; }
    public string SecondApproverName { get; set; } = string.Empty;
    public DateTime ApprovedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
///     Activity that processes orders for suspend/resume testing
/// </summary>
public class ProcessOrderAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = string.Empty;

    public string ProcessedOrder { get; set; } = string.Empty;
    public bool ProcessOrderCompleted { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(OrderId))
            throw new ArgumentException("OrderId cannot be empty");

        ProcessedOrder = $"PROCESSED-{OrderId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        ProcessOrderCompleted = true;
        Console.WriteLine($"PROCESS ORDER: Processed order {OrderId}");
        await Task.CompletedTask;
    }
}

/// <summary>
///     Activity that completes orders for suspend/resume testing
/// </summary>
public class CompleteOrderAsyncActivity : IAsyncActivity
{
    public string OrderId { get; set; } = string.Empty;
    public string ProcessedData { get; set; } = string.Empty;
    public bool ApprovalStatus { get; set; }

    public DateTime CompletedAt { get; set; }
    public bool CompleteOrderCompleted { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(OrderId))
            throw new ArgumentException("OrderId cannot be empty");

        CompletedAt = DateTime.UtcNow;
        CompleteOrderCompleted = true;
        Console.WriteLine($"COMPLETE ORDER: Completed order {OrderId}");
        await Task.CompletedTask;
    }
}

/// <summary>
///     Cash payment activity for OutcomeOn testing
/// </summary>
public class SagaCashPaymentAsyncActivity : IAsyncActivity
{
    public decimal Amount { get; set; }
    public string OrderId { get; set; } = string.Empty;

    public bool CashReceived { get; set; }
    public bool Step2Completed { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        CashReceived = true; // Simulate cash payment received
        Step2Completed = true;
        Console.WriteLine($"CASH PAYMENT: Received ${Amount} for order {OrderId}");
        await Task.CompletedTask;
    }
}

/// <summary>
///     Log unknown payment activity for OutcomeOn testing
/// </summary>
public class SagaLogUnknownPaymentAsyncActivity : IAsyncActivity
{
    public string PaymentMethod { get; set; } = string.Empty;

    public bool LoggedUnknown { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        LoggedUnknown = true;
        Console.WriteLine($"LOG UNKNOWN PAYMENT: Unknown payment method '{PaymentMethod}' logged");
        await Task.CompletedTask;
    }
}

/// <summary>
///     Final step activity for sequence testing
/// </summary>
public class FinalizeExecutionAsyncActivity : IAsyncActivity
{
    public List<string> ExecutionLog { get; set; } = new();
    public string LastStepOutput { get; set; } = string.Empty;

    public string FinalResult { get; set; } = string.Empty;

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        FinalResult = $"FINALIZED-{ExecutionLog.Count}-STEPS-{DateTime.UtcNow:HHmmss}";
        await Task.CompletedTask;
    }
}

/// <summary>
///     Slow activity for capacity and concurrency testing
/// </summary>
public class SlowExecutionAsyncActivity : IAsyncActivity
{
    public string TaskName { get; set; } = string.Empty;
    public int DelayMs { get; set; } = 1000;
    public int TaskId { get; set; }

    public string TaskOutput { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public int ActualDelayMs { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        StartedAt = DateTime.UtcNow;
        await Task.Delay(DelayMs, cancellationToken);
        CompletedAt = DateTime.UtcNow;
        ActualDelayMs = (int)(CompletedAt - StartedAt).TotalMilliseconds;
        TaskOutput = $"SLOW-TASK-{TaskId}-{TaskName}-COMPLETED-{ActualDelayMs}ms";
    }
}

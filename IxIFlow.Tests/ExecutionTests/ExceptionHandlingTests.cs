using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.ExecutionTests.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

public class ExceptionHandlingTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkflowEngine _workflowEngine;

    public ExceptionHandlingTests()
    {
        // Setup DI container
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<IExpressionEvaluator, ExpressionEvaluator>();
        services.AddSingleton<IActivityExecutor, ActivityExecutor>();
        services.AddSingleton<ISuspendResumeExecutor, SuspendResumeExecutor>();
        services.AddSingleton<ISagaExecutor, SagaExecutor>();
        services.AddSingleton<IWorkflowVersionRegistry, WorkflowVersionRegistry>();
        services.AddSingleton<IEventCorrelator, EventCorrelator>();
        services.AddSingleton<IWorkflowTracer, WorkflowTracer>();
        services.AddSingleton<IWorkflowStateRepository, InMemoryWorkflowStateRepository>();
        services.AddSingleton<IEventStore, InMemoryEventStore>();
        services.AddSingleton<IWorkflowInvoker, WorkflowInvoker>();
        services.AddSingleton<WorkflowEngine>();

        _serviceProvider = services.BuildServiceProvider();
        _workflowEngine = _serviceProvider.GetRequiredService<WorkflowEngine>();
    }

    [Fact]
    public async Task TestBasicExceptionHandling()
    {
        // Arrange
        var definition = Workflow
            .Create<ExceptionTestData>("BasicExceptionHandlingWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InitialStep")
                .Input(act => act.StepNumber).From(ctx => 0))
            .Try(tryBuilder => tryBuilder
                .Step<ValidateOrderWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
                .Step<ProcessPaymentWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                    .Output(act => act.PaymentProcessed).To(ctx => ctx.WorkflowData.PaymentProcessed)))
            .Catch<BusinessValidationException>(catchBuilder => catchBuilder
                .Step<LogErrorActivity>(setup => setup
                    .Input(act => act.Message).From(ctx => ctx.Exception.Message)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.Logged).To(ctx => ctx.WorkflowData.ErrorHandled)
                    .Output(act => act.Message).To(ctx => ctx.WorkflowData.ErrorMessage)))
            .Build();

        var workflowData = new ExceptionTestData
        {
            OrderId = "ORDER-INVALID-001",
            Amount = -100m // Invalid amount that will trigger BusinessValidationException
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ExceptionTestData)result.WorkflowData;

        // Verify exception was caught and handled
        Assert.True(finalData.ErrorHandled);
        Assert.Contains("Invalid order amount", finalData.ErrorMessage);
        Assert.False(finalData.PaymentProcessed);
        Assert.Empty(finalData.TransactionId);
    }

    [Fact]
    public async Task TestSuccessfulExecution()
    {
        // Arrange
        var definition = Workflow
            .Create<ExceptionTestData>("SuccessfulExecutionWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InitialStep")
                .Input(act => act.StepNumber).From(ctx => 0))
            .Try(tryBuilder => tryBuilder
                .Step<ValidateOrderWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
                .Step<ProcessPaymentWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                    .Output(act => act.PaymentProcessed).To(ctx => ctx.WorkflowData.PaymentProcessed)))
            .Catch<BusinessValidationException>(catchBuilder => catchBuilder
                .Step<LogErrorActivity>(setup => setup
                    .Input(act => act.Message).From(ctx => ctx.Exception.Message)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.Logged).To(ctx => ctx.WorkflowData.ErrorHandled)
                    .Output(act => act.Message).To(ctx => ctx.WorkflowData.ErrorMessage)))
            .Build();

        var workflowData = new ExceptionTestData
        {
            OrderId = "ORDER-VALID-001",
            Amount = 100m // Valid amount, no exception should be thrown
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ExceptionTestData)result.WorkflowData;

        // Verify successful execution
        Assert.False(finalData.ErrorHandled);
        Assert.Empty(finalData.ErrorMessage);
        Assert.True(finalData.PaymentProcessed);
        Assert.NotEmpty(finalData.TransactionId);
        Assert.True(finalData.ValidationResult);
    }

    [Fact]
    public async Task TestMultipleCatchBlocks()
    {
        // Arrange
        var definition = Workflow
            .Create<ExceptionTestData>("MultipleCatchBlocksWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InitialStep")
                .Input(act => act.StepNumber).From(ctx => 0))
            .Try(tryBuilder => tryBuilder
                .Step<ValidateOrderWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
                .Step<ProcessPaymentWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                    .Output(act => act.PaymentProcessed).To(ctx => ctx.WorkflowData.PaymentProcessed)))
            .Catch<BusinessValidationException>(catchBuilder => catchBuilder
                .Step<LogErrorActivity>(setup => setup
                    .Input(act => act.Message).From(ctx => ctx.Exception.Message)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.Logged).To(ctx => ctx.WorkflowData.ErrorHandled)
                    .Output(act => act.Message).To(ctx => ctx.WorkflowData.ErrorMessage)))
            .Catch<PaymentProcessingException>(catchBuilder => catchBuilder
                .Step<HandlePaymentErrorActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                    .Input(act => act.RefundAmount).From(ctx => ctx.Exception.PaymentAmount)
                    .Output(act => act.Handled).To(ctx => ctx.WorkflowData.ErrorHandled)
                    .Output(act => act.RefundProcessed).To(ctx => ctx.WorkflowData.RefundProcessed)
                    .Output(act => act.RefundAmount).To(ctx => ctx.WorkflowData.RefundAmount) // Add this line
                    .Output(act => act.ErrorMessage).To(ctx => ctx.WorkflowData.ErrorMessage)))
            .Build();

        var workflowData = new ExceptionTestData
        {
            OrderId = "ORDER-PAYMENT-ERROR-001",
            Amount = 200m // Amount that will trigger PaymentProcessingException
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ExceptionTestData)result.WorkflowData;

        // Verify PaymentProcessingException was caught and handled
        Assert.True(finalData.ErrorHandled);
        Assert.Contains("Payment processing failed", finalData.ErrorMessage);
        Assert.True(finalData.RefundProcessed);
        Assert.Equal(200m, finalData.RefundAmount);
    }

    [Fact]
    public async Task TestFinallyBlock()
    {
        // Arrange
        var definition = Workflow
            .Create<ExceptionTestData>("FinallyBlockWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InitialStep")
                .Input(act => act.StepNumber).From(ctx => 0))
            .Try(tryBuilder => tryBuilder
                .Step<ValidateOrderWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult)))
            .Catch<BusinessValidationException>(catchBuilder => catchBuilder
                .Step<LogErrorActivity>(setup => setup
                    .Input(act => act.Message).From(ctx => ctx.Exception.Message)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.Logged).To(ctx => ctx.WorkflowData.ErrorHandled)
                    .Output(act => act.Message).To(ctx => ctx.WorkflowData.ErrorMessage)))
            .Finally(finallyBuilder => finallyBuilder
                .Step<CleanupActivity>(setup => setup
                    .Input(act => act.WorkflowId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.CleanupResult).To(ctx => ctx.WorkflowData.FinallyExecuted)))
            .Build();

        // Test with exception
        var exceptionData = new ExceptionTestData
        {
            OrderId = "ORDER-FINALLY-001",
            Amount = -50m // Invalid amount that will trigger BusinessValidationException
        };

        // Act - with exception
        var exceptionResult = await _workflowEngine.ExecuteWorkflowAsync(definition, exceptionData);

        // Assert - with exception
        Assert.True(exceptionResult.IsSuccess);
        Assert.NotNull(exceptionResult.WorkflowData);

        var exceptionFinalData = (ExceptionTestData)exceptionResult.WorkflowData;

        // Verify exception was caught and finally block executed
        Assert.True(exceptionFinalData.ErrorHandled);
        Assert.Contains("Invalid order amount", exceptionFinalData.ErrorMessage);
        Assert.True(exceptionFinalData.FinallyExecuted);

        // Test without exception
        var successData = new ExceptionTestData
        {
            OrderId = "ORDER-FINALLY-002",
            Amount = 50m // Valid amount, no exception should be thrown
        };

        // Act - without exception
        var successResult = await _workflowEngine.ExecuteWorkflowAsync(definition, successData);

        // Assert - without exception
        Assert.True(successResult.IsSuccess);
        Assert.NotNull(successResult.WorkflowData);

        var successFinalData = (ExceptionTestData)successResult.WorkflowData;

        // Verify successful execution and finally block executed
        Assert.False(successFinalData.ErrorHandled);
        Assert.Empty(successFinalData.ErrorMessage);
        Assert.True(successFinalData.FinallyExecuted);
    }

    [Fact]
    public async Task TestNestedTryCatch()
    {
        // Arrange
        var definition = Workflow
            .Create<ExceptionTestData>("NestedTryCatchWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InitialStep")
                .Input(act => act.StepNumber).From(ctx => 0))
            .Try(outerTryBuilder => outerTryBuilder
                .Try(innerTryBuilder => innerTryBuilder
                    .Step<ProcessPaymentWithExceptionActivity>(setup => setup
                        .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                        .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                        .Output(act => act.PaymentProcessed).To(ctx => ctx.WorkflowData.PaymentProcessed)))
                .Catch<BusinessValidationException>(innerCatchBuilder => innerCatchBuilder
                    .Step<LogErrorActivity>(setup => setup
                        .Input(act => act.Message).From(ctx => "Inner catch: " + ctx.Exception.Message)
                        .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Output(act => act.Logged).To(ctx => ctx.WorkflowData.ErrorHandled)
                        .Output(act => act.Message).To(ctx => ctx.WorkflowData.ErrorMessage))))
            .Catch<PaymentProcessingException>(outerCatchBuilder => outerCatchBuilder
                .Step<HandlePaymentErrorActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.ErrorMessage).From(ctx => "Outer catch: " + ctx.Exception.Message)
                    .Input(act => act.RefundAmount).From(ctx => ctx.Exception.PaymentAmount)
                    .Output(act => act.Handled).To(ctx => ctx.WorkflowData.ErrorHandled)
                    .Output(act => act.RefundProcessed).To(ctx => ctx.WorkflowData.RefundProcessed)
                    .Output(act => act.ErrorMessage).To(ctx => ctx.WorkflowData.ErrorMessage)))
            .Build();

        var workflowData = new ExceptionTestData
        {
            OrderId = "ORDER-NESTED-001",
            Amount = 200m // Amount that will trigger PaymentProcessingException
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ExceptionTestData)result.WorkflowData;

        // Verify outer catch handled the exception
        Assert.True(finalData.ErrorHandled);
        Assert.Contains("Outer catch", finalData.ErrorMessage);
        Assert.True(finalData.RefundProcessed);
    }

    [Fact]
    public async Task TestCatchAll()
    {
        // Arrange - using generic Exception catch block
        var definition = Workflow
            .Create<ExceptionTestData>("CatchAllWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InitialStep")
                .Input(act => act.StepNumber).From(ctx => 0))
            .Try(tryBuilder => tryBuilder
                .Step<ProcessPaymentWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                    .Output(act => act.PaymentProcessed).To(ctx => ctx.WorkflowData.PaymentProcessed)))
            .Catch<Exception>(catchBuilder => catchBuilder // Generic catch-all
                .Step<LogErrorActivity>(setup => setup
                    .Input(act => act.Message).From(ctx => "Catch-all: " + ctx.Exception.Message)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.Logged).To(ctx => ctx.WorkflowData.ErrorHandled)
                    .Output(act => act.Message).To(ctx => ctx.WorkflowData.ErrorMessage)))
            .Build();

        var workflowData = new ExceptionTestData
        {
            OrderId = "ORDER-CATCHALL-001",
            Amount = 200m // Amount that will trigger PaymentProcessingException
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ExceptionTestData)result.WorkflowData;

        // Verify catch-all handled the exception
        Assert.True(finalData.ErrorHandled);
        Assert.Contains("Catch-all", finalData.ErrorMessage);
        Assert.Contains("Payment processing failed", finalData.ErrorMessage);
    }

    [Fact]
    public async Task TestExceptionContext()
    {
        // Arrange - test accessing exception properties in catch block
        var definition = Workflow
            .Create<ExceptionTestData>("ExceptionContextWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InitialStep")
                .Input(act => act.StepNumber).From(ctx => 0))
            .Try(tryBuilder => tryBuilder
                .Step<ProcessPaymentWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                    .Output(act => act.PaymentProcessed).To(ctx => ctx.WorkflowData.PaymentProcessed)))
            .Catch<PaymentProcessingException>(catchBuilder => catchBuilder
                .Step<HandlePaymentErrorActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                    .Input(act => act.RefundAmount).From(ctx => ctx.Exception.PaymentAmount) // Access specific property
                    .Output(act => act.Handled).To(ctx => ctx.WorkflowData.ErrorHandled)
                    .Output(act => act.RefundProcessed).To(ctx => ctx.WorkflowData.RefundProcessed)
                    .Output(act => act.RefundAmount).To(ctx => ctx.WorkflowData.RefundAmount) // Store the amount
                    .Output(act => act.ErrorMessage).To(ctx => ctx.WorkflowData.ErrorMessage)))
            .Build();

        var workflowData = new ExceptionTestData
        {
            OrderId = "ORDER-CONTEXT-001",
            Amount = 175m // Amount that will trigger PaymentProcessingException
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ExceptionTestData)result.WorkflowData;

        // Verify exception context was accessed correctly
        Assert.True(finalData.ErrorHandled);
        Assert.Contains("Payment processing failed", finalData.ErrorMessage);
        Assert.Equal(175m, finalData.RefundAmount); // Verify the specific property was accessed
        Assert.True(finalData.RefundProcessed);
    }

    [Fact]
    public async Task TestTryFinallyWithoutCatch()
    {
        // Arrange - try/finally without catch blocks
        var definition = Workflow
            .Create<ExceptionTestData>("TryFinallyWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InitialStep")
                .Input(act => act.StepNumber).From(ctx => 0))
            .Try(tryBuilder => tryBuilder
                .Step<ValidateOrderWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult)))
            .Finally(finallyBuilder => finallyBuilder
                .Step<CleanupActivity>(setup => setup
                    .Input(act => act.WorkflowId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.CleanupResult).To(ctx => ctx.WorkflowData.FinallyExecuted)))
            .Build();

        // Test with valid data (no exception)
        var validData = new ExceptionTestData
        {
            OrderId = "ORDER-TRYFINALLY-001",
            Amount = 50m // Valid amount, no exception
        };

        // Act - with valid data
        var validResult = await _workflowEngine.ExecuteWorkflowAsync(definition, validData);

        // Assert - with valid data
        Assert.True(validResult.IsSuccess);
        Assert.NotNull(validResult.WorkflowData);

        var validFinalData = (ExceptionTestData)validResult.WorkflowData;

        // Verify finally block executed with successful try
        Assert.True(validFinalData.ValidationResult);
        Assert.True(validFinalData.FinallyExecuted);

        // Test with invalid data (will throw exception)
        var invalidData = new ExceptionTestData
        {
            OrderId = "ORDER-TRYFINALLY-002",
            Amount = -50m // Invalid amount, will throw exception
        };

        // Act - with invalid data
        var invalidResult = await _workflowEngine.ExecuteWorkflowAsync(definition, invalidData);

        // Assert - with invalid data
        Assert.False(invalidResult.IsSuccess); // Workflow should fail because exception is not caught
        Assert.NotNull(invalidResult.WorkflowData);

        var invalidFinalData = (ExceptionTestData)invalidResult.WorkflowData;

        // Verify finally block executed even though try threw uncaught exception
        Assert.True(invalidFinalData.FinallyExecuted);
    }

    [Fact]
    public async Task TestExceptionInCatchBlock()
    {
        // Arrange - test exception thrown in catch block
        var definition = Workflow
            .Create<ExceptionTestData>("ExceptionInCatchWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InitialStep")
                .Input(act => act.StepNumber).From(ctx => 0))
            .Try(tryBuilder => tryBuilder
                .Step<ValidateOrderWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult)))
            .Catch<BusinessValidationException>(catchBuilder => catchBuilder
                // This will throw a PaymentProcessingException when Amount >= 150
                .Step<ProcessPaymentWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => 200m) // Force exception
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                    .Output(act => act.PaymentProcessed).To(ctx => ctx.WorkflowData.PaymentProcessed)))
            .Catch<PaymentProcessingException>(catchBuilder => catchBuilder
                .Step<HandlePaymentErrorActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.ErrorMessage).From(ctx => "Outer catch: " + ctx.Exception.Message)
                    .Input(act => act.RefundAmount).From(ctx => ctx.Exception.PaymentAmount)
                    .Output(act => act.Handled).To(ctx => ctx.WorkflowData.ErrorHandled)
                    .Output(act => act.RefundProcessed).To(ctx => ctx.WorkflowData.RefundProcessed)
                    .Output(act => act.RefundAmount).To(ctx => ctx.WorkflowData.RefundAmount) // Add this line
                    .Output(act => act.ErrorMessage).To(ctx => ctx.WorkflowData.ErrorMessage)))
            .Finally(finallyBuilder => finallyBuilder
                .Step<CleanupActivity>(setup => setup
                    .Input(act => act.WorkflowId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.CleanupResult).To(ctx => ctx.WorkflowData.FinallyExecuted)))
            .Build();

        var workflowData = new ExceptionTestData
        {
            OrderId = "ORDER-EXCEPTION-IN-CATCH-001",
            Amount = -50m // Will trigger BusinessValidationException first
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ExceptionTestData)result.WorkflowData;

        // Verify the exception in catch block was caught by outer catch
        Assert.True(finalData.ErrorHandled);
        Assert.Contains("Outer catch", finalData.ErrorMessage);
        Assert.Contains("Payment processing failed", finalData.ErrorMessage);
        Assert.True(finalData.RefundProcessed);
        Assert.Equal(200m, finalData.RefundAmount);
        Assert.True(finalData.FinallyExecuted);
    }

    [Fact]
    public async Task TestExceptionInFinallyBlock()
    {
        // Arrange - test exception thrown in finally block
        var definition = Workflow
            .Create<ExceptionTestData>("ExceptionInFinallyWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InitialStep")
                .Input(act => act.StepNumber).From(ctx => 0))
            .Try(tryBuilder => tryBuilder
                .Step<ValidateOrderWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult)))
            .Catch<BusinessValidationException>(catchBuilder => catchBuilder
                .Step<LogErrorActivity>(setup => setup
                    .Input(act => act.Message).From(ctx => ctx.Exception.Message)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.Logged).To(ctx => ctx.WorkflowData.ErrorHandled)
                    .Output(act => act.Message).To(ctx => ctx.WorkflowData.ErrorMessage)))
            .Finally(finallyBuilder => finallyBuilder
                .Step<ThrowingCleanupActivity>(setup => setup
                    .Input(act => act.WorkflowId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.ShouldThrow).From(ctx => true) // Force exception
                    .Output(act => act.CleanupResult).To(ctx => ctx.WorkflowData.FinallyExecuted)))
            .Build();

        var workflowData = new ExceptionTestData
        {
            OrderId = "ORDER-EXCEPTION-IN-FINALLY-001",
            Amount = -50m // Will trigger BusinessValidationException
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.False(result.IsSuccess); // Workflow should fail because exception in finally is not caught
        Assert.NotNull(result.WorkflowData);

        var finalData = (ExceptionTestData)result.WorkflowData;

        // Verify the first exception was caught
        Assert.True(finalData.ErrorHandled);
        Assert.Contains("Invalid order amount", finalData.ErrorMessage);

        // But finally block exception caused workflow failure
        Assert.False(finalData.FinallyExecuted);
        Assert.Contains("CriticalSystemException", result.ErrorMessage);
        Assert.Contains("Exception thrown in finally block", result.ErrorMessage);
    }

    [Fact]
    public async Task TestCatchPrecedence()
    {
        // Arrange - test catch block precedence (most specific to least specific)
        var definition = Workflow
            .Create<ExceptionTestData>("CatchPrecedenceWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InitialStep")
                .Input(act => act.StepNumber).From(ctx => 0))
            .Try(tryBuilder => tryBuilder
                .Step<ProcessPaymentWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                    .Output(act => act.PaymentProcessed).To(ctx => ctx.WorkflowData.PaymentProcessed)))
            .Catch<PaymentProcessingException>(catchBuilder => catchBuilder
                .Step<HandlePaymentErrorActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.ErrorMessage).From(ctx => "Specific catch: " + ctx.Exception.Message)
                    .Input(act => act.RefundAmount).From(ctx => ctx.Exception.PaymentAmount)
                    .Output(act => act.Handled).To(ctx => ctx.WorkflowData.ErrorHandled)
                    .Output(act => act.RefundProcessed).To(ctx => ctx.WorkflowData.RefundProcessed)
                    .Output(act => act.ErrorMessage).To(ctx => ctx.WorkflowData.ErrorMessage)))
            .Catch<Exception>(catchBuilder => catchBuilder // Generic catch-all
                .Step<LogErrorActivity>(setup => setup
                    .Input(act => act.Message).From(ctx => "Generic catch: " + ctx.Exception.Message)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.Logged).To(ctx => ctx.WorkflowData.ErrorHandled)
                    .Output(act => act.Message).To(ctx => ctx.WorkflowData.ErrorMessage)))
            .Build();

        var workflowData = new ExceptionTestData
        {
            OrderId = "ORDER-PRECEDENCE-001",
            Amount = 200m // Will trigger PaymentProcessingException
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ExceptionTestData)result.WorkflowData;

        // Verify the specific catch block was used, not the generic one
        Assert.True(finalData.ErrorHandled);
        Assert.Contains("Specific catch", finalData.ErrorMessage);
        Assert.DoesNotContain("Generic catch", finalData.ErrorMessage);
        Assert.True(finalData.RefundProcessed);
    }

    [Fact]
    public async Task TestUnhandledException()
    {
        // Arrange - test unhandled exception (no matching catch block)
        var definition = Workflow
            .Create<ExceptionTestData>("UnhandledExceptionWorkflow")
            .Step<StepExecutionAsyncActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InitialStep")
                .Input(act => act.StepNumber).From(ctx => 0))
            .Try(tryBuilder => tryBuilder
                .Step<ProcessPaymentWithExceptionActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                    .Output(act => act.PaymentProcessed).To(ctx => ctx.WorkflowData.PaymentProcessed)))
            .Catch<BusinessValidationException>(catchBuilder => catchBuilder // Won't match PaymentProcessingException
                .Step<LogErrorActivity>(setup => setup
                    .Input(act => act.Message).From(ctx => "Business validation error: " + ctx.Exception.Message)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.Logged).To(ctx => ctx.WorkflowData.ErrorHandled)
                    .Output(act => act.Message).To(ctx => ctx.WorkflowData.ErrorMessage)))
            .Finally(finallyBuilder => finallyBuilder
                .Step<CleanupActivity>(setup => setup
                    .Input(act => act.WorkflowId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.CleanupResult).To(ctx => ctx.WorkflowData.FinallyExecuted)))
            .Build();

        var workflowData = new ExceptionTestData
        {
            OrderId = "ORDER-UNHANDLED-001",
            Amount = 200m // Will trigger PaymentProcessingException, not BusinessValidationException
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.False(result.IsSuccess); // Workflow should fail because exception is not caught
        Assert.NotNull(result.WorkflowData);

        var finalData = (ExceptionTestData)result.WorkflowData;

        // Verify the exception was not caught by the wrong catch block
        Assert.False(finalData.ErrorHandled);
        Assert.Empty(finalData.ErrorMessage);

        // But finally block still executed
        Assert.True(finalData.FinallyExecuted);

        // And error is in the workflow result
        Assert.Contains("PaymentProcessingException", result.ErrorMessage);
        Assert.Contains("Payment processing failed", result.ErrorMessage);
    }

    // Custom activity that throws an exception in finally
    public class ThrowingCleanupActivity : IAsyncActivity
    {
        public string WorkflowId { get; set; } = string.Empty;
        public bool ShouldThrow { get; set; }

        public bool CleanupResult { get; set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            if (ShouldThrow) throw new CriticalSystemException("Exception thrown in finally block");

            CleanupResult = true;
            await Task.CompletedTask;
        }
    }
}
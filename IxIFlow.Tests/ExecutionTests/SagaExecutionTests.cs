using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.ExecutionTests.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using TimeoutException = IxIFlow.Tests.ExecutionTests.Models.TimeoutException;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
///     Tests for saga pattern execution
/// </summary>
public class SagaExecutionTests
{
    private readonly ServiceProvider _serviceProvider;
    private readonly WorkflowEngine _workflowEngine;
    private readonly ITestOutputHelper _output;

    public SagaExecutionTests(ITestOutputHelper output)
    {
        _output = output;
        
        var services = new ServiceCollection();

        // Register interfaces for WorkflowEngine dependencies
        services.AddSingleton<IActivityExecutor, ActivityExecutor>();
        services.AddSingleton<IExpressionEvaluator, ExpressionEvaluator>();
        services.AddSingleton<ISuspendResumeExecutor, SuspendResumeExecutor>();
        services.AddSingleton<ISagaExecutor, SagaExecutor>();
        services.AddSingleton<IWorkflowVersionRegistry, WorkflowVersionRegistry>();
        services.AddSingleton<IEventCorrelator, EventCorrelator>();
        services.AddSingleton<IWorkflowTracer, WorkflowTracer>();

        // Register mock implementations for persistence dependencies
        services.AddSingleton<IWorkflowStateRepository, MockWorkflowStateRepository>();
        services.AddSingleton<IEventStore, MockEventStore>();
        services.AddSingleton<IWorkflowInvoker, WorkflowInvoker>();

        // Register the main engine
        services.AddSingleton<WorkflowEngine>();

        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();
        _workflowEngine = _serviceProvider.GetRequiredService<WorkflowEngine>();
    }

    [Fact]
    public async Task BasicSagaExecution_AllStepsSucceed_NoCompensationNeeded()
    {
        // Arrange - Create saga workflow with 3 steps that all succeed
        var workflowData = new SagaTestData
        {
            OrderId = "ORDER-001",
            Amount = 100m,
            ProductId = "PRODUCT-001",
            PaymentMethod = "CreditCard"
        };

        var workflow = Workflow
            .Create<SagaTestData>("BasicSagaTest")
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            { saga
                .Step<SagaReserveInventoryAsyncActivity>(setup => setup
                        .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                        .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)
                        .Output(act => act.Step1Completed).To(ctx => ctx.WorkflowData.Step1Completed)
                        .CompensateWith<ReleaseInventoryCompensationAsyncActivity>(comp =>
                        {
                            comp.Input(act => act.ReservationId).From(ctx => ctx.WorkflowData.ReservationId);
                            comp.Output(act => act.Step1Compensated).To(ctx => ctx.WorkflowData.Step1Compensated);
                        }))
                .Step<SagaCalculatePricingAsyncActivity>(setup => setup
                                .Input(act => act.ReservationId).From(ctx => ctx.PreviousStep.ReservationId)
                                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                                .Output(act => act.FinalPrice).To(ctx => ctx.WorkflowData.FinalPrice)
                                .Output(act => act.Step2Completed).To(ctx => ctx.WorkflowData.Step2Completed)
                                .CompensateWith<RevertPricingCompensationAsyncActivity>(comp =>
                                {
                                    comp.Input(act => act.FinalPrice).From(ctx => ctx.CurrentStep.FinalPrice);
                                    comp.Output(act => act.Step2Compensated).To(ctx => ctx.WorkflowData.Step2Compensated);
                                }))
                .Step<SagaProcessPaymentAsyncActivity>(setup => setup
                        .Input(act => act.FinalPrice).From(ctx => ctx.PreviousStep.FinalPrice)
                        .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                        .Input(act => act.ShouldFail).From(ctx => false) // Don't fail - happy path
                        .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                        .Output(act => act.Step3Completed).To(ctx => ctx.WorkflowData.Step3Completed)
                        .CompensateWith<RefundPaymentCompensationAsyncActivity>(comp =>
                        {
                            comp.Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.TransactionId);
                            comp.Input(act => act.FinalPrice).From(ctx => ctx.WorkflowData.FinalPrice);
                            comp.Output(act => act.Step3Compensated).To(ctx => ctx.WorkflowData.Step3Compensated);
                        }));
            })
            .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Success).From(ctx => ctx.WorkflowData.ValidationResult)
                .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsUpdated))
            .Build();

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(workflowData.Step1Completed);
        Assert.True(workflowData.Step2Completed);
        Assert.True(workflowData.Step3Completed);

        // No compensation should have occurred since all steps succeeded
        Assert.False(workflowData.Step1Compensated);
        Assert.False(workflowData.Step2Compensated);
        Assert.False(workflowData.Step3Compensated);

        // Verify step outputs
        Assert.NotEmpty(workflowData.ReservationId);
        Assert.True(workflowData.FinalPrice > 0);
        Assert.NotEmpty(workflowData.TransactionId);
        Assert.True(workflowData.ValidationResult);
        Assert.True(workflowData.AnalyticsUpdated);
    }

    [Fact]
    public async Task SagaExecution_CompensateAndTerminate_FullCompensationAndContinueWorkflow()
    {
        // Arrange - Payment step will fail, should compensate all and terminate
        var workflowData = new SagaTestData
        {
            OrderId = "ORDER-002",
            Amount = 100m,
            ProductId = "PRODUCT-002",
            PaymentMethod = "CreditCard"
        };

        var workflow = Workflow
            .Create<SagaTestData>("CompensateAndTerminateSagaTest")
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Try(tryBlock =>
            {
                tryBlock.Saga(saga =>
                    {
                        saga.Step<SagaReserveInventoryAsyncActivity>(setup => setup
                                .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                                .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)
                                .Output(act => act.Step1Completed).To(ctx => ctx.WorkflowData.Step1Completed)
                                .CompensateWith<ReleaseInventoryCompensationAsyncActivity>(comp =>
                                {
                                    comp.Input(act => act.ReservationId).From(ctx => ctx.WorkflowData.ReservationId);
                                    comp.Output(act => act.Step1Compensated)
                                        .To(ctx => ctx.WorkflowData.Step1Compensated);
                                }))
                            .Step<SagaCalculatePricingAsyncActivity>(setup => setup
                                .Input(act => act.ReservationId).From(ctx => ctx.PreviousStep.ReservationId)
                                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                                .Output(act => act.FinalPrice).To(ctx => ctx.WorkflowData.FinalPrice)
                                .Output(act => act.Step2Completed).To(ctx => ctx.WorkflowData.Step2Completed)
                                .CompensateWith<RevertPricingCompensationAsyncActivity>(comp =>
                                {
                                    comp.Input(act => act.FinalPrice).From(ctx => ctx.WorkflowData.FinalPrice);
                                    comp.Output(act => act.Step2Compensated)
                                        .To(ctx => ctx.WorkflowData.Step2Compensated);
                                }))
                            .Step<SagaProcessPaymentAsyncActivity>(setup => setup
                                .Input(act => act.FinalPrice).From(ctx => ctx.PreviousStep.FinalPrice)
                                .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                                .Input(act => act.ShouldFail).From(ctx => true) // FAIL to test compensation
                                .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                                .Output(act => act.Step3Completed).To(ctx => ctx.WorkflowData.Step3Completed)
                                .CompensateWith<RefundPaymentCompensationAsyncActivity>(comp =>
                                {
                                    comp.Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.TransactionId);
                                    comp.Input(act => act.FinalPrice).From(ctx => ctx.WorkflowData.FinalPrice);
                                    comp.Output(act => act.Step3Compensated)
                                        .To(ctx => ctx.WorkflowData.Step3Compensated);
                                }));
                    })
                    .OnError<PaymentProcessingException>(error =>
                        error.Compensate().ThenTerminate()); // NEW API: Compensate all and terminate
            })
            .Catch<SagaTerminatedException>(catchBlock =>
            {
                catchBlock.Step<HandleSagaFailureAsyncActivity>(setup => setup
                    .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                    .Output(act => act.FailureHandled).To(ctx => ctx.WorkflowData.ErrorHandlerExecuted));
            })
            .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Success).From(ctx => false) // Saga failed
                .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsUpdated))
            .Build();

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert
        Assert.True(result.IsSuccess); // Workflow succeeded (caught SagaTerminatedException)
        Assert.True(workflowData.Step1Completed);
        Assert.True(workflowData.Step2Completed);
        Assert.False(workflowData.Step3Completed); // Payment failed

        // Compensation should have occurred for successful steps
        Assert.True(workflowData.Step1Compensated);
        Assert.True(workflowData.Step2Compensated);
        Assert.False(workflowData.Step3Compensated); // No transaction to compensate

        // Error handler should have executed
        Assert.True(workflowData.ErrorHandlerExecuted);

        // Analytics step SHOULD have executed (workflow continued after catch block)
        Assert.True(workflowData.AnalyticsUpdated);
    }

    [Fact]
    public async Task SagaExecution_CompensateNoneAndContinue_NoCompensationButWorkflowContinues()
    {
        // Arrange - Payment step will fail, should not compensate but continue workflow
        var workflowData = new SagaTestData
        {
            OrderId = "ORDER-003",
            Amount = 100m,
            ProductId = "PRODUCT-003",
            PaymentMethod = "CreditCard"
        };

        var workflow = Workflow
            .Create<SagaTestData>("CompensateNoneAndContinueSagaTest")
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            {
                saga.Step<SagaReserveInventoryAsyncActivity>(setup => setup
                        .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                        .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)
                        .Output(act => act.Step1Completed).To(ctx => ctx.WorkflowData.Step1Completed)
                        .CompensateWith<ReleaseInventoryCompensationAsyncActivity>(comp =>
                        {
                            comp.Input(act => act.ReservationId).From(ctx => ctx.WorkflowData.ReservationId);
                            comp.Output(act => act.Step1Compensated).To(ctx => ctx.WorkflowData.Step1Compensated);
                        }))
                    .Step<SagaCalculatePricingAsyncActivity>(setup => setup
                        .Input(act => act.ReservationId).From(ctx => ctx.PreviousStep.ReservationId)
                        .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                        .Output(act => act.FinalPrice).To(ctx => ctx.WorkflowData.FinalPrice)
                        .Output(act => act.Step2Completed).To(ctx => ctx.WorkflowData.Step2Completed)
                        .CompensateWith<RevertPricingCompensationAsyncActivity>(comp =>
                        {
                            comp.Input(act => act.FinalPrice).From(ctx => ctx.WorkflowData.FinalPrice);
                            comp.Output(act => act.Step2Compensated).To(ctx => ctx.WorkflowData.Step2Compensated);
                        }))
                    .Step<SagaProcessPaymentAsyncActivity>(setup => setup
                        .Input(act => act.FinalPrice).From(ctx => ctx.PreviousStep.FinalPrice)
                        .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                        .Input(act => act.ShouldFail).From(ctx => true) // FAIL to test no compensation
                        .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                        .Output(act => act.Step3Completed).To(ctx => ctx.WorkflowData.Step3Completed)
                        .CompensateWith<RefundPaymentCompensationAsyncActivity>(comp =>
                        {
                            comp.Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.TransactionId);
                            comp.Input(act => act.FinalPrice).From(ctx => ctx.WorkflowData.FinalPrice);
                            comp.Output(act => act.Step3Compensated).To(ctx => ctx.WorkflowData.Step3Compensated);
                        }));
            })
            .OnError<PaymentProcessingException>(error =>
                error.CompensateNone().ThenContinue()) // NEW API: No compensation, continue workflow
            .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Success).From(ctx => false) // Saga failed but continued
                .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsUpdated))
            .Build();

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert
        Assert.True(result.IsSuccess); // Workflow continued despite saga failure
        Assert.True(workflowData.Step1Completed);
        Assert.True(workflowData.Step2Completed);
        Assert.False(workflowData.Step3Completed); // Payment failed

        // NO compensation should have occurred
        Assert.False(workflowData.Step1Compensated);
        Assert.False(workflowData.Step2Compensated);
        Assert.False(workflowData.Step3Compensated);

        // Workflow should have continued to analytics step
        Assert.True(workflowData.AnalyticsUpdated);
    }

    [Fact]
    public async Task SagaExecution_CompensateAndRetry_CompensationThenRetryFailedSaga()
    {
        // Arrange - Payment will fail first time, then succeed on retry
        var workflowData = new SagaTestData
        {
            OrderId = "ORDER-004",
            Amount = 100m,
            ProductId = "PRODUCT-004",
            PaymentMethod = "CreditCard"
        };

        var workflow = Workflow
            .Create<SagaTestData>("CompensateAndRetrySagaTest")
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            {
                saga.Step<SagaReserveInventoryAsyncActivity>(setup => setup
                        .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                        .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)
                        .Output(act => act.Step1Completed).To(ctx => ctx.WorkflowData.Step1Completed)
                        .CompensateWith<ReleaseInventoryCompensationAsyncActivity>(comp =>
                        {
                            comp.Input(act => act.ReservationId).From(ctx => ctx.WorkflowData.ReservationId);
                            comp.Output(act => act.Step1Compensated).To(ctx => ctx.WorkflowData.Step1Compensated);
                        }))
                    .Step<SagaCalculatePricingAsyncActivity>(setup => setup
                        .Input(act => act.ReservationId).From(ctx => ctx.PreviousStep.ReservationId)
                        .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                        .Output(act => act.FinalPrice).To(ctx => ctx.WorkflowData.FinalPrice)
                        .Output(act => act.Step2Completed).To(ctx => ctx.WorkflowData.Step2Completed)
                        .CompensateWith<RevertPricingCompensationAsyncActivity>(comp =>
                        {
                            comp.Input(act => act.FinalPrice).From(ctx => ctx.WorkflowData.FinalPrice);
                            comp.Output(act => act.Step2Compensated).To(ctx => ctx.WorkflowData.Step2Compensated);
                        }))
                    .Step<SagaRetryablePaymentAsyncActivity>(setup => setup // Special retryable payment
                        .Input(act => act.FinalPrice).From(ctx => ctx.PreviousStep.FinalPrice)
                        .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                        .Input(act => act.RetryAttempts).From(ctx => ctx.WorkflowData.RetryAttempts)
                        .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                        .Output(act => act.Step3Completed).To(ctx => ctx.WorkflowData.Step3Completed)
                        .Output(act => act.RetryAttempts).To(ctx => ctx.WorkflowData.RetryAttempts)
                        .CompensateWith<RefundPaymentCompensationAsyncActivity>(comp =>
                        {
                            comp.Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.TransactionId);
                            comp.Input(act => act.FinalPrice).From(ctx => ctx.WorkflowData.FinalPrice);
                            comp.Output(act => act.Step3Compensated).To(ctx => ctx.WorkflowData.Step3Compensated);
                        }));
            })
            .OnError<PaymentProcessingException>(error =>
                error.Compensate().ThenRetry(2)) // NEW API: Compensate then retry up to 2 times
            .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Success).From(ctx => ctx.WorkflowData.Step3Completed)
                .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsUpdated))
            .Build();

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.True(workflowData.Step1Completed);
        Assert.True(workflowData.Step2Completed);
        Assert.True(workflowData.Step3Completed); // Should succeed on retry

        // Retry attempts should be > 0 (indicating retries occurred)
        Assert.True(workflowData.RetryAttempts > 0);

        // Workflow should complete successfully
        Assert.True(workflowData.AnalyticsUpdated);
        Assert.NotEmpty(workflowData.TransactionId);
    }

    [Fact]
    public async Task SagaExecution_DefaultBehavior_CompensateAndTerminateWhenNoExplicitHandler()
    {
        // Arrange - Custom exception with no explicit handler should trigger default behavior
        var workflowData = new SagaTestData
        {
            OrderId = "ORDER-005",
            Amount = 100m,
            ProductId = "PRODUCT-005",
            PaymentMethod = "CreditCard"
        };

        var workflow = Workflow
            .Create<SagaTestData>("DefaultBehaviorSagaTest")
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Try(tryBlock =>
            {
                tryBlock.Saga(saga =>
                {
                    saga.Step<SagaReserveInventoryAsyncActivity>(setup => setup
                            .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                            .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)
                            .Output(act => act.Step1Completed).To(ctx => ctx.WorkflowData.Step1Completed)
                            .CompensateWith<ReleaseInventoryCompensationAsyncActivity>(comp =>
                            {
                                comp.Input(act => act.ReservationId).From(ctx => ctx.CurrentStep.ProductId.ToString()); //just to test access to current step
                                comp.Input(act => act.ReservationId).From(ctx => ctx.WorkflowData.ReservationId);
                                comp.Output(act => act.Step1Compensated).To(ctx => ctx.WorkflowData.Step1Compensated);
                            }))
                        .Step<SagaThrowCustomExceptionAsyncActivity>(setup => setup // Throws custom exception
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                            .Output(act => act.Step2Completed).To(ctx => ctx.WorkflowData.Step2Completed));
                });
                // NO explicit error handler - should use default behavior (Compensate + Terminate)
            })
            .Catch<SagaTerminatedException>(catchBlock =>
            {
                catchBlock.Step<HandleSagaFailureAsyncActivity>(setup => setup
                    .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                    .Output(act => act.FailureHandled).To(ctx => ctx.WorkflowData.ErrorHandlerExecuted));
            })
            .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Success).From(ctx => false)
                .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsUpdated))
            .Build();

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert
        Assert.True(result.IsSuccess); // Workflow succeeded (caught SagaTerminatedException)
        Assert.True(workflowData.Step1Completed);
        Assert.False(workflowData.Step2Completed); // Custom exception step failed

        // Default behavior should have compensated all successful steps
        Assert.True(workflowData.Step1Compensated);
        Assert.False(workflowData.Step2Compensated); // Step didn't complete

        // Error handler should have executed
        Assert.True(workflowData.ErrorHandlerExecuted);

        // Analytics step SHOULD have executed (workflow continued after catch block)
        Assert.True(workflowData.AnalyticsUpdated);
    }

    [Fact]
    public async Task SagaExecution_CompensateUpToSpecificStep_PartialCompensation()
    {
        // Arrange - Test CompensateUpTo specific step behavior (INCLUSIVE semantics)
        // This test demonstrates that CompensateUpTo<Step1> should compensate steps 2,1 (INCLUSIVE) but not step 3 (failed)
        var workflowData = new SagaTestData
        {
            OrderId = "ORDER-006",
            Amount = 100m,
            ProductId = "PRODUCT-006",
            PaymentMethod = "CreditCard"
        };

        var workflow = Workflow
            .Create<SagaTestData>("CompensateUpToSagaTest")
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Try(tryBlock =>
            {
                tryBlock.Saga(saga =>
                    {
                        // Step1: ReserveInventory (CompensateUpTo target - INCLUSIVE)
                        saga.Step<SagaReserveInventoryAsyncActivity>(setup => setup
                                .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                                .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)
                                .Output(act => act.Step1Completed).To(ctx => ctx.WorkflowData.Step1Completed)
                                .CompensateWith<ReleaseInventoryCompensationAsyncActivity>(comp =>
                                {
                                    comp.Input(act => act.ReservationId).From(ctx => ctx.WorkflowData.ReservationId);
                                    comp.Output(act => act.Step1Compensated)
                                        .To(ctx => ctx.WorkflowData.Step1Compensated);
                                }))
                            // Step2: CalculatePricing (should be compensated - after target)
                            .Step<SagaCalculatePricingAsyncActivity>(setup => setup
                                .Input(act => act.ReservationId).From(ctx => ctx.PreviousStep.ReservationId)
                                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                                .Output(act => act.FinalPrice).To(ctx => ctx.WorkflowData.FinalPrice)
                                .Output(act => act.Step2Completed).To(ctx => ctx.WorkflowData.Step2Completed)
                                .CompensateWith<RevertPricingCompensationAsyncActivity>(comp =>
                                {
                                    comp.Input(act => act.FinalPrice).From(ctx => ctx.WorkflowData.FinalPrice);
                                    comp.Output(act => act.Step2Compensated)
                                        .To(ctx => ctx.WorkflowData.Step2Compensated);
                                }))
                            // Step3: ProcessPayment (FAILS - should not be compensated)
                            .Step<SagaProcessPaymentAsyncActivity>(setup => setup
                                .Input(act => act.FinalPrice).From(ctx => ctx.PreviousStep.FinalPrice)
                                .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                                .Input(act => act.ShouldFail).From(ctx => true) // FAIL to test partial compensation
                                .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                                .Output(act => act.Step3Completed).To(ctx => ctx.WorkflowData.Step3Completed)
                                .CompensateWith<RefundPaymentCompensationAsyncActivity>(comp =>
                                {
                                    comp.Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.TransactionId);
                                    comp.Input(act => act.FinalPrice).From(ctx => ctx.WorkflowData.FinalPrice);
                                    comp.Output(act => act.Step3Compensated)
                                        .To(ctx => ctx.WorkflowData.Step3Compensated);
                                }));
                    })
                    .OnError<PaymentProcessingException>(error =>
                        error.CompensateUpTo<SagaReserveInventoryAsyncActivity>()
                            .ThenTerminate()); // CompensateUpTo Step1 (INCLUSIVE) - should compensate Step2,Step1 only
            })
            .Catch<SagaTerminatedException>(catchBlock =>
            {
                catchBlock.Step<HandleSagaFailureAsyncActivity>(setup => setup
                    .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                    .Output(act => act.FailureHandled).To(ctx => ctx.WorkflowData.ErrorHandlerExecuted));
            })
            .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Success).From(ctx => false)
                .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsUpdated))
            .Build();

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert
        Assert.True(result.IsSuccess); // Workflow succeeded (caught SagaTerminatedException)
        Assert.True(workflowData.Step1Completed); // Step1 reserve inventory succeeded  
        Assert.True(workflowData.Step2Completed); // Step2 calculate pricing succeeded
        Assert.False(workflowData.Step3Completed); // Step3 payment failed

        // CORRECT EXPECTATIONS based on INCLUSIVE CompensateUpTo<Step1> semantics:
        // CompensateUpTo<Step1> should compensate: Step2, Step1 (INCLUSIVE target)
        Assert.True(workflowData.Step1Compensated);  // Should be compensated (INCLUSIVE target step)
        Assert.True(workflowData.Step2Compensated);  // Should be compensated (after target step, successful)
        Assert.False(workflowData.Step3Compensated); // Should NOT be compensated (failed step)

        // Error handler should have executed
        Assert.True(workflowData.ErrorHandlerExecuted);

        // Analytics step SHOULD have executed (workflow continued after catch block)
        Assert.True(workflowData.AnalyticsUpdated);
    }

    [Fact]
    public async Task SagaExecution_StepLevelErrorHandling_RetryIgnoreTerminate()
    {
        // Arrange - Test step-level error handling behaviors
        var workflowData = new SagaTestData
        {
            OrderId = "ORDER-007",
            Amount = 100m,
            ProductId = "PRODUCT-007",
            PaymentMethod = "CreditCard"
        };

        var workflow = Workflow
            .Create<SagaTestData>("StepLevelErrorHandlingSagaTest")
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            {
                saga.Step<SagaStepWithMultipleErrorsAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.TestScenario).From(ctx => "timeout") // Will throw timeout exception
                    .Output(act => act.Step1Completed).To(ctx => ctx.WorkflowData.Step1Completed)
                    .Output(act => act.RetryAttempts).To(ctx => ctx.WorkflowData.RetryAttempts)
                    .OnError<TimeoutException>(handler => handler.ThenRetry(2)) // Step-level retry
                    .OnError<ValidationException>(handler => handler.ThenIgnore()) // Step-level ignore  
                    .OnError<InsufficientFundsException>(handler => handler.ThenTerminate()) // Step-level terminate
                    .CompensateWith<ReleaseInventoryCompensationAsyncActivity>(comp =>
                    {
                        comp.Output(act => act.Step1Compensated).To(ctx => ctx.WorkflowData.Step1Compensated);
                    }));
            })
            .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Success).From(ctx => ctx.WorkflowData.Step1Completed)
                .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsUpdated))
            .Build();

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert
        Assert.True(result.IsSuccess); // Should succeed after retries
        Assert.True(workflowData.Step1Completed);
        Assert.True(workflowData.RetryAttempts > 0); // Should show retry attempts occurred
        Assert.False(workflowData.Step1Compensated); // No compensation needed (step eventually succeeded)
        Assert.True(workflowData.AnalyticsUpdated); // Should continue after step succeeded
    }

    [Fact]
    public async Task SagaExecution_SuspendWithResumeCondition_ThenRetrySaga()
    {
        // Arrange - Test suspend/resume with ThenRetrySaga behavior
        var workflowData = new SagaTestData
        {
            OrderId = "ORDER-008",
            Amount = 100m,
            ProductId = "PRODUCT-008",
            PaymentMethod = "CreditCard"
        };

        var workflow = Workflow
            .Create<SagaTestData>("SuspendResumeSagaTest")
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            {
                saga
                    //.Suspend()
                    //.If(x=>x.SuspendNeededToCorrectData, then=> //allow user to select another model
                    //    then
                    //        .Suspend<ConfirmOrderEventData>("System unavailable - waiting for recovery",
                    //            (evt, ctx) => evt.OrderDataCorrected,
                    //            setup => setup
                    //                .Input(act => act.OrderData).From(ctx => ctx.PreviousStep)
                    //                .Output(act => act.Message).To(ctx => ctx.WorkflowData.SuspensionMessage)
                    //        )
                    //)
                    //.OutcomeOn(x => x.Data1, setup => 
                    //    setup
                    //        .Outcome("d11",d1=>{})
                    //        .Outcome("d12",d2 =>{})
                    //        .Outcome("d13",d3 =>{})
                    //    )
                    //.OutcomeOn(x => x.Data2, setup => 
                    //    setup
                    //        .Outcome("d21", d2 =>{})
                    //        .Outcome("d22", d2 =>{})
                    //        .Outcome("d23", d2 =>{})
                    //    )
                    .Suspend<OrderConfirmationEvent>("Waiting for order confirmation before proceeding with inventory reservation",
                        (evt, ctx) => evt.IsConfirmed,
                        setup => setup
                            .Input(act => act.ConfirmedProductId).From(ctx => ctx.WorkflowData.ProductId)
                            .Output(act => act.ConfirmedProductId).To(ctx => ctx.WorkflowData.SuspensionMessage)
                    )
                    .Step<SagaReserveInventoryAsyncActivity>(setup => setup
                        .Input(act => act.ProductId).From(ctx => ctx.PreviousStep.ConfirmedProductId)
                        .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)
                        .Output(act => act.Step1Completed).To(ctx => ctx.WorkflowData.Step1Completed)
                        .OnError(x=>x.ThenRetry(1))
                        .CompensateWith<ReleaseInventoryCompensationAsyncActivity>(comp =>
                        {
                            comp.Input(act => act.ReservationId).From(ctx => ctx.WorkflowData.ReservationId);
                            comp.Output(act => act.Step1Compensated).To(ctx => ctx.WorkflowData.Step1Compensated);
                        }))
                    .Step<SagaSystemDependentAsyncActivity>(setup => setup // Will fail due to system unavailable
                        .Input(act => act.SystemName).From(ctx => "PaymentProcessor")
                        .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Output(act => act.Step2Completed).To(ctx => ctx.WorkflowData.Step2Completed)
                        .CompensateWith<RevertPricingCompensationAsyncActivity>(comp =>
                        {
                            comp.Output(act => act.Step2Compensated).To(ctx => ctx.WorkflowData.Step2Compensated);
                        }));
            })
            .OnError<SystemUnavailableException>(error =>
                error
                    .Compensate() // Compensate all successful steps
                    .ThenRetry(2)) // Retry entire saga when resumed
            .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Success).From(ctx => ctx.WorkflowData.Step2Completed)
                .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsUpdated))
            .Build();

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert - This will be suspended, so check suspension state
        Assert.False(result.IsSuccess); // Should be suspended
        Assert.False(workflowData.Step1Completed); // Should be FALSE - suspend happens FIRST
        Assert.False(workflowData.Step2Completed); // Should be FALSE - suspend happens FIRST

        // NO compensation should have occurred yet - no steps have executed
        Assert.False(workflowData.Step1Compensated); // No compensation yet
        Assert.False(workflowData.Step2Compensated); // No compensation yet

        // Analytics should not have executed (workflow suspended)
        Assert.False(workflowData.AnalyticsUpdated);

        // Simulate order confirmation and resume the workflow
        var confirmationEvent = new OrderConfirmationEvent
        {
            OrderId = workflowData.OrderId,
            ConfirmedProductId = workflowData.ProductId,
            ConfirmedAmount = workflowData.Amount,
            IsConfirmed = true, // This satisfies the resume condition
            ConfirmedBy = "TestManager",
            Notes = "Order confirmed for testing"
        };
        var resumeResult = await _workflowEngine.ResumeWorkflowAsync(result.InstanceId, confirmationEvent);
        Assert.True(resumeResult.IsSuccess); // Should succeed after resume
        // Use the resumed workflow data, not the original
        var finalWorkflowData = (SagaTestData)resumeResult.WorkflowData!;
        Assert.Equal(workflowData.ProductId, finalWorkflowData.SuspensionMessage); // Should contain the confirmed product ID
        Assert.True(finalWorkflowData.AnalyticsUpdated);
    }

    [Fact]
    public async Task DEBUG_SimpleCompensationTest_ShouldShowWhatsFailing()
    {
        // SETUP: Create the simplest possible saga that should trigger compensation
        var workflowData = new SagaTestData
        {
            OrderId = "DEBUG-001",
            Amount = 100m,
            ProductId = "PRODUCT-DEBUG",
            PaymentMethod = "CreditCard"
        };

        var workflow = Workflow
            .Create<SagaTestData>("DEBUG_SimpleCompensation")
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            {
                // Step 1: Always succeeds, has compensation
                saga.Step<SagaReserveInventoryAsyncActivity>(setup => setup
                    .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)
                    .Output(act => act.Step1Completed).To(ctx => ctx.WorkflowData.Step1Completed)
                    .CompensateWith<ReleaseInventoryCompensationAsyncActivity>(comp =>
                    {
                        comp.Input(act => act.UselessProperty).From(ctx => ctx.CurrentStep.ProductId);
                        comp.Input(act => act.ReservationId).From(ctx => ctx.WorkflowData.ReservationId);
                        comp.Output(act => act.Step1Compensated).To(ctx => ctx.WorkflowData.Step1Compensated);
                    }))
                
                // Step 2: Always fails to trigger compensation
                .Step<SagaProcessPaymentAsyncActivity>(setup => setup
                    .Input(act => act.FinalPrice).From(ctx => ctx.WorkflowData.Amount)
                    .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                    .Input(act => act.ShouldFail).From(ctx => true) // FORCE FAILURE
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                    .Output(act => act.Step3Completed).To(ctx => ctx.WorkflowData.Step3Completed));
            })
            .OnError<PaymentProcessingException>(error =>
                error.Compensate().ThenTerminate()) // Should compensate all and terminate
            .Build();

        // ACT
        _output.WriteLine("DEBUG: Executing simple compensation test...");
        _output.WriteLine($"BEFORE: Step1Completed = {workflowData.Step1Completed}, Step1Compensated = {workflowData.Step1Compensated}");
        
        // DEBUG: Print workflow structure  
_output.WriteLine("WORKFLOW STRUCTURE:");
        for (int i = 0; i < workflow.Steps.Count; i++)
        {
            var step = workflow.Steps[i];
            _output.WriteLine($"  Step {i}: {step.Name} (Type: {step.StepType})");
            if (step.StepType == WorkflowStepType.Saga)
            {
                _output.WriteLine($"    SequenceSteps Count: {step.SequenceSteps?.Count ?? 0}");
                if (step.SequenceSteps != null)
                {
                    for (int j = 0; j < step.SequenceSteps.Count; j++)
                    {
                        var subStep = step.SequenceSteps[j];
                        _output.WriteLine($"      SubStep {j}: {subStep.Name} (Type: {subStep.StepType})");
                    }
                }
            }
        }
        
        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // DEBUG OUTPUT
        _output.WriteLine($"AFTER: Step1Completed = {workflowData.Step1Completed}, Step1Compensated = {workflowData.Step1Compensated}");
        _output.WriteLine($"Result.IsSuccess = {result.IsSuccess}");
        _output.WriteLine($"ReservationId = {workflowData.ReservationId}");
        _output.WriteLine($"ErrorMessage = {result.ErrorMessage}");

        // ASSERT
        Assert.True(workflowData.Step1Completed, "Step 1 should have completed successfully");
        Assert.False(workflowData.Step3Completed, "Step 3 should have failed");
        
        // THE KEY TEST: Is compensation working?
        if (!workflowData.Step1Compensated)
        {
        _output.WriteLine("COMPENSATION DID NOT EXECUTE - This is the bug we need to fix!");
            _output.WriteLine("Expected: Step1Compensated = true");
            _output.WriteLine($"Actual: Step1Compensated = {workflowData.Step1Compensated}");
            Assert.Fail("Fail on purpose to show output");
        }
        else
        {
        _output.WriteLine("COMPENSATION EXECUTED SUCCESSFULLY");
        }

        // For now, just document the failure - we'll fix this
        //Assert.True(workflowData.Step1Compensated, "Step 1 should have been compensated after saga failure");
    }

    [Fact]
    public async Task DEBUG_SagaSuspend_ShouldSuspendProperly()
    {
        // Arrange - Test simple saga suspend functionality
        var workflowData = new SagaTestData
        {
            OrderId = "DEBUG-SUSPEND-001",
            Amount = 100m,
            ProductId = "PRODUCT-DEBUG-SUSPEND",
            PaymentMethod = "CreditCard"
        };

        var workflow = Workflow
            .Create<SagaTestData>("DEBUG_SagaSuspendTest")
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            {
                saga
                    .Suspend<OrderConfirmationEvent>("Debug: Waiting for order confirmation",
                        (evt, ctx) => evt.IsConfirmed,
                        setup => setup
                            .Input(act => act.ConfirmedProductId).From(ctx => ctx.WorkflowData.ProductId)
                            .Output(act => act.ConfirmedProductId).To(ctx => ctx.WorkflowData.SuspensionMessage)
                    )
                    .Step<SagaReserveInventoryAsyncActivity>(setup => setup
                        .Input(act => act.ProductId).From(ctx => ctx.PreviousStep.ConfirmedProductId)
                        .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)
                        .Output(act => act.Step1Completed).To(ctx => ctx.WorkflowData.Step1Completed)
                        .CompensateWith<ReleaseInventoryCompensationAsyncActivity>(comp =>
                        {
                            comp.Input(act => act.ReservationId).From(ctx => ctx.WorkflowData.ReservationId);
                            comp.Output(act => act.Step1Compensated).To(ctx => ctx.WorkflowData.Step1Compensated);
                        }));
            })
            .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Success).From(ctx => ctx.WorkflowData.Step1Completed)
                .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsUpdated))
            .Build();

        // Act
_output.WriteLine("DEBUG: Executing saga suspend test...");
        _output.WriteLine($"BEFORE: Step1Completed = {workflowData.Step1Completed}");

        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Debug output
        _output.WriteLine($"AFTER: Step1Completed = {workflowData.Step1Completed}");
        _output.WriteLine($"Result.IsSuccess = {result.IsSuccess}");
        _output.WriteLine($"Result.Status = {result.Status}");
        _output.WriteLine($"ErrorMessage = {result.ErrorMessage}");

        // Assert - Should be suspended
        Assert.False(result.IsSuccess, "Workflow should be suspended (IsSuccess = false)");
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        Assert.Contains("suspended", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);

        // Step should not have executed yet (suspend happens first)
        Assert.False(workflowData.Step1Completed, "Step should not have completed - suspend happens first");
        Assert.False(workflowData.AnalyticsUpdated, "Analytics should not run - workflow is suspended");

        _output.WriteLine("SUSPEND TEST PASSED - Workflow properly suspended");
        //Assert.Fail(); // Fail on purpose to show output
    }

    [Fact]
    public async Task SagaExecution_OutcomeOnPattern_ShouldExecuteCorrectBranch()
    {
        // Arrange - Test OutcomeOn pattern execution with different payment methods
        var workflowData = new SagaTestData
        {
            OrderId = "OUTCOME-001",
            Amount = 100m,
            ProductId = "PRODUCT-OUTCOME",
            PaymentMethod = "Card"
        };

        var workflow = Workflow
            .Create<SagaTestData>("OutcomeOnExecutionTest")
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            {
                saga.Step<SagaSmartReserveInventoryAsyncActivity>(setup => setup
                    .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                    .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)
                    .Output(act => act.Step1Completed).To(ctx => ctx.WorkflowData.Step1Completed)
                    .CompensateWith<ReleaseInventoryCompensationAsyncActivity>(comp =>
                    {
                        comp.Input(act => act.ReservationId).From(ctx => ctx.WorkflowData.ReservationId);
                        comp.Output(act => act.Step1Compensated).To(ctx => ctx.WorkflowData.Step1Compensated);
                    }))

                .OutcomeOn(ctx => ctx.PreviousStep.ReservationId.Substring(0, 4), outcomes => outcomes // Use reservation prefix for branching
                    .Outcome("CARD", cardPath => cardPath
                        .Step<SagaProcessPaymentAsyncActivity>(setup => setup
                            .Input(act => act.FinalPrice).From(ctx => ctx.WorkflowData.Amount)
                            .Input(act => act.PaymentMethod).From(ctx => "CreditCard")
                            .Input(act => act.ShouldFail).From(ctx => false)
                            .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                            .Output(act => act.Step3Completed).To(ctx => ctx.WorkflowData.Step3Completed)))
                    
                    .Outcome("CASH", cashPath => cashPath
                        .Step<SagaCashPaymentAsyncActivity>(setup => setup
                            .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                            .Output(act => act.CashReceived).To(ctx => ctx.WorkflowData.CashReceived)
                            .Output(act => act.Step2Completed).To(ctx => ctx.WorkflowData.Step2Completed)))
                    
                    .Outcome("CHCK", checkPath => checkPath
                        .Suspend<OrderConfirmationEvent>("Waiting for check clearance"))
                    
                    .DefaultOutcome(defaultPath => defaultPath
                        .Step<SagaLogUnknownPaymentAsyncActivity>(setup => setup
                            .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                            .Output(act => act.LoggedUnknown).To(ctx => ctx.WorkflowData.LoggedUnknown))));
            })
            .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Success).From(ctx => ctx.WorkflowData.Step1Completed)
                .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsUpdated))
            .Build();

        // Act
        _output.WriteLine("TESTING: OutcomeOn pattern execution...");
        _output.WriteLine($"PaymentMethod: {workflowData.PaymentMethod}");

        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert
        _output.WriteLine($"Result.IsSuccess: {result.IsSuccess}");
        _output.WriteLine($"Step1Completed: {workflowData.Step1Completed}");
        _output.WriteLine($"ReservationId: {workflowData.ReservationId}");
        _output.WriteLine($"Step3Completed (Card): {workflowData.Step3Completed}");
        _output.WriteLine($"Step2Completed (Cash): {workflowData.Step2Completed}");
        _output.WriteLine($"TransactionId: {workflowData.TransactionId}");

        Assert.True(result.IsSuccess, "Workflow should complete successfully");
        Assert.True(workflowData.Step1Completed, "Inventory reservation should complete");

        // The ReservationId should start with "CARD" (based on our test activities)
        // So the Card outcome should execute
        Assert.True(workflowData.Step3Completed, "Card payment should have executed");
        Assert.False(workflowData.Step2Completed, "Cash payment should NOT have executed");
        Assert.NotEmpty(workflowData.TransactionId);
        Assert.True(workflowData.AnalyticsUpdated);

        _output.WriteLine("OutcomeOn pattern executed successfully - Card branch selected");
    }

    [Fact]
    public async Task SagaExecution_OutcomeOnPattern_CheckPayment_ShouldSuspendForCheckClearance()
    {
        // Arrange - Test OutcomeOn pattern with CHECK payment method that should suspend
        var workflowData = new SagaTestData
        {
            OrderId = "OUTCOME-CHECK-001",
            Amount = 100m,
            ProductId = "PRODUCT-OUTCOME-CHECK",
            PaymentMethod = "CHECK" // This should generate "CHCK" prefix and trigger suspend
        };

        var workflow = Workflow
            .Create<SagaTestData>("OutcomeOnCheckSuspendTest")
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            {
                saga.Step<SagaSmartReserveInventoryAsyncActivity>(setup => setup
                    .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                    .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)
                    .Output(act => act.Step1Completed).To(ctx => ctx.WorkflowData.Step1Completed)
                    .CompensateWith<ReleaseInventoryCompensationAsyncActivity>(comp =>
                    {
                        comp.Input(act => act.ReservationId).From(ctx => ctx.WorkflowData.ReservationId);
                        comp.Output(act => act.Step1Compensated).To(ctx => ctx.WorkflowData.Step1Compensated);
                    }))

                .OutcomeOn(ctx => ctx.PreviousStep.ReservationId.Substring(0, 4), outcomes => outcomes // Use reservation prefix for branching
                    .Outcome("CARD", cardPath => cardPath
                        .Step<SagaProcessPaymentAsyncActivity>(setup => setup
                            .Input(act => act.FinalPrice).From(ctx => ctx.WorkflowData.Amount)
                            .Input(act => act.PaymentMethod).From(ctx => "CreditCard")
                            .Input(act => act.ShouldFail).From(ctx => false)
                            .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                            .Output(act => act.Step3Completed).To(ctx => ctx.WorkflowData.Step3Completed)))
                    
                    .Outcome("CASH", cashPath => cashPath
                        .Step<SagaCashPaymentAsyncActivity>(setup => setup
                            .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                            .Output(act => act.CashReceived).To(ctx => ctx.WorkflowData.CashReceived)
                            .Output(act => act.Step2Completed).To(ctx => ctx.WorkflowData.Step2Completed)))
                    
                    .Outcome("CHCK", checkPath => checkPath
                        .Suspend<OrderConfirmationEvent>("Waiting for check clearance",
                            (evt, ctx) => evt.IsConfirmed,
                            setup => setup
                                .Input(act => act.ConfirmedProductId).From(ctx => ctx.WorkflowData.ProductId)
                                .Output(act => act.ConfirmedProductId).To(ctx => ctx.WorkflowData.SuspensionMessage)))
                    
                    .DefaultOutcome(defaultPath => defaultPath
                        .Step<SagaLogUnknownPaymentAsyncActivity>(setup => setup
                            .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                            .Output(act => act.LoggedUnknown).To(ctx => ctx.WorkflowData.LoggedUnknown))));
            })
            .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Success).From(ctx => ctx.WorkflowData.Step1Completed)
                .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsUpdated))
            .Build();

        // Act
_output.WriteLine("TESTING: OutcomeOn pattern with CHECK payment (should suspend)...");
        _output.WriteLine($"PaymentMethod: {workflowData.PaymentMethod}");

        var result = await _workflowEngine.ExecuteWorkflowAsync(workflow, workflowData);

        // Assert - Should be suspended
        _output.WriteLine($"Result.IsSuccess: {result.IsSuccess}");
        _output.WriteLine($"Result.Status: {result.Status}");
        _output.WriteLine($"Step1Completed: {workflowData.Step1Completed}");
        _output.WriteLine($"ReservationId: {workflowData.ReservationId}");
        _output.WriteLine($"ErrorMessage: {result.ErrorMessage}");

        Assert.False(result.IsSuccess, "Workflow should be suspended (IsSuccess = false)");
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        Assert.True(workflowData.Step1Completed, "Inventory reservation should complete");

        // The ReservationId should start with "CHCK" (based on CHECK payment method)
        Assert.StartsWith("CHCK", workflowData.ReservationId);
        
        // None of the other payment paths should have executed
        Assert.False(workflowData.Step3Completed, "Card payment should NOT have executed");
        Assert.False(workflowData.Step2Completed, "Cash payment should NOT have executed");
        Assert.False(workflowData.LoggedUnknown, "Unknown payment logging should NOT have executed");
        Assert.Empty(workflowData.TransactionId); // No transaction since suspended

        // Analytics should not have executed (workflow suspended)
        Assert.False(workflowData.AnalyticsUpdated, "Analytics should NOT run - workflow is suspended");

        _output.WriteLine("OutcomeOn pattern suspended successfully - Check clearance branch selected");
        
        // TEST INPUT MAPPING: Verify that input mapping worked during suspend
        // The suspend should have received the ProductId from workflow data as ConfirmedProductId
        var suspendedWorkflowData = (SagaTestData)result.WorkflowData!;
        _output.WriteLine($"TESTING INPUT MAPPING: Expected ProductId '{workflowData.ProductId}' should be available to suspend step");
        // Note: We can't directly access the suspend step's input since it's internal to the engine,
        // but we'll verify the workflow data is intact and ready for the suspend step
        Assert.Equal(workflowData.ProductId, suspendedWorkflowData.ProductId); // Input source should be preserved
        
        // Test resume functionality
        _output.WriteLine("TESTING: Resuming workflow with check confirmation...");
        
        var confirmationEvent = new OrderConfirmationEvent
        {
            OrderId = workflowData.OrderId,
            ConfirmedProductId = "CONFIRMED-" + workflowData.ProductId,
            ConfirmedAmount = workflowData.Amount,
            IsConfirmed = true, // This satisfies the resume condition
            ConfirmedBy = "CheckClearanceManager",
            Notes = "Check cleared successfully"
        };
        
        _output.WriteLine($"RESUME EVENT: ConfirmedProductId = '{confirmationEvent.ConfirmedProductId}'");

        var resumeResult = await _workflowEngine.ResumeWorkflowAsync(result.InstanceId, confirmationEvent);
        
        // Assert resume success
        _output.WriteLine($"Resume Result.IsSuccess: {resumeResult.IsSuccess}");
        _output.WriteLine($"Resume Result.Status: {resumeResult.Status}");
        
        Assert.True(resumeResult.IsSuccess, "Workflow should complete successfully after resume");
        Assert.Equal(WorkflowExecutionStatus.Success, resumeResult.Status);
        
        // TEST OUTPUT MAPPING: Use the resumed workflow data
        var finalWorkflowData = (SagaTestData)resumeResult.WorkflowData!;
        _output.WriteLine($"OUTPUT MAPPING: Expected '{confirmationEvent.ConfirmedProductId}', Actual '{finalWorkflowData.SuspensionMessage}'");
        Assert.Equal(confirmationEvent.ConfirmedProductId, finalWorkflowData.SuspensionMessage); // Should contain the confirmed product ID from resume event
        Assert.True(finalWorkflowData.AnalyticsUpdated, "Analytics should run after resume");

        _output.WriteLine("SUSPEND/RESUME MAPPING TEST PASSED:");
        _output.WriteLine($"  - Input Mapping: ProductId '{workflowData.ProductId}' available to suspend step");
        _output.WriteLine($"  - Output Mapping: Event ConfirmedProductId '{confirmationEvent.ConfirmedProductId}' -> SuspensionMessage '{finalWorkflowData.SuspensionMessage}'");
        _output.WriteLine("OutcomeOn pattern resumed and completed successfully");
    }
    
    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }

    /// <summary>
    ///     Mock implementation of IWorkflowStateRepository for testing
    /// </summary>
    public class MockWorkflowStateRepository : IWorkflowStateRepository
    {
        private readonly Dictionary<string, WorkflowInstance> _instances = new();

        public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId)
        {
            _instances.TryGetValue(instanceId, out var instance);
            return Task.FromResult(instance);
        }

        public Task SaveWorkflowInstanceAsync(WorkflowInstance instance)
        {
            _instances[instance.InstanceId] = instance;
            return Task.CompletedTask;
        }

        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName)
        {
            var instances = _instances.Values.Where(i => i.WorkflowName == workflowName);
            return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
        }

        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status)
        {
            var instances = _instances.Values.Where(i => i.Status == status);
            return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
        }

        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId)
        {
            var instances = _instances.Values.Where(i => i.CorrelationId == correlationId);
            return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
        }

        public Task<IEnumerable<WorkflowInstance>> GetSuspendedWorkflowsReadyForResumptionAsync()
        {
            var instances = _instances.Values.Where(i => i.Status == WorkflowStatus.Suspended);
            return Task.FromResult<IEnumerable<WorkflowInstance>>(instances);
        }

        public Task DeleteWorkflowInstanceAsync(string instanceId)
        {
            _instances.Remove(instanceId);
            return Task.CompletedTask;
        }

        public Task<List<WorkflowInstance>> GetWorkflowInstancesAsync(string workflowName, int? version = null)
        {
            var instances = _instances.Values.Where(i => i.WorkflowName == workflowName);
            if (version.HasValue)
            {
                instances = instances.Where(i => i.WorkflowVersion == version.Value);
            }
            return Task.FromResult(instances.ToList());
    }
}

    /// <summary>
    ///     Mock implementation of IEventStore for testing
    /// </summary>
    private class MockEventStore : IEventStore
    {
        public Task AppendEventAsync(string streamId, WorkflowEvent workflowEvent)
        {
            return Task.CompletedTask;
        }

        public Task<IEnumerable<WorkflowEvent>> GetEventsAsync(string streamId)
        {
            return Task.FromResult<IEnumerable<WorkflowEvent>>(new List<WorkflowEvent>());
        }

        public Task<IEnumerable<WorkflowEvent>> GetEventsFromSequenceAsync(string streamId, long fromSequenceNumber)
        {
            return Task.FromResult<IEnumerable<WorkflowEvent>>(new List<WorkflowEvent>());
        }

        public Task<long> GetLatestSequenceNumberAsync(string streamId)
        {
            return Task.FromResult(0L);
        }

        public Task SaveEventAsync<TEvent>(string eventId, TEvent eventData, string? correlationId = null)
            where TEvent : class
        {
            return Task.CompletedTask;
        }

        public Task<TEvent?> GetEventAsync<TEvent>(string eventId) where TEvent : class
        {
            return Task.FromResult<TEvent?>(null);
        }

        public Task<List<TEvent>> GetEventsAsync<TEvent>(string? correlationId = null) where TEvent : class
        {
            return Task.FromResult(new List<TEvent>());
        }

        public Task DeleteEventAsync(string eventId)
        {
            return Task.CompletedTask;
        }
    }

    public class SagaCheckInventoryAsyncActivity : IAsyncActivity
    {
        public bool SuspendNeededToCorrectData { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}

using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.SyntaxTests.Models;

namespace IxIFlow.Tests.SyntaxTests;

/// <summary>
/// Test 6: Saga with compensation and error handling syntax validation
/// This tests:
/// - Saga activities with CompensateWith (only available in saga context)
/// - Saga-specific error strategies (Retry, Terminate, Compensate, Ignore)
/// - Saga container-level error handling with compensation strategies
/// - Previous step access within saga activities
/// </summary>
[Trait("Feature", "authoring")]
public class SagaSyntaxTests
{
    [Fact]
    public void Build_SagaWithCompensation_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that saga with compensation syntax compiles correctly
        var definition = Workflow.Create<OrderWorkflowData>("SagaWithCompensationTest", 1)
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))

            .Saga(saga =>
            {
                saga.Step<ChargePaymentAsyncActivity>(setup => setup
                    .Input(act => act.TransactionId).From(ctx => ctx.PreviousStep.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                    .CompensateWith<RefundPaymentAsyncActivity>(comp =>
                    {
                        comp.Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.TransactionId);
                    }));
            })
            .Build();

        // Assert - Verify workflow definition was created successfully
        Assert.NotNull(definition);
        Assert.Equal("SagaWithCompensationTest", definition.Name);
        Assert.Equal(1, definition.Version);
        Assert.Equal(2, definition.Steps.Count); // ValidateOrder + Saga
        Assert.Equal(typeof(OrderWorkflowData), definition.WorkflowDataType);
    }

    [Fact]
    public void Build_SagaWithErrorHandling_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that saga with error handling compiles correctly
        var definition = Workflow.Create<OrderWorkflowData>("SagaWithErrorHandlingTest", 1)
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))

            .Saga(saga =>
            {
                saga.Step<ChargePaymentAsyncActivity>(setup => setup
                    .Input(act => act.TransactionId).From(ctx => ctx.PreviousStep.OrderId)
                    .OnError<PaymentTimeoutException>(handler => handler.ThenRetry(3)) // Step-specific error handling
                    .OnError<InsufficientFundsException>(handler => handler.ThenTerminate()) // Step-specific terminate
                    .CompensateWith<RefundPaymentAsyncActivity>());
            })
            .OnError<PaymentException>(error =>
            {
                error.Compensate() // All successful steps
                    .ThenTerminate(); // Throws SagaTerminatedException
            })
            .Build();

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count); // ValidateOrder + Saga
    }

    [Fact]
    public void Build_SagaWithMultipleSteps_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that saga with multiple steps compiles correctly
        var definition = Workflow.Create<OrderWorkflowData>("SagaWithMultipleStepsTest", 1)
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            {
                saga.Step<ChargePaymentAsyncActivity>(setup => setup
                    .Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                    .CompensateWith<RefundPaymentAsyncActivity>())

                .Step<ReserveInventoryAsyncActivity>(setup => setup
                    .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                    .Input(act => act.InStock).From(ctx => !string.IsNullOrEmpty(ctx.PreviousStep.TransactionId)) // Access previous step
                    .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)
                    .CompensateWith<ReleaseInventoryAsyncActivity>())

                .Step<SendConfirmationAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.ShipmentId).From(ctx => ctx.PreviousStep.ReservationId) // Access previous step
                    .CompensateWith<SendCancellationAsyncActivity>());
            })
            .Build();

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
    }

    [Fact]
    public void Build_SagaWithTryCatch_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that saga within try/catch compiles correctly
        var definition = Workflow.Create<OrderWorkflowData>("SagaWithTryCatchTest", 1)
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Try(tryBlock =>
            {
                tryBlock.Saga(saga =>
                {
                    saga.Step<ChargePaymentAsyncActivity>(setup => setup
                        .Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.OrderId)
                        .CompensateWith<RefundPaymentAsyncActivity>());
                });
            })
            .Catch<SagaTerminatedException, MessageFault>(catchBlock =>
            {
                catchBlock.Step<HandleSagaFailureAsyncActivity>(setup => setup
                    .Input(act => act.ErrorMessage).From(ctx => ctx.Fault.Message)
                    .Output(act => act.FailureHandled).To(ctx => ctx.WorkflowData.ErrorHandled));
            })
                .Catch(x => { })
            .Build();

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.TryCatch, definition.Steps[1].StepType);
    }

    [Fact]
    public void Build_SagaStructure_ShouldHaveCorrectStepTypes()
    {
        // Arrange & Act
        var definition = Workflow.Create<OrderWorkflowData>("SagaStructureTest", 1)
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))

            .Saga(saga =>
            {
                saga.Step<ChargePaymentAsyncActivity>(setup => setup
                    .Input(act => act.TransactionId).From(ctx => ctx.PreviousStep.OrderId)
                    .CompensateWith<RefundPaymentAsyncActivity>());
            })
            .Build();

        // Assert - Verify step types and structure
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[0].StepType);
        Assert.Equal(typeof(ValidateOrderAsyncActivity), definition.Steps[0].ActivityType);

        // Note: Saga step type might not be implemented yet
        Assert.Equal(2, definition.Steps.Count);
    }

    [Fact]
    public void Build_SagaErrorStrategies_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that saga error strategies compile correctly
        var definition = Workflow.Create<OrderWorkflowData>("SagaErrorStrategiesTest", 1)
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            {
                saga.Step<ChargePaymentAsyncActivity>(setup => setup
                    .Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.OrderId)
                    .OnError<PaymentTimeoutException>(handler => handler.ThenRetry(3)) // Retry strategy
                    .OnError<InsufficientFundsException>(handler => handler.ThenTerminate()) // Terminate strategy
                    .OnError(handler => handler.ThenIgnore()) // Ignore strategy
                    .CompensateWith<RefundPaymentAsyncActivity>());
            })
            .Build();

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
    }

    [Fact]
    public void Build_SagaCompensationStrategies_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that saga compensation strategies compile correctly
        var definition = Workflow.Create<OrderWorkflowData>("SagaCompensationStrategiesTest", 1)
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            {
                saga.Step<ChargePaymentAsyncActivity>(setup => setup
                    .Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.OrderId)
                    .CompensateWith<RefundPaymentAsyncActivity>());
            })
            .OnError<PaymentException>(error =>
            {
                error.Compensate() // Compensate all successful steps
                    .ThenTerminate();
            })
            .OnError(error =>
            {
                error.CompensateUpTo<ChargePaymentAsyncActivity>() // Compensate up to specific step
                    .ThenRetry(maxAttempts: 3);
            })
            .Build();

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
    }

    [Fact]
    public void Build_StepAfterSaga_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that step after saga can access saga results
        var definition = Workflow.Create<OrderWorkflowData>("StepAfterSagaTest", 1)
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            {
                saga.Step<ChargePaymentAsyncActivity>(setup => setup
                    .Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                    .CompensateWith<RefundPaymentAsyncActivity>());
            })

            .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Success).From(ctx => !string.IsNullOrEmpty(ctx.WorkflowData.TransactionId)) // Access saga result
                .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsStatus))
            .Build();

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(3, definition.Steps.Count); // Saga + UpdateAnalytics
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[0].StepType);
        Assert.Equal(WorkflowStepType.Saga, definition.Steps[1].StepType);
        Assert.Equal(typeof(UpdateAnalyticsAsyncActivity), definition.Steps[2].ActivityType);
    }

    [Fact]
    public void Build_SagaWithComplexCompensation_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that saga with complex compensation logic compiles
        var definition = Workflow.Create<OrderWorkflowData>("SagaComplexCompensationTest", 1)
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Saga(saga =>
            {
                saga.Step<ChargePaymentAsyncActivity>(setup => setup
                    .Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                    .CompensateWith<RefundPaymentAsyncActivity>(comp =>
                    {
                        comp.Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.TransactionId);
                        comp.Input(act => act.TransactionId).From(ctx => ctx.CurrentStep.TransactionId); // Access previous step in compensation
                    }));
            })
            .Build();

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
    }

    [Fact]
    public void Build_SagaWithOutcomeOnPattern_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that saga with OutcomeOn pattern compiles correctly
        var definition = Workflow.Create<OrderWorkflowData>("SagaOutcomeOnTest", 1)
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))

            .Saga(saga =>
            {
                saga.Step<ChargePaymentAsyncActivity>(setup => setup
                    .Input(act => act.TransactionId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.PaymentStatus).To(ctx => ctx.WorkflowData.PaymentStatus)
                    .CompensateWith<RefundPaymentAsyncActivity>())

                .OutcomeOn(ctx => ctx.PreviousStep.PaymentMethod, outcomes => outcomes
                    .Outcome("Card", success => success
                        .Step<SendConfirmationAsyncActivity>(setup => setup
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)))
                    
                    .Outcome("Cash", declined => declined
                        .Step<HandleDeclinedPaymentAsyncActivity>(setup => setup
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)))
                    
                    .Outcome("Check", pending => pending
                        .Suspend<ConfirmOrderEventData>("Waiting for payment confirmation"))
                    
                    .DefaultOutcome(defaultPath => defaultPath
                        .Step<LogUnknownStatusAsyncActivity>(setup => setup
                            .Input(act => act.Status).From(ctx => ctx.WorkflowData.PaymentStatus))));
            })
            .Build();

        // Assert
        Assert.NotNull(definition);
        Assert.Equal("SagaOutcomeOnTest", definition.Name);
        Assert.Equal(1, definition.Version);
        Assert.Equal(2, definition.Steps.Count); // ValidateOrder + Saga
        Assert.Equal(typeof(OrderWorkflowData), definition.WorkflowDataType);
    }

    [Fact]
    public void Build_SagaWithSimpleOutcomeOn_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that saga with simple OutcomeOn pattern compiles correctly
        var definition = Workflow.Create<OrderWorkflowData>("SagaSimpleOutcomeOnTest", 1)
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))

            .Saga(saga =>
            {
                saga.Step<ChargePaymentAsyncActivity>(setup => setup
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.PaymentStatus).To(ctx => ctx.WorkflowData.PaymentStatus))

                .OutcomeOn(ctx => ctx.WorkflowData.ValidationResult, outcomes => outcomes
                    .Outcome(true, valid => valid
                        .Step<ProcessOrderAsyncActivity>(setup => setup
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)))
                    
                    .DefaultOutcome(invalid => invalid
                        .Step<RejectOrderAsyncActivity>(setup => setup
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))));
            })
            .Build();

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
    }
}

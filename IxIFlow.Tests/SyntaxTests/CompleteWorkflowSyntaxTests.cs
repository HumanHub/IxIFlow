using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.SyntaxTests.Models;

namespace IxIFlow.Tests.SyntaxTests;

/// <summary>
/// Tests complete mixed workflow combining all patterns.
/// </summary>
public class CompleteWorkflowSyntaxTests
{
    [Fact]
    public void Build_CompleteWorkflowAllPatterns_ShouldCompileSuccessfully()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
                .Step<CheckInventoryAsyncActivity>(setup => setup
                    .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                    .Input(act => act.IsOrderValid).From(ctx => ctx.PreviousStep.IsValid)
                    .Output(act => act.InStock).To(ctx => ctx.WorkflowData.InventoryStatus))

                .Try(tryBlock =>
                {
                    tryBlock.Step<CalculatePricingAsyncActivityMock>(setup => setup
                        .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                        .Input(act => act.InStock).From(ctx => ctx.PreviousStep.InStock)
                        .Output(act => act.FinalPrice).To(ctx => ctx.WorkflowData.FinalPrice));
                })
                .Catch<PricingException>(catchBlock =>
                {
                    catchBlock.Step<UseDefaultPricingAsyncActivity>(setup => setup
                        .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                        .Input(act => act.SomeMessage).From(ctx => ctx.Exception.Message)
                        .Output(act => act.DefaultPrice).To(ctx => ctx.WorkflowData.FinalPrice));
                })
                .Catch<Exception>(catchBlock =>
                {
                    catchBlock.Step<UseDefaultPricingAsyncActivity>(setup => setup
                        .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                        .Input(act => act.SomeMessage).From(ctx => ctx.Exception.Message)
                        .Output(act => act.DefaultPrice).To(ctx => ctx.WorkflowData.FinalPrice));
                })

                .If(ctx => ctx.WorkflowData.FinalPrice > 1000,
                    then =>
                    {
                        then.Suspend<ApprovalEventData>("High value order requires approval",
                             (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId && evt.Approved,
                             setup => setup
                                 .Input(act=>act.ProductId).From(ctx=>ctx.PreviousStep.ProductId)
                                 .Output(act=>act.Approved).To(ctx=>ctx.WorkflowData.Approved)
                             );

                        // For now, just process the order directly
                        then.Step<ProcessOrderAsyncActivity>(setup => setup
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                            .Output(act => act.ProcessedOrder).To(ctx => ctx.WorkflowData.ProcessedOrder));
                    },
                    @else =>
                    {
                        @else.Step<ProcessOrderAsyncActivity>(setup => setup
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                            .Output(act => act.ProcessedOrder).To(ctx => ctx.WorkflowData.ProcessedOrder));
                    })

                // Handle saga termination with Try/Catch
                .Try(tryBlock =>
                {
                    // TODO: Saga pattern not implemented yet
                    tryBlock.Saga(saga =>
                    {
                        saga.Step<ChargePaymentAsyncActivity>(setup => setup
                                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.FinalPrice)
                                .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)
                                .OnError<PaymentException>(handler => handler.ThenRetry(3))
                                .CompensateWith<RefundPaymentAsyncActivity>())

                            .Step<ReserveInventoryAsyncActivity>(setup => setup
                                .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                                .Input(act => act.InStock).From(ctx => !string.IsNullOrEmpty(ctx.PreviousStep.TransactionId))
                                .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)
                                .OnError<InventoryException>(handler => handler.ThenRetry(3))
                                .CompensateWith<ReleaseInventoryAsyncActivity>(x => x
                                    .Input(s => s.ReservationId).From(chargePayment => chargePayment.CurrentStep.ReservationId)))

                            .Step<CreateShipmentAsyncActivity>(setup => setup
                                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                                .Input(act => act.ReservationId)
                                .From(ctx => ctx.PreviousStep.ReservationId)
                                .Output(act => act.ShipmentId).To(ctx => ctx.WorkflowData.ShipmentId)
                                .OnError(handler => handler.ThenIgnore()) // Non-critical
                                .CompensateWith<CancelShipmentAsyncActivity>());
                    })
                    .OnError<PaymentException>(error =>
                    {
                        error
                            .Step<LogPaymentException>(setup => setup
                                .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                                .Input(act => act.ErrorMessage).From(ctx => ctx.PreviousStep.ProductId)
                                .Output(act => act.Logged).To(ctx => ctx.WorkflowData.ErrorHandled)
                                .OnError(handler => handler.ThenRetry(1)))
                            .Step<LogPaymentException>(setup => setup
                                .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                                .Input(act => act.ErrorMessage).From(ctx => ctx.PreviousStep.ErrorMessage)
                                .Output(act => act.Logged).To(ctx => ctx.WorkflowData.ErrorHandled)
                                .OnError(handler => handler.ThenRetry(1)))
                            .CompensateUpTo<ChargePaymentAsyncActivity>() // Compensate up to specific step
                            .ThenTerminate(); // Throws SagaTerminatedException
                    })
                    .OnError(error =>
                    {
                        error
                            .Compensate(); // Compensate successful steps
                    });
                })
                 .Catch<SagaTerminatedException>(catchBlock =>
                 {
                     catchBlock
                         .Step<HandleSagaFailureAsyncActivity>(setup => setup
                             .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                             .Output(act => act.FailureHandled).To(ctx => ctx.WorkflowData.ErrorHandled))
                         .Parallel(parallel =>
                         {
                             parallel
                                 .Do(emailBranch =>
                                 {
                                     emailBranch.Step<SendEmailAsyncActivity>(setup => setup
                                         .Input(act => act.EmailAddress).From(ctx => ctx.WorkflowData.CustomerEmail)
                                         .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                                         .Output(act => act.EmailSent).To(ctx => ctx.WorkflowData.EmailStatus));
                                 })
                                 .Do(smsBranch =>
                                 {
                                     smsBranch.Step<SendSmsAsyncActivity>(setup => setup
                                         .Input(act => act.PhoneNumber).From(ctx => ctx.WorkflowData.CustomerPhone)
                                         .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                                         .Output(act => act.SmsSent).To(ctx => ctx.WorkflowData.SmsStatus));
                                 });
                         });
                 })
                .Catch<Exception>(catchBlock =>
                {
                    catchBlock
                        .Step<HandleSagaFailureAsyncActivity>(setup => setup
                            .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                            .Output(act => act.FailureHandled).To(ctx => ctx.WorkflowData.ErrorHandled))
                     .Parallel(parallel =>
                     {
                         parallel
                             .Do(emailBranch =>
                             {
                                 emailBranch.Step<SendEmailAsyncActivity>(setup => setup
                                     .Input(act => act.EmailAddress).From(ctx => ctx.WorkflowData.CustomerEmail)
                                     .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                                     .Output(act => act.EmailSent).To(ctx => ctx.WorkflowData.EmailStatus));
                             })
                             .Do(smsBranch =>
                             {
                                 smsBranch.Step<SendSmsAsyncActivity>(setup => setup
                                     .Input(act => act.PhoneNumber).From(ctx => ctx.WorkflowData.CustomerPhone)
                                     .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                                     .Output(act => act.SmsSent).To(ctx => ctx.WorkflowData.SmsStatus));
                             });
                     });
                })

                 // Continue with regular activities after payment processing
                 .Parallel(parallel =>
                 {
                     parallel
                         .Do(emailBranch =>
                         {
                             emailBranch.Step<SendConfirmationAsyncActivity>(setup => setup
                                 .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                                 .Input(act => act.ShipmentId).From(ctx => ctx.WorkflowData.ShipmentId)
                                 .Input(act => act.ProcessedData).From(ctx => ctx.WorkflowData.ProcessedOrder)
                                 .Output(act => act.ConfirmationSent).To(ctx => ctx.WorkflowData.EmailStatus));
                         })
                         .Do(smsBranch =>
                         {
                             smsBranch.Step<SendSmsAsyncActivity>(setup => setup
                                 .Input(act => act.PhoneNumber).From(ctx => ctx.WorkflowData.CustomerPhone)
                                 .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                                 .Output(act => act.SmsSent).To(ctx => ctx.WorkflowData.SmsStatus));
                         });
                 })
                .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Success).From(ctx => ctx.WorkflowData.ErrorHandled == false)
                    .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsStatus)),
            "CompleteWorkflowAllPatternsTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal("CompleteWorkflowAllPatternsTest", definition.Name);
        Assert.Equal(1, definition.Version);
        Assert.True(definition.Steps.Count >= 6);
        Assert.Equal(typeof(OrderWorkflowData), definition.WorkflowDataType);
    }

    [Fact]
    public void Build_CompleteWorkflowWithSequentialActivities_ShouldCompileSuccessfully()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
                .Step<CheckInventoryAsyncActivity>(setup => setup
                    .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                    .Input(act => act.IsOrderValid)
                    .From(ctx => ctx.PreviousStep.IsValid) // ValidateOrderAsyncActivity.IsValid
                    .Output(act => act.InStock).To(ctx => ctx.WorkflowData.InventoryStatus))
                .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Success)
                    .From(ctx => ctx.PreviousStep.InStock) // CheckInventoryAsyncActivity.InStock
                    .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsStatus)),
            "CompleteSequentialTest");

        // Assert - Verify workflow definition was created successfully
        Assert.NotNull(definition);
        Assert.Equal("CompleteSequentialTest", definition.Name);
        Assert.Equal(1, definition.Version);
        Assert.Equal(3, definition.Steps.Count);
        Assert.Equal(typeof(OrderWorkflowData), definition.WorkflowDataType);
    }

    [Fact]
    public void Build_WorkflowWithTryCatchAndConditionals_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that try/catch with conditionals compile correctly
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
                .Try(tryBlock =>
                {
                    tryBlock.Step<CalculatePricingAsyncActivityMock>(setup => setup
                        .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                        .Input(act => act.InStock)
                        .From(ctx => ctx.PreviousStep.IsValid) // ValidateOrderAsyncActivity.IsValid
                        .Output(act => act.FinalPrice).To(ctx => ctx.WorkflowData.FinalPrice));
                })
                .Catch<PricingException>(catchBlock =>
                {
                    catchBlock.Step<UseDefaultPricingAsyncActivity>(setup => setup
                        .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                        .Input(act => act.SomeMessage).From(ctx => ctx.Exception.Message) // Exception message access
                        .Output(act => act.DefaultPrice).To(ctx => ctx.WorkflowData.FinalPrice));
                })
                .If(ctx => ctx.WorkflowData.FinalPrice > 1000,
                    then =>
                    {
                        then.Step<ProcessOrderAsyncActivity>(setup => setup
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                            .Output(act => act.ProcessedOrder).To(ctx => ctx.WorkflowData.ProcessedOrder));
                    },
                    @else =>
                    {
                        @else.Step<ProcessValidOrderAsyncActivity>(setup => setup
                            .Input(act => act.OrderData).From(ctx => ctx.WorkflowData.OrderId)
                            .Input(act => act.ValidationResult).From(ctx => ctx.WorkflowData.ValidationResult)
                            .Output(act => act.ProcessedOrder).To(ctx => ctx.WorkflowData.ProcessedOrder));
                    }),
            "TryCatchConditionalTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(3, definition.Steps.Count); // ValidateOrder + TryCatch + If
    }

    [Fact]
    public void Build_WorkflowWithParallelExecution_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that parallel execution compiles correctly
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))

                 .Parallel(parallel =>
                 {
                     parallel
                         .Do(emailBranch =>
                         {
                             emailBranch.Step<SendEmailAsyncActivity>(setup => setup
                                 .Input(act => act.EmailAddress).From(ctx => ctx.WorkflowData.CustomerEmail)
                                 .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                                 .Output(act => act.EmailSent).To(ctx => ctx.WorkflowData.EmailStatus));
                         })
                         .Do(smsBranch =>
                         {
                             smsBranch.Step<SendSmsAsyncActivity>(setup => setup
                                 .Input(act => act.PhoneNumber).From(ctx => ctx.WorkflowData.CustomerPhone)
                                 .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                                 .Output(act => act.SmsSent).To(ctx => ctx.WorkflowData.SmsStatus));
                         });
                 })
                .Step<VerifyNotificationsAsyncActivity>(setup => setup
                    .Input(act => act.EmailStatus).From(ctx => ctx.WorkflowData.EmailStatus)
                    .Input(act => act.SmsStatus).From(ctx => ctx.WorkflowData.SmsStatus)
                    .Output(act => act.AllNotificationsSent).To(ctx => ctx.WorkflowData.NotificationComplete)),
            "ParallelExecutionTest");

        // Assert
        Assert.NotNull(definition);
        // Assert.Equal(3, definition.Steps.Count); // ValidateOrder + Parallel + VerifyNotifications
    }

    [Fact]
    public void Build_WorkflowStructure_ShouldHaveCorrectStepTypes()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Try(tryBlock =>
                {
                    tryBlock.Step<ProcessOrderAsyncActivity>(setup => setup
                        .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId));
                })
                .Catch<Exception>(catchBlock =>
                {
                    catchBlock.Step<ErrorLoggingAsyncActivity>(setup => setup
                        .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message));
                })

             .Parallel(parallel =>
             {
                 parallel.Do(branch =>
                 {
                     branch.Step<SendEmailAsyncActivity>(setup => setup
                         .Input(act => act.EmailAddress).From(ctx => ctx.WorkflowData.CustomerEmail));
                 });
             }),
            "WorkflowStructureTest");

        // Assert - Verify step types and structure
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[0].StepType);
        Assert.Equal(typeof(ValidateOrderAsyncActivity), definition.Steps[0].ActivityType);

        Assert.Equal(WorkflowStepType.TryCatch, definition.Steps[1].StepType);
        Assert.Single(definition.Steps[1].CatchBlocks); // One catch block

        // Assert.Equal(WorkflowStepType.Parallel, definition.Steps[2].StepType);
        // Assert.Single(definition.Steps[2].ParallelBranches); // One parallel branch
    }

    [Fact]
    public void Build_ChainedExceptionHandling_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that chained exception handling compiles correctly
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Try(tryBlock =>
                {
                    tryBlock.Step<RiskyAsyncActivity>(setup => setup
                        .Input(act => act.Data).From(ctx => ctx.WorkflowData.OrderId));
                })
                .Catch<PricingException>(catchBlock =>
                {
                    catchBlock.Step<BusinessErrorHandlerAsyncActivity>(setup => setup
                        .Input(act => act.BusinessErrors).From(ctx => ctx.WorkflowData.ValidationErrors));
                })
                .Catch<Exception>(catchBlock =>
                {
                    catchBlock.Step<ErrorLoggingAsyncActivity>(setup => setup
                        .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                        .Input(act => act.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId));
                }),
            "ChainedExceptionHandlingTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count); // ValidateOrder + TryCatch
        Assert.Equal(WorkflowStepType.TryCatch, definition.Steps[1].StepType);
        Assert.Equal(2, definition.Steps[1].CatchBlocks.Count); // Two catch blocks
    }

    [Fact]
    public void Build_NestedParallelInConditional_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that nested parallel execution in conditional compiles
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
                .If(ctx => ctx.WorkflowData.ValidationResult,
                    then =>
                    {
                        then.Parallel(parallel =>
                        {
                            parallel
                                .Do(branch1 =>
                                {
                                    branch1.Step<SendEmailAsyncActivity>(setup => setup
                                        .Input(act => act.EmailAddress).From(ctx => ctx.WorkflowData.CustomerEmail));
                                })
                                .Do(branch2 =>
                                {
                                    branch2.Step<SendSmsAsyncActivity>(setup => setup
                                        .Input(act => act.PhoneNumber).From(ctx => ctx.WorkflowData.CustomerPhone));
                                });
                        });
                    },
                    @else =>
                    {
                        @else.Step<RejectOrderAsyncActivity>(setup => setup
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId));
                    }),
            "NestedParallelInConditionalTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count); // ValidateOrder + If
        Assert.Equal(WorkflowStepType.Conditional, definition.Steps[1].StepType);
    }
}

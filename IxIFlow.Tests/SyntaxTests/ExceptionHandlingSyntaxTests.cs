using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.SyntaxTests.Models;

namespace IxIFlow.Tests.SyntaxTests;

/// <summary>
/// Tests exception handling with Try/Catch syntax validation.
/// </summary>
public class ExceptionHandlingSyntaxTests
{
    [Fact]
    public void Build_TryCatchWithSingleCatch_ShouldCompileSuccessfully()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
                .Try(tryBlock =>
                {
                    tryBlock.Step<RiskyAsyncActivity>(setup => setup
                        .Input(act => act.Data).From(ctx => ctx.PreviousStep.IsValid)
                        .Output(act => act.Result).To(ctx => ctx.WorkflowData.ProcessedOrder));
                })
                .Catch<PaymentException>(catchBlock =>
                {
                    catchBlock.Step<ErrorLoggingAsyncActivity>(setup => setup
                        .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                        .Input(act => act.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId)
                        .Output(act => act.LoggedAt).To(ctx => ctx.WorkflowData.CompletedTimestamp));
                }),
            "TryCatchSingleTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal("TryCatchSingleTest", definition.Name);
        Assert.Equal(1, definition.Version);
        Assert.Equal(2, definition.Steps.Count); // ValidateOrder + TryCatch
        Assert.Equal(typeof(OrderWorkflowData), definition.WorkflowDataType);
    }

    [Fact]
    public void Build_TryCatchWithMultipleCatches_ShouldCompileSuccessfully()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
                .Try(tryBlock =>
                {
                    tryBlock.Step<RiskyAsyncActivity>(setup => setup
                        .Input(act => act.Data).From(ctx => ctx.PreviousStep.IsValid)
                        .Output(act => act.Result).To(ctx => ctx.WorkflowData.ProcessedOrder));
                })
                .Catch<PaymentException>(catchBlock =>
                {
                    catchBlock.Step<ErrorLoggingAsyncActivity>(setup => setup
                        .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                        .Input(act => act.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId)
                        .Output(act => act.LoggedAt).To(ctx => ctx.WorkflowData.CompletedTimestamp));
                })
                .Catch<BusinessException>(catchBlock =>
                {
                    catchBlock.Step<BusinessErrorHandlerAsyncActivity>(setup => setup
                        .Input(act => act.BusinessErrors).From(ctx => ctx.Exception.BusinessErrors)
                        .Output(act => act.HandledErrors).To(ctx => ctx.WorkflowData.ProcessedOrder));
                }),
            "TryCatchMultipleTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count); // ValidateOrder + TryCatch
        Assert.Equal(WorkflowStepType.TryCatch, definition.Steps[1].StepType);
        Assert.Equal(2, definition.Steps[1].CatchBlocks.Count); // Two catch blocks
    }

    [Fact]
    public void Build_TryCatchWithFinally_ShouldCompileSuccessfully()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
                .Try(tryBlock =>
                {
                    tryBlock.Step<RiskyAsyncActivity>(setup => setup
                        .Input(act => act.Data).From(ctx => ctx.PreviousStep.IsValid)
                        .Output(act => act.Result).To(ctx => ctx.WorkflowData.ProcessedOrder));
                })
                .Catch<PaymentException>(catchBlock =>
                {
                    catchBlock.Step<ErrorLoggingAsyncActivity>(setup => setup
                        .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                        .Input(act => act.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId));
                })
                .Finally(finallyBlock =>
                {
                    finallyBlock.Step<CleanupAsyncActivity>(setup => setup
                        .Input(act => act.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId)
                        .Output(act => act.CleanupResult).To(ctx => ctx.WorkflowData.ProcessedOrder));
                }),
            "TryCatchFinallyTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count); // ValidateOrder + TryCatch
        Assert.Equal(WorkflowStepType.TryCatch, definition.Steps[1].StepType);
        Assert.Single(definition.Steps[1].CatchBlocks); // One catch block
        Assert.Single(definition.Steps[1].FinallySteps); // One finally step
    }

    [Fact]
    public void Build_TryCatchStructure_ShouldHaveCorrectStepTypes()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Try(tryBlock =>
                {
                    tryBlock.Step<RiskyAsyncActivity>(setup => setup
                        .Input(act => act.Data).From(ctx => ctx.PreviousStep.IsValid));
                })
                .Catch<PaymentException>(catchBlock =>
                {
                    catchBlock.Step<ErrorLoggingAsyncActivity>(setup => setup
                        .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message));
                }),
            "TryCatchStructureTest");

        // Assert
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[0].StepType);
        Assert.Equal(typeof(ValidateOrderAsyncActivity), definition.Steps[0].ActivityType);

        Assert.Equal(WorkflowStepType.TryCatch, definition.Steps[1].StepType);
        Assert.Single(definition.Steps[1].SequenceSteps); // Try block has one step
        Assert.Single(definition.Steps[1].CatchBlocks); // One catch block

        // Verify catch block structure
        var catchBlock = definition.Steps[1].CatchBlocks[0];
        Assert.Equal(WorkflowStepType.CatchBlock, catchBlock.StepType);
        Assert.Equal(typeof(PaymentException), catchBlock.ExceptionType);
    }

    [Fact]
    public void Build_ExceptionContextAccess_ShouldCompileCorrectly()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Try(tryBlock =>
                {
                    tryBlock.Step<RiskyAsyncActivity>(setup => setup
                        .Input(act => act.Data).From(ctx => ctx.PreviousStep.IsValid));
                })
                .Catch<PaymentException>(catchBlock =>
                {
                    catchBlock.Step<ErrorLoggingAsyncActivity>(setup => setup
                        .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message)
                        .Input(act => act.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId));
                }),
            "ExceptionContextAccessTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.TryCatch, definition.Steps[1].StepType);
    }

    [Fact]
    public void Build_BusinessExceptionWithCustomProperties_ShouldCompileCorrectly()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Try(tryBlock =>
                {
                    tryBlock.Step<RiskyAsyncActivity>(setup => setup
                        .Input(act => act.Data).From(ctx => ctx.PreviousStep.IsValid));
                })
                .Catch<BusinessException>(catchBlock =>
                {
                    catchBlock.Step<BusinessErrorHandlerAsyncActivity>(setup => setup
                        .Input(act => act.BusinessErrors).From(ctx => ctx.Exception.BusinessErrors));
                }),
            "BusinessExceptionTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
        var catchBlock = definition.Steps[1].CatchBlocks[0];
        Assert.Equal(typeof(BusinessException), catchBlock.ExceptionType);
    }

    [Fact]
    public void Build_StepAfterTryCatch_ShouldAccessTryBlockResult()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
                .Try(tryBlock =>
                {
                    tryBlock.Step<RiskyAsyncActivity>(setup => setup
                        .Input(act => act.Data).From(ctx => ctx.PreviousStep.IsValid)
                        .Output(act => act.Result).To(ctx => ctx.WorkflowData.ProcessedOrder));
                })
                .Catch<PaymentException>(catchBlock =>
                {
                    catchBlock.Step<ErrorLoggingAsyncActivity>(setup => setup
                        .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message));
                })
                .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Success).From(ctx => ctx.WorkflowData.ProcessedOrder != null)),
            "StepAfterTryCatchTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(3, definition.Steps.Count); // ValidateOrder + TryCatch + UpdateAnalytics
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[2].StepType);
        Assert.Equal(typeof(UpdateAnalyticsAsyncActivity), definition.Steps[2].ActivityType);
    }

    [Fact]
    public void Build_NestedTryBlockSteps_ShouldCompileCorrectly()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<NoopActivity>()
                .Try(tryBlock =>
                {
                    tryBlock
                        .Step<ValidateOrderAsyncActivity>(setup => setup
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                            .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
                        .Step<RiskyAsyncActivity>(setup => setup
                            .Input(act => act.Data).From(ctx => ctx.PreviousStep.IsValid)
                            .Output(act => act.Result).To(ctx => ctx.WorkflowData.ProcessedOrder));
                })
                .Catch<PaymentException>(catchBlock =>
                {
                    catchBlock.Step<ErrorLoggingAsyncActivity>(setup => setup
                        .Input(act => act.ErrorMessage).From(ctx => ctx.Exception.Message));
                }),
            "NestedTryBlockTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count); // NoopActivity + TryCatch step
        Assert.Equal(WorkflowStepType.TryCatch, definition.Steps[1].StepType);
        Assert.Equal(2, definition.Steps[1].SequenceSteps.Count); // Two steps in try block
    }
}

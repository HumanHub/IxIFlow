using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.SyntaxTests.Models;

namespace IxIFlow.Tests.SyntaxTests;

/// <summary>
/// Tests basic workflow with transparent pass-through syntax validation.
/// </summary>
public class BasicTransparentPassThroughSyntaxTests
{
    [Fact]
    public void Build_BasicTransparentPassThroughWorkflow_ShouldCompileSuccessfully()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult)
                    .Output(act => act.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors))

                .Step<CheckInventoryAsyncActivity>(setup => setup
                    .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                    .Input(act => act.IsOrderValid).From(ctx => ctx.PreviousStep.IsValid)
                    .Output(act => act.InStock).To(ctx => ctx.WorkflowData.InventoryStatus))

                .Sequence(seq =>
                {
                    seq.Step<ReserveInventoryAsyncActivity>(setup => setup
                            .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                            .Input(act => act.InStock).From(ctx => ctx.PreviousStep.InStock)
                            .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId))
                        .Step<CalculatePricingAsyncActivity>(setup => setup
                            .Input(act => act.ReservationId).From(ctx => ctx.PreviousStep.ReservationId)
                            .Input(act => act.BasePrice).From(ctx => ctx.WorkflowData.BasePrice)
                            .Output(act => act.FinalPrice).To(ctx => ctx.WorkflowData.FinalPrice));
                })

                .Step<ProcessPaymentAsyncActivity>(setup => setup
                    .Input(act => act.Amount).From(ctx => ctx.PreviousStep.FinalPrice)
                    .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId))

                .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Success).From(ctx => !string.IsNullOrEmpty(ctx.PreviousStep.TransactionId))
                    .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsStatus)),
            "BasicTransparentPassThroughTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal("BasicTransparentPassThroughTest", definition.Name);
        Assert.Equal(1, definition.Version);
        Assert.Equal(5, definition.Steps.Count); // 4 steps + 1 sequence
        Assert.Equal(typeof(OrderWorkflowData), definition.WorkflowDataType);
    }

    [Fact]
    public void Build_ConditionalWorkflow_ShouldHaveCorrectStepStructure()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult)
                    .Output(act => act.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors))
                .If(ctx => ctx.PreviousStep.IsValid,
                    then =>
                    {
                        then.Step<ProcessValidOrderAsyncActivity>(setup => setup
                            .Input(act => act.OrderData).From(ctx => ctx.WorkflowData.OrderId)
                            .Input(act => act.ValidationResult).From(ctx => ctx.PreviousStep.IsValid)
                            .Output(act => act.ProcessedOrder).To(ctx => ctx.WorkflowData.ProcessedOrder));
                    },
                    @else =>
                    {
                        @else.Step<RejectOrderAsyncActivity>(setup => setup
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                            .Input(act => act.ValidationErrors).From(ctx => ctx.PreviousStep.ValidationErrors)
                            .Output(act => act.RejectionReason).To(ctx => ctx.WorkflowData.ErrorMessage));
                    })

                .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Success).From(ctx => ctx.WorkflowData.ProcessedOrder != null)
                    .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsStatus)),
            "ConditionalWorkflowTest");

        // Assert
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[0].StepType);
        Assert.Equal(typeof(ValidateOrderAsyncActivity), definition.Steps[0].ActivityType);

        Assert.Equal(WorkflowStepType.Conditional, definition.Steps[1].StepType);
        Assert.Single(definition.Steps[1].ThenSteps);
        Assert.Single(definition.Steps[1].ElseSteps);
        Assert.Equal(typeof(ProcessValidOrderAsyncActivity), definition.Steps[1].ThenSteps[0].ActivityType);
        Assert.Equal(typeof(RejectOrderAsyncActivity), definition.Steps[1].ElseSteps[0].ActivityType);

        Assert.Equal(WorkflowStepType.Activity, definition.Steps[2].StepType);
        Assert.Equal(typeof(UpdateAnalyticsAsyncActivity), definition.Steps[2].ActivityType);
    }

    [Fact]
    public void Build_WorkflowDataAccess_ShouldCompileCorrectly()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)),
            "WorkflowDataAccessTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Single(definition.Steps);
    }

    [Fact]
    public void Build_PreviousStepAccess_ShouldCompileCorrectly()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount))
                .Step<CheckInventoryAsyncActivity>(setup => setup
                    .Input(act => act.IsOrderValid).From(ctx => ctx.PreviousStep.IsValid)),
            "PreviousStepAccessTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
    }

    [Fact]
    public void Build_SequenceWithTransparentPassThrough_ShouldCompileCorrectly()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Sequence(seq => seq
                    .Step<CheckInventoryAsyncActivity>(setup => setup
                        .Input(act => act.IsOrderValid).From(ctx => ctx.PreviousStep.IsValid))
                    .Step<ReserveInventoryAsyncActivity>(setup => setup
                        .Input(act => act.InStock).From(ctx => ctx.PreviousStep.InStock)))
                .Step<ProcessPaymentAsyncActivity>(setup => setup
                    .Input(act => act.Amount).From(ctx => ctx.PreviousStep.Amount)),
            "SequenceTransparencyTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(3, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.Sequence, definition.Steps[1].StepType);
        Assert.Equal(2, definition.Steps[1].SequenceSteps.Count);
    }
}

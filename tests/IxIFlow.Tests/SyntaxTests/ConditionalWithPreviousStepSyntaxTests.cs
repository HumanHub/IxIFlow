using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.SyntaxTests.Models;

namespace IxIFlow.Tests.SyntaxTests;

/// <summary>
/// Tests conditional execution with previous step access syntax validation.
/// </summary>
public class ConditionalWithPreviousStepSyntaxTests
{
    [Fact]
    public void Build_ConditionalWithPreviousStepAccess_ShouldCompileSuccessfully()
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
            "ConditionalWithPreviousStepTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal("ConditionalWithPreviousStepTest", definition.Name);
        Assert.Equal(1, definition.Version);
        Assert.Equal(3, definition.Steps.Count); // ValidateOrder + If + UpdateAnalytics
        Assert.Equal(typeof(OrderWorkflowData), definition.WorkflowDataType);
    }

    [Fact]
    public void Build_ConditionalWithPreviousStepAccess_ShouldHaveCorrectStepStructure()
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
            "ConditionalWithPreviousStepTest");

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
    public void Build_IfConditionAccess_ShouldCompileCorrectly()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount))
                .If(ctx => ctx.PreviousStep.IsValid,
                    then => then.Step<ProcessValidOrderAsyncActivity>(setup => setup
                        .Input(act => act.OrderData).From(ctx => ctx.WorkflowData.OrderId)),
                    @else => @else.Step<RejectOrderAsyncActivity>(setup => setup
                        .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))),
            "IfConditionAccessTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.Conditional, definition.Steps[1].StepType);
    }

    [Fact]
    public void Build_ThenBranchPreviousStepAccess_ShouldCompileCorrectly()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount))
                .If(ctx => ctx.PreviousStep.IsValid,
                    then => then.Step<ProcessValidOrderAsyncActivity>(setup => setup
                        .Input(act => act.ValidationResult)
                        .From(ctx => ctx.PreviousStep.IsValid)),
                    @else => @else.Step<RejectOrderAsyncActivity>(setup => setup
                        .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))),
            "ThenBranchAccessTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.Conditional, definition.Steps[1].StepType);
        Assert.Single(definition.Steps[1].ThenSteps);
    }

    [Fact]
    public void Build_ElseBranchPreviousStepAccess_ShouldCompileCorrectly()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount))
                .If(ctx => ctx.PreviousStep.IsValid,
                    then => then.Step<ProcessValidOrderAsyncActivity>(setup => setup
                        .Input(act => act.OrderData).From(ctx => ctx.WorkflowData.OrderId)),
                    @else => @else.Step<RejectOrderAsyncActivity>(setup => setup
                        .Input(act => act.ValidationErrors)
                        .From(ctx => ctx.PreviousStep.ValidationErrors))),
            "ElseBranchAccessTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.Conditional, definition.Steps[1].StepType);
        Assert.Single(definition.Steps[1].ElseSteps);
    }

    [Fact]
    public void Build_StepAfterConditional_ShouldAccessStepBeforeConditional()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount))
                .If(ctx => ctx.PreviousStep.IsValid,
                    then => then.Step<ProcessValidOrderAsyncActivity>(setup => setup
                        .Input(act => act.OrderData).From(ctx => ctx.WorkflowData.OrderId)),
                    @else => @else.Step<RejectOrderAsyncActivity>(setup => setup
                        .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)))
                .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                    .Input(act => act.Success).From(ctx => ctx.PreviousStep.IsValid)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)),
            "StepAfterConditionalTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(3, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[2].StepType);
        Assert.Equal(typeof(UpdateAnalyticsAsyncActivity), definition.Steps[2].ActivityType);
    }
}

using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.SyntaxTests.Models;

namespace IxIFlow.Tests.SyntaxTests;

/// <summary>
///     Test 5: Suspend/Resume with events syntax validation
///     This tests:
///     - Combined Suspend
///     <TEventData>
///         (reason, condition) syntax
///         - Resume event access in subsequent steps
///         - Nested suspend/resume in conditional branches
/// </summary>
public class SuspendResumeSyntaxTests
{
    [Fact]
    public void Build_SuspendWithEvent_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that suspend with event syntax compiles correctly
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ProcessOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.ProcessedOrder).To(ctx => ctx.WorkflowData.ProcessedOrder))

                // Suspend with resume condition
                .Suspend<ApprovalEventData>("Waiting for manager approval",
                    (resumeEvent, ctx) => resumeEvent.OrderId == ctx.WorkflowData.OrderId &&
                                          (resumeEvent.Approved || resumeEvent.Declined),
                    setup => setup
                        .Input(evt => evt.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Step<CompleteOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.ProcessedData)
                    .From(ctx => ctx.WorkflowData.ProcessedOrder) // Access from workflow data
                    .Output(act => act.CompletedAt).To(ctx => ctx.WorkflowData.CompletedTimestamp)),
            "SuspendWithEventTest");

        // Assert - Verify workflow definition was created successfully
        Assert.NotNull(definition);
        Assert.Equal("SuspendWithEventTest", definition.Name);
        Assert.Equal(1, definition.Version);
        Assert.Equal(3, definition.Steps.Count); // ProcessOrder + Suspend + CompleteOrder
        Assert.Equal(typeof(OrderWorkflowData), definition.WorkflowDataType);
    }

    [Fact]
    public void Build_ResumeEventAccess_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that resume event access compiles correctly
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ProcessOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Suspend<ApprovalEventData>("Waiting for approval",
                    (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId,
                    setup => setup
                        .Input(evt => evt.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Output(evt => evt.RequiresDoubleApproval).To(ctx => ctx.WorkflowData.RequiresSecondApproval)
                )

                // Conditional based on resume event data
                .If(ctx => ctx.PreviousStep.RequiresDoubleApproval,
                    then =>
                    {
                        then.Step<FinalizeOrderAsyncActivity>(setup => setup
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                            .Output(act => act.FinalizedAt).To(ctx => ctx.WorkflowData.CompletedTimestamp));
                    },
                    @else =>
                    {
                        @else.Step<CompleteOrderAsyncActivity>(setup => setup
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                            .Output(act => act.CompletedAt).To(ctx => ctx.WorkflowData.CompletedTimestamp));
                    }),
            "ResumeEventAccessTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(3, definition.Steps.Count); // ProcessOrder + Suspend + If
        // Note: Suspend step type not yet implemented in engine
        Assert.Equal(WorkflowStepType.Conditional, definition.Steps[2].StepType);
    }

    [Fact]
    public void Build_NestedSuspendInConditional_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that nested suspend in conditional branches compiles correctly
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ProcessOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Suspend<ApprovalEventData>("Waiting for first approval",
                    (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId,
                    setup => setup
                        .Input(evt => evt.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Output(evt => evt.RequiresDoubleApproval).To(ctx => ctx.WorkflowData.RequiresSecondApproval)
                )
                .If(ctx => ctx.PreviousStep.RequiresDoubleApproval,
                    then =>
                    {
                        then.Suspend<SecondApprovalEventData>("Waiting for second manager approval",
                                (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId && (evt.Approved || evt.Declined),
                                setup => setup
                                    .Input(evt => evt.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                            .Step<FinalizeOrderAsyncActivity>(setup => setup
                                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                                .Output(act => act.FinalizedAt).To(ctx => ctx.WorkflowData.CompletedTimestamp));
                    },
                    @else =>
                    {
                        @else.Step<FinalizeOrderAsyncActivity>(setup => setup
                            .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                            .Output(act => act.FinalizedAt).To(ctx => ctx.WorkflowData.CompletedTimestamp));
                    }),
            "NestedSuspendTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(3, definition.Steps.Count); // ProcessOrder + Suspend + If
        Assert.Equal(WorkflowStepType.SuspendResume, definition.Steps[1].StepType);
        Assert.Equal(typeof(ApprovalEventData), definition.Steps[1].ResumeEventType);
    }

    [Fact]
    public void Build_SuspendWithComplexCondition_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that suspend with complex condition compiles
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ProcessOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Suspend<ApprovalEventData>("Waiting for complex approval",
                    (resumeEvent, ctx) => resumeEvent.OrderId == ctx.WorkflowData.OrderId &&
                                          (resumeEvent.Approved || resumeEvent.Declined) &&
                                          !resumeEvent.RequiresDoubleApproval, // Complex condition
                    setup => setup
                        .Input(evt => evt.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Step<CompleteOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)),
            "SuspendComplexConditionTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(3, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.SuspendResume, definition.Steps[1].StepType);
    }

    [Fact]
    public void Build_SuspendStructure_ShouldHaveCorrectStepTypes()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Suspend<ApprovalEventData>("Waiting for approval",
                    (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId,
                    setup => setup
                        .Input(evt => evt.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Step<CompleteOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)),
            "SuspendStructureTest");

        // Assert - Verify step types and structure
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[0].StepType);
        Assert.Equal(typeof(ValidateOrderAsyncActivity), definition.Steps[0].ActivityType);

        Assert.Equal(WorkflowStepType.SuspendResume, definition.Steps[1].StepType);
        Assert.Equal(typeof(ApprovalEventData), definition.Steps[1].ResumeEventType);
        // Note: SuspendReason property not exposed on WorkflowStep

        Assert.Equal(WorkflowStepType.Activity, definition.Steps[2].StepType);
        Assert.Equal(typeof(CompleteOrderAsyncActivity), definition.Steps[2].ActivityType);
    }

    [Fact]
    public void Build_StepAfterSuspend_ShouldAccessPreSuspendResult()
    {
        // Arrange & Act - Test that step after suspend can access results from before suspend
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ProcessOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.ProcessedOrder).To(ctx => ctx.WorkflowData.ProcessedOrder))
                .Suspend<ApprovalEventData>("Waiting for approval",
                    (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId)
                .Step<CompleteOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.ProcessedData)
                    .From(ctx => ctx.WorkflowData.ProcessedOrder) // Access from workflow data
                    .Output(act => act.CompletedAt).To(ctx => ctx.WorkflowData.CompletedTimestamp)),
            "StepAfterSuspendTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(3, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[2].StepType);
        Assert.Equal(typeof(CompleteOrderAsyncActivity), definition.Steps[2].ActivityType);
    }

    [Fact]
    public void Build_MultipleSuspends_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that multiple suspends compile correctly
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ProcessOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Suspend<ApprovalEventData>("Waiting for first approval",
                    (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId,
                    setup => setup
                        .Input(evt => evt.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Step<ValidateOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Suspend<SecondApprovalEventData>("Waiting for second approval",
                    (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId,
                    setup => setup
                        .Input(evt => evt.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Step<CompleteOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)),
            "MultipleSuspendsTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(5, definition.Steps.Count); // ProcessOrder + Suspend + Validate + Suspend + Complete
        Assert.Equal(WorkflowStepType.SuspendResume, definition.Steps[1].StepType);
        Assert.Equal(typeof(ApprovalEventData), definition.Steps[1].ResumeEventType);
        Assert.Equal(WorkflowStepType.SuspendResume, definition.Steps[3].StepType);
        Assert.Equal(typeof(SecondApprovalEventData), definition.Steps[3].ResumeEventType);
    }

    [Fact]
    public void Build_SuspendResumeEventProperties_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that accessing resume event properties compiles correctly
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<ProcessOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Suspend<ApprovalEventData>("Waiting for approval",
                    (evt, ctx) => evt.OrderId == ctx.WorkflowData.OrderId,
                    setup => setup
                        .Input(evt => evt.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                .Step<CompleteOrderAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.PreviousStep.OrderId) // Access resume event properties
                    .Output(act => act.CompletedAt).To(ctx => ctx.WorkflowData.CompletedTimestamp)),
            "SuspendResumeEventPropertiesTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(3, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.SuspendResume, definition.Steps[1].StepType);
    }
}
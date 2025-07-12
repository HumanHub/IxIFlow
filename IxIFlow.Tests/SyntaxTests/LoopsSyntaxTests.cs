using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.SyntaxTests.Models;

namespace IxIFlow.Tests.SyntaxTests;

/// <summary>
///     Test 9: Loops with previous step access syntax validation
///     This tests:
///     - DoWhile and WhileDo constructs
///     - Previous step access within loop body
///     - Loop condition accessing previous step data
/// </summary>
public class LoopsSyntaxTests
{
    [Fact]
    public void Build_DoWhileLoop_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that DoWhile loop syntax compiles correctly
        var definition = Workflow.Build<BatchWorkflowData>(builder => builder
                .Step<InitializeBatchAsyncActivity>(setup => setup
                    .Input(act => act.BatchData).From(ctx => ctx.WorkflowData.BatchInput)
                    .Output(act => act.BatchItems).To(ctx => ctx.WorkflowData.RemainingItems)
                    .Output(act => act.ProcessedCount).To(ctx => ctx.WorkflowData.ProcessedCount))
                .DoWhile(
                    loopBody =>
                    {
                        loopBody.Step<ProcessBatchItemAsyncActivity>(setup => setup
                            .Input(act => act.BatchItems).From(ctx => ctx.WorkflowData.RemainingItems)
                            .Input(act => act.ProcessedItems)
                            .From(ctx =>
                                ctx.PreviousStep.ProcessedCount) // Previous iteration or InitializeBatchAsyncActivity
                            .Output(act => act.ProcessedItems).To(ctx => ctx.WorkflowData.ProcessedCount)
                            .Output(act => act.RemainingItems).To(ctx => ctx.WorkflowData.RemainingItems));
                    },
                    condition =>
                        condition.PreviousStep.RemainingItems.Count > 0 // ProcessBatchItemAsyncActivity.RemainingItems
                ),
            "DoWhileLoopTest");

        // Assert - Verify workflow definition was created successfully
        Assert.NotNull(definition);
        Assert.Equal("DoWhileLoopTest", definition.Name);
        Assert.Equal(1, definition.Version);
        Assert.Equal(2, definition.Steps.Count); // InitializeBatch + DoWhile
        Assert.Equal(typeof(BatchWorkflowData), definition.WorkflowDataType);
    }

    [Fact]
    public void Build_WhileDoLoop_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that WhileDo loop syntax compiles correctly
        var definition = Workflow.Build<BatchWorkflowData>(builder => builder
                .Step<InitializeBatchAsyncActivity>(setup => setup
                    .Input(act => act.BatchData).From(ctx => ctx.WorkflowData.BatchInput)
                    .Output(act => act.RemainingItems).To(ctx => ctx.WorkflowData.RemainingItems)
                    .Output(act => act.ProcessedCount).To(ctx => ctx.WorkflowData.ProcessedCount))
                .WhileDo(
                    condition =>
                        condition.PreviousStep.RemainingItems.Count > 0, // InitializeBatchAsyncActivity.RemainingItems
                    loopBody =>
                    {
                        loopBody.Step<ProcessBatchItemAsyncActivity>(setup => setup
                            .Input(act => act.BatchItems).From(ctx => ctx.WorkflowData.RemainingItems)
                            .Input(act => act.ProcessedItems)
                            .From(ctx =>
                                ctx.PreviousStep.ProcessedCount) // Previous iteration or InitializeBatchAsyncActivity
                            .Output(act => act.ProcessedItems).To(ctx => ctx.WorkflowData.ProcessedCount)
                            .Output(act => act.RemainingItems).To(ctx => ctx.WorkflowData.RemainingItems));
                    }
                ),
            "WhileDoLoopTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count); // InitializeBatch + WhileDo
    }

    [Fact]
    public void Build_LoopWithMultipleSteps_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that loops with multiple steps compile correctly
        var definition = Workflow.Build<BatchWorkflowData>(builder => builder
                .Step<InitializeBatchAsyncActivity>(setup => setup
                    .Input(act => act.BatchData).From(ctx => ctx.WorkflowData.BatchInput)
                    .Output(act => act.RemainingItems).To(ctx => ctx.WorkflowData.RemainingItems))
                .DoWhile(
                    loopBody =>
                    {
                        loopBody
                            .Step<ProcessBatchItemAsyncActivity>(setup => setup
                                .Input(act => act.BatchItems).From(ctx => ctx.WorkflowData.RemainingItems)
                                .Output(act => act.ProcessedItems).To(ctx => ctx.WorkflowData.ProcessedCount))
                            .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                                .Input(act => act.OrderId)
                                .From(ctx => ctx.PreviousStep.ProcessedItems.ToString()) // Access previous step in loop
                                .Input(act => act.Success).From(ctx => ctx.PreviousStep.ProcessedItems > 0)
                                .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsStatus));
                    },
                    condition => condition.WorkflowData.RemainingItems.Count > 0
                ),
            "LoopWithMultipleStepsTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count); // InitializeBatch + DoWhile
    }

    [Fact]
    public void Build_NestedLoops_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that nested loops compile correctly
        var definition = Workflow.Build<BatchWorkflowData>(builder => builder
                .Step<InitializeBatchAsyncActivity>(setup => setup
                    .Input(act => act.BatchData).From(ctx => ctx.WorkflowData.BatchInput)
                    .Output(act => act.RemainingItems).To(ctx => ctx.WorkflowData.RemainingItems))
                .DoWhile(
                    outerLoop =>
                    {
                        outerLoop.WhileDo(
                            condition => condition.WorkflowData.RemainingItems.Count > 5, // Inner condition
                            innerLoop =>
                            {
                                innerLoop.Step<ProcessBatchItemAsyncActivity>(setup => setup
                                    .Input(act => act.BatchItems).From(ctx => ctx.WorkflowData.RemainingItems)
                                    .Output(act => act.RemainingItems).To(ctx => ctx.WorkflowData.RemainingItems));
                            }
                        );
                    },
                    condition => condition.WorkflowData.RemainingItems.Count > 0 // Outer condition
                ),
            "NestedLoopsTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count); // InitializeBatch + DoWhile
    }

    [Fact]
    public void Build_LoopStructure_ShouldHaveCorrectStepTypes()
    {
        // Arrange & Act
        var definition = Workflow.Build<BatchWorkflowData>(builder => builder
                .Step<InitializeBatchAsyncActivity>(setup => setup
                    .Input(act => act.BatchData).From(ctx => ctx.WorkflowData.BatchInput))
                .DoWhile(
                    loopBody =>
                    {
                        loopBody.Step<ProcessBatchItemAsyncActivity>(setup => setup
                            .Input(act => act.BatchItems).From(ctx => ctx.WorkflowData.RemainingItems));
                    },
                    condition => condition.WorkflowData.RemainingItems.Count > 0
                ),
            "LoopStructureTest");

        // Assert - Verify step types and structure
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[0].StepType);
        Assert.Equal(typeof(InitializeBatchAsyncActivity), definition.Steps[0].ActivityType);

        Assert.Equal(WorkflowStepType.Loop, definition.Steps[1].StepType);
        Assert.Equal(LoopType.DoWhile, definition.Steps[1].LoopType);
        Assert.Single(definition.Steps[1].LoopBodySteps); // One step in loop body
    }

    [Fact]
    public void Build_WhileDoStructure_ShouldHaveCorrectStepTypes()
    {
        // Arrange & Act
        var definition = Workflow.Build<BatchWorkflowData>(builder => builder
                .Step<InitializeBatchAsyncActivity>(setup => setup
                    .Input(act => act.BatchData).From(ctx => ctx.WorkflowData.BatchInput))
                .WhileDo(
                    condition => condition.WorkflowData.RemainingItems.Count > 0,
                    loopBody =>
                    {
                        loopBody.Step<ProcessBatchItemAsyncActivity>(setup => setup
                            .Input(act => act.BatchItems).From(ctx => ctx.WorkflowData.RemainingItems));
                    }
                ),
            "WhileDoStructureTest");

        // Assert - Verify step types and structure
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[0].StepType);
        Assert.Equal(typeof(InitializeBatchAsyncActivity), definition.Steps[0].ActivityType);

        Assert.Equal(WorkflowStepType.Loop, definition.Steps[1].StepType);
        Assert.Equal(LoopType.WhileDo, definition.Steps[1].LoopType);
        Assert.Single(definition.Steps[1].LoopBodySteps); // One step in loop body
    }

    [Fact]
    public void Build_StepAfterLoop_ShouldAccessLoopResult()
    {
        // Arrange & Act - Test that step after loop can access loop results
        var definition = Workflow.Build<BatchWorkflowData>(builder => builder
                .Step<InitializeBatchAsyncActivity>(setup => setup
                    .Input(act => act.BatchData).From(ctx => ctx.WorkflowData.BatchInput)
                    .Output(act => act.RemainingItems).To(ctx => ctx.WorkflowData.RemainingItems))
                .DoWhile(
                    loopBody =>
                    {
                        loopBody.Step<ProcessBatchItemAsyncActivity>(setup => setup
                            .Input(act => act.BatchItems).From(ctx => ctx.WorkflowData.RemainingItems)
                            .Output(act => act.ProcessedItems).To(ctx => ctx.WorkflowData.ProcessedCount)
                            .Output(act => act.RemainingItems).To(ctx => ctx.WorkflowData.RemainingItems));
                    },
                    condition => condition.PreviousStep.RemainingItems.Count > 0
                )
                .Step<UpdateAnalyticsAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.ProcessedCount.ToString())
                    .Input(act => act.Success).From(ctx => ctx.PreviousStep.ProcessedItems > 0) // Access loop result
                    .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsStatus)),
            "StepAfterLoopTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(3, definition.Steps.Count); // InitializeBatch + DoWhile + UpdateAnalytics
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[2].StepType);
        Assert.Equal(typeof(UpdateAnalyticsAsyncActivity), definition.Steps[2].ActivityType);
    }

    [Fact]
    public void Build_LoopConditionComplexity_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that complex loop conditions compile correctly
        var definition = Workflow.Build<BatchWorkflowData>(builder => builder
                .Step<InitializeBatchAsyncActivity>(setup => setup
                    .Input(act => act.BatchData).From(ctx => ctx.WorkflowData.BatchInput)
                    .Output(act => act.RemainingItems).To(ctx => ctx.WorkflowData.RemainingItems)
                    .Output(act => act.ProcessedCount).To(ctx => ctx.WorkflowData.ProcessedCount))
                .DoWhile(
                    loopBody =>
                    {
                        loopBody.Step<ProcessBatchItemAsyncActivity>(setup => setup
                            .Input(act => act.BatchItems).From(ctx => ctx.WorkflowData.RemainingItems)
                            .Output(act => act.RemainingItems).To(ctx => ctx.WorkflowData.RemainingItems));
                    },
                    condition => condition.PreviousStep.RemainingItems.Count > 0 &&
                                 condition.WorkflowData.ProcessedCount < 100 // Complex condition
                ),
            "LoopConditionComplexityTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.Loop, definition.Steps[1].StepType);
    }

    [Fact]
    public void Build_EmptyLoopBody_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that loop with minimal body compiles
        var definition = Workflow.Build<BatchWorkflowData>(builder => builder
                .Step<InitializeBatchAsyncActivity>(setup => setup
                    .Input(act => act.BatchData).From(ctx => ctx.WorkflowData.BatchInput)
                    .Output(act => act.RemainingItems).To(ctx => ctx.WorkflowData.RemainingItems))
                .DoWhile(
                    loopBody =>
                    {
                        loopBody.Step<ProcessBatchItemAsyncActivity>(setup => setup
                            .Input(act => act.BatchItems).From(ctx => ctx.WorkflowData.RemainingItems));
                    },
                    condition => false // Loop will not execute
                ),
            "EmptyLoopBodyTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.Loop, definition.Steps[1].StepType);
    }
}
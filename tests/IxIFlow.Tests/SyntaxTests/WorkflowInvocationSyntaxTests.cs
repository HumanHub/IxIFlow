using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.SyntaxTests.Models;

namespace IxIFlow.Tests.SyntaxTests;

/// <summary>
///     Test 3: Workflow invocation with same Input/Output pattern as activities syntax validation
///     This tests:
///     - Invoke
///     <WorkflowType, WorkflowDataType>
///         syntax
///         - Input/Output binding to workflow data (not workflow interface)
///         - Previous step access to invoked workflow results
///         - Parallel execution with workflow invocations
/// </summary>
public class WorkflowInvocationSyntaxTests
{
    [Fact]
    public void Build_WorkflowInvocationByType_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that workflow invocation by type syntax compiles correctly
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<PrepareDataAsyncActivity>(setup => setup
                    .Input(act => act.RawData).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.PreparedData).To(ctx => ctx.WorkflowData.ProcessedOrder))

                // Invoke another workflow - same pattern as activities
                .Invoke<ValidationWorkflow, ValidationWorkflowData>(setup => setup
                    .Input(data => data.DataToValidate)
                    .From(ctx => ctx.PreviousStep.PreparedData)
                    .Input(data => data.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId)
                    .Output(data => data.ValidationResult).To(ctx => ctx.WorkflowData.ValidationResult)
                    .Output(data => data.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors))

                // Next step gets access to the invoked workflow data as previous step
                .Step<ProcessValidatedDataAsyncActivity>(setup => setup
                    .Input(act => act.IsValid)
                    .From(ctx => ctx.PreviousStep.ValidationResult)
                    .Input(act => act.ProcessingData).From(ctx => ctx.WorkflowData.ProcessedOrder)
                    .Output(act => act.FinalResult).To(ctx => ctx.WorkflowData.ProcessedOrder)),
            "WorkflowInvocationByTypeTest");

        // Assert - Verify workflow definition was created successfully
        Assert.NotNull(definition);
        Assert.Equal("WorkflowInvocationByTypeTest", definition.Name);
        Assert.Equal(1, definition.Version);
        Assert.Equal(3, definition.Steps.Count); // PrepareData + Invoke + ProcessValidatedData
        Assert.Equal(typeof(OrderWorkflowData), definition.WorkflowDataType);
    }

    [Fact]
    public void Build_WorkflowInvocationByNameAndVersion_ShouldCompileSuccessfully()
    {
        // Arrange & Act - This test verifies that workflow invocation by name and version syntax compiles correctly
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<PrepareDataAsyncActivity>(setup => setup
                    .Input(act => act.RawData).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.PreparedData).To(ctx => ctx.WorkflowData.ProcessedOrder))

                // Invoke workflow by name and version
                .Invoke<ValidationWorkflowData>("ValidationWorkflow", 1, setup => setup
                    .Input(data => data.DataToValidate)
                    .From(ctx => ctx.PreviousStep.PreparedData)
                    .Input(data => data.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId)
                    .Output(data => data.ValidationResult).To(ctx => ctx.WorkflowData.ValidationResult)
                    .Output(data => data.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors))
                .Step<ProcessValidatedDataAsyncActivity>(setup => setup
                    .Input(act => act.IsValid)
                    .From(ctx => ctx.PreviousStep.ValidationResult)
                    .Input(act => act.ProcessingData).From(ctx => ctx.WorkflowData.ProcessedOrder)
                    .Output(act => act.FinalResult).To(ctx => ctx.WorkflowData.ProcessedOrder)),
            "WorkflowInvocationByNameTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(3, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.WorkflowInvocation, definition.Steps[1].StepType);
    }

    // Note: Parallel execution is not yet implemented, so this test is commented out
    // Will be enabled when Parallel builder is implemented
    // [Fact]
    // public void Build_ParallelWorkflowInvocations_ShouldCompileSuccessfully()
    // {
    //     // Arrange & Act - This test verifies that parallel workflow invocations compile correctly
    //     var definition = Workflow.Build<OrderWorkflowData>(builder => builder
    //         .Step<PrepareDataAsyncActivity>(setup => setup
    //             .Input(act => act.RawData).From(ctx => ctx.WorkflowData.OrderId)
    //             .Output(act => act.PreparedData).To(ctx => ctx.WorkflowData.ProcessedOrder))

    //         .Parallel(pb => pb
    //             .Do(do1 => do1
    //                 // Invoke by name and version(1)
    //                 .Invoke<ValidationWorkflowData>("ValidationWorkflow", 1, setup => setup
    //                     .Input(data => data.DataToValidate).From(ctx => ctx.PreviousStep.PreparedData)
    //                     .Input(data => data.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId)
    //                     .Output(data => data.ValidationResult).To(ctx => ctx.WorkflowData.ValidationResult)
    //                     .Output(data => data.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors))

    //                 // Next step gets access to the invoked workflow data as previous step
    //                 .Step<ProcessValidatedDataAsyncActivity>(setup => setup
    //                     .Input(act => act.IsValid).From(ctx => ctx.PreviousStep.ValidationResult)
    //                     .Input(act => act.ProcessingData).From(ctx => ctx.WorkflowData.ProcessedOrder)
    //                     .Output(act => act.FinalResult).To(ctx => ctx.WorkflowData.ProcessedOrder)))

    //             .Do(do2 => do2
    //                 // Invoke by name and version(2)
    //                 .Invoke<ValidationWorkflowData>("ValidationWorkflow", 2, setup => setup
    //                     .Input(data => data.DataToValidate).From(ctx => ctx.PreviousStep.PreparedData)
    //                     .Input(data => data.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId)
    //                     .Output(data => data.ValidationResult).To(ctx => ctx.WorkflowData.ValidationResult)
    //                     .Output(data => data.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors)))

    //             .Do(do3 => do3
    //                 .Invoke<ValidationWorkflow, ValidationWorkflowData>(setup => setup
    //                     .Input(data => data.DataToValidate).From(ctx => ctx.PreviousStep.PreparedData)
    //                     .Input(data => data.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId)
    //                     .Output(data => data.ValidationResult).To(ctx => ctx.WorkflowData.ValidationResult)
    //                     .Output(data => data.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors)))),

    //         "ParallelWorkflowInvocationTest", 1);

    //     // Assert
    //     Assert.NotNull(definition);
    //     Assert.Equal(2, definition.Steps.Count); // PrepareData + Parallel
    //     Assert.Equal(WorkflowStepType.Parallel, definition.Steps[1].StepType);
    //     Assert.Equal(3, definition.Steps[1].ParallelBranches.Count);
    // }

    [Fact]
    public void Build_WorkflowInvocationStructure_ShouldHaveCorrectStepTypes()
    {
        // Arrange & Act
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<PrepareDataAsyncActivity>(setup => setup
                    .Input(act => act.RawData).From(ctx => ctx.WorkflowData.OrderId))
                .Invoke<ValidationWorkflow, ValidationWorkflowData>(setup => setup
                    .Input(data => data.DataToValidate).From(ctx => ctx.PreviousStep.PreparedData)
                    .Input(data => data.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId)),
            "WorkflowInvocationStructureTest");

        // Assert - Verify step types and structure
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[0].StepType);
        Assert.Equal(typeof(PrepareDataAsyncActivity), definition.Steps[0].ActivityType);

        Assert.Equal(WorkflowStepType.WorkflowInvocation, definition.Steps[1].StepType);
        Assert.Equal(typeof(ValidationWorkflow), definition.Steps[1].WorkflowType);
        // WorkflowDataType refers to the main workflow's context, not the invoked workflow's data type
        Assert.Equal(typeof(OrderWorkflowData), definition.Steps[1].WorkflowDataType);
    }

    [Fact]
    public void Build_WorkflowInvocationInputBinding_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that input binding to workflow data compiles
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<PrepareDataAsyncActivity>(setup => setup
                    .Input(act => act.RawData).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.PreparedData).To(ctx => ctx.WorkflowData.ProcessedOrder))
                .Invoke<ValidationWorkflow, ValidationWorkflowData>(setup => setup
                    .Input(data => data.DataToValidate)
                    .From(ctx => ctx.PreviousStep.PreparedData) // Access previous step
                    .Input(data => data.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId)), // Access workflow data
            "WorkflowInvocationInputBindingTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.WorkflowInvocation, definition.Steps[1].StepType);
    }

    [Fact]
    public void Build_WorkflowInvocationOutputBinding_ShouldCompileCorrectly()
    {
        // Arrange & Act - Test that output binding from workflow data compiles
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<PrepareDataAsyncActivity>(setup => setup
                    .Input(act => act.RawData).From(ctx => ctx.WorkflowData.OrderId))
                .Invoke<ValidationWorkflow, ValidationWorkflowData>(setup => setup
                    .Input(data => data.DataToValidate).From(ctx => ctx.PreviousStep.PreparedData)
                    .Input(data => data.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId)
                    .Output(data => data.ValidationResult)
                    .To(ctx => ctx.WorkflowData.ValidationResult) // Bind to workflow data
                    .Output(data => data.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors)),
            "WorkflowInvocationOutputBindingTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(2, definition.Steps.Count);
    }

    [Fact]
    public void Build_StepAfterWorkflowInvocation_ShouldAccessInvokedWorkflowResult()
    {
        // Arrange & Act - Test that step after workflow invocation can access invoked workflow results
        var definition = Workflow.Build<OrderWorkflowData>(builder => builder
                .Step<PrepareDataAsyncActivity>(setup => setup
                    .Input(act => act.RawData).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.PreparedData).To(ctx => ctx.WorkflowData.ProcessedOrder))
                .Invoke<ValidationWorkflow, ValidationWorkflowData>(setup => setup
                    .Input(data => data.DataToValidate).From(ctx => ctx.PreviousStep.PreparedData)
                    .Input(data => data.WorkflowId).From(ctx => ctx.WorkflowData.WorkflowId)
                    .Output(data => data.ValidationResult).To(ctx => ctx.WorkflowData.ValidationResult)
                    .Output(data => data.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors))
                .Step<ProcessValidatedDataAsyncActivity>(setup => setup
                    .Input(act => act.IsValid)
                    .From(ctx =>
                        ctx.PreviousStep.ValidationResult) // Should access ValidationWorkflowData.ValidationResult
                    .Input(act => act.ProcessingData).From(ctx => ctx.WorkflowData.ProcessedOrder)),
            "StepAfterWorkflowInvocationTest");

        // Assert
        Assert.NotNull(definition);
        Assert.Equal(3, definition.Steps.Count);
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[2].StepType);
        Assert.Equal(typeof(ProcessValidatedDataAsyncActivity), definition.Steps[2].ActivityType);
    }
}
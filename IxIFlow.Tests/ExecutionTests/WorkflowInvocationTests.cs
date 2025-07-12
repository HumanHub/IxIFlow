using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

public class WorkflowInvocationTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkflowEngine _workflowEngine;

    public WorkflowInvocationTests()
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
    public async Task TestWorkflowInvocation_SuccessfulExecution()
    {
        // Arrange
        var builder = new WorkflowBuilder<ParentWorkflowData>("ParentWorkflow");
        var parentWorkflow = new ParentWorkflow();
        parentWorkflow.Build(builder);
        var definition = builder.Build();

        var workflowData = new ParentWorkflowData
        {
            OrderId = "ORD-001",
            ProcessingCount = 42
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ParentWorkflowData)result.WorkflowData;

        // Verify expected results
        var expectedProcessedData = "Prepared: ORD-001";
        var expectedSubWorkflowOutput = "Processed[42]: Prepared: ORD-001";
        var expectedValidationResult = "Processed[42]: Processed[42]: Prepared: ORD-001";

        Assert.Equal(expectedProcessedData, finalData.ProcessedData);
        Assert.Equal(expectedSubWorkflowOutput, finalData.SubWorkflowOutput);
        Assert.Equal(expectedValidationResult, finalData.ValidationResult);
    }

    [Fact]
    public async Task TestWorkflowInvocation_UsingWorkflowFactory()
    {
        // Arrange
        var definition = Workflow
            .Create<ParentWorkflowData>("ParentWorkflowFactory")
            // Step 1: Prepare data
            .Step<PrepareDataActivity>(setup => setup
                .Input(act => act.RawInput).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.PreparedOutput).To(ctx => ctx.WorkflowData.ProcessedData))

            // Step 2: Invoke child workflow
            .Invoke<ChildWorkflow, ChildWorkflowData>(setup => setup
                .Input(child => child.InputData).From(ctx => ctx.PreviousStep.PreparedOutput)
                .Input(child => child.ProcessingStep).From(ctx => ctx.WorkflowData.ProcessingCount)
                .Output(child => child.ProcessedResult).To(ctx => ctx.WorkflowData.SubWorkflowOutput))

            // Step 3: Use child workflow result
            .Step<ProcessDataActivity>(setup => setup
                .Input(act => act.DataToProcess).From(ctx => ctx.PreviousStep.ProcessedResult)
                .Input(act => act.StepNumber).From(ctx => ctx.WorkflowData.ProcessingCount)
                .Output(act => act.ProcessedData).To(ctx => ctx.WorkflowData.ValidationResult))
            .Build();

        var workflowData = new ParentWorkflowData
        {
            OrderId = "ORD-002",
            ProcessingCount = 100
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ParentWorkflowData)result.WorkflowData;

        // Verify expected results
        var expectedProcessedData = "Prepared: ORD-002";
        var expectedSubWorkflowOutput = "Processed[100]: Prepared: ORD-002";
        var expectedValidationResult = "Processed[100]: Processed[100]: Prepared: ORD-002";

        Assert.Equal(expectedProcessedData, finalData.ProcessedData);
        Assert.Equal(expectedSubWorkflowOutput, finalData.SubWorkflowOutput);
        Assert.Equal(expectedValidationResult, finalData.ValidationResult);
    }

    [Fact]
    public async Task TestWorkflowInvocation_ContextPropagation()
    {
        // Arrange - Create a workflow that tests context propagation through invocation
        var definition = Workflow
            .Create<ParentWorkflowData>("ContextPropagationWorkflow")
            // Step 1: Set initial value
            .Step<PrepareDataActivity>(setup => setup
                .Input(act => act.RawInput).From(ctx => ctx.WorkflowData.OrderId)
                .Output(act => act.PreparedOutput).To(ctx => ctx.WorkflowData.ProcessedData))

            // Step 2: Invoke child workflow
            .Invoke<ChildWorkflow, ChildWorkflowData>(setup => setup
                .Input(child => child.InputData).From(ctx => ctx.PreviousStep.PreparedOutput)
                .Input(child => child.ProcessingStep).From(ctx => ctx.WorkflowData.ProcessingCount)
                .Output(child => child.ProcessedResult).To(ctx => ctx.WorkflowData.SubWorkflowOutput))

            // Step 3: Verify context propagation - PreviousStep should be the child workflow result
            .Step<ProcessDataActivity>(setup => setup
                .Input(act => act.DataToProcess).From(ctx => ctx.PreviousStep.ProcessedResult)
                .Input(act => act.StepNumber).From(ctx => ctx.WorkflowData.ProcessingCount)
                .Output(act => act.ProcessedData).To(ctx => ctx.WorkflowData.ValidationResult))
            .Build();

        var workflowData = new ParentWorkflowData
        {
            OrderId = "ORD-003",
            ProcessingCount = 200
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ParentWorkflowData)result.WorkflowData;

        // The key test here is that ctx.PreviousStep.ProcessedResult correctly accessed
        // the child workflow's output, proving context propagation works
        Assert.Equal("Processed[200]: Processed[200]: Prepared: ORD-003", finalData.ValidationResult);
    }

    // Child workflow data model
    public class ChildWorkflowData
    {
        public string InputData { get; set; } = string.Empty;
        public int ProcessingStep { get; set; }
        public string ProcessedResult { get; set; } = string.Empty;
    }

    // Parent workflow data model
    public class ParentWorkflowData
    {
        public string OrderId { get; set; } = string.Empty;
        public string ProcessedData { get; set; } = string.Empty;
        public string ValidationResult { get; set; } = string.Empty;
        public string SubWorkflowOutput { get; set; } = string.Empty;
        public int ProcessingCount { get; set; }
    }

    // Simple activity for parent workflow
    public class PrepareDataActivity : IAsyncActivity
    {
        public string RawInput { get; set; } = string.Empty;
        public string PreparedOutput { get; set; } = string.Empty;

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            PreparedOutput = $"Prepared: {RawInput}";
            await Task.CompletedTask;
        }
    }

    // Simple activity for child workflow
    public class ProcessDataActivity : IAsyncActivity
    {
        public string DataToProcess { get; set; } = string.Empty;
        public string ProcessedData { get; set; } = string.Empty;
        public int StepNumber { get; set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ProcessedData = $"Processed[{StepNumber}]: {DataToProcess}";
            await Task.CompletedTask;
        }
    }

    // Child workflow definition
    public class ChildWorkflow : IWorkflow<ChildWorkflowData>
    {
        public int Version { get; } = 1;

        public void Build(IWorkflowBuilder<ChildWorkflowData> builder)
        {
            builder
                .Step<ProcessDataActivity>(setup => setup
                    .Input(act => act.DataToProcess).From(ctx => ctx.WorkflowData.InputData)
                    .Input(act => act.StepNumber).From(ctx => ctx.WorkflowData.ProcessingStep)
                    .Output(act => act.ProcessedData).To(ctx => ctx.WorkflowData.ProcessedResult));
        }
    }

    // Parent workflow definition
    public class ParentWorkflow : IWorkflow<ParentWorkflowData>
    {
        public int Version { get; } = 1;

        public void Build(IWorkflowBuilder<ParentWorkflowData> builder)
        {
            builder
                // Step 1: Prepare data
                .Step<PrepareDataActivity>(setup => setup
                    .Input(act => act.RawInput).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.PreparedOutput).To(ctx => ctx.WorkflowData.ProcessedData))

                // Step 2: Invoke child workflow
                .Invoke<ChildWorkflow, ChildWorkflowData>(setup => setup
                    .Input(child => child.InputData).From(ctx => ctx.PreviousStep.PreparedOutput)
                    .Input(child => child.ProcessingStep).From(ctx => ctx.WorkflowData.ProcessingCount)
                    .Output(child => child.ProcessedResult).To(ctx => ctx.WorkflowData.SubWorkflowOutput))

                // Step 3: Use child workflow result
                .Step<ProcessDataActivity>(setup => setup
                    .Input(act => act.DataToProcess).From(ctx => ctx.PreviousStep.ProcessedResult)
                    .Input(act => act.StepNumber).From(ctx => ctx.WorkflowData.ProcessingCount)
                    .Output(act => act.ProcessedData).To(ctx => ctx.WorkflowData.ValidationResult));
        }
    }
}
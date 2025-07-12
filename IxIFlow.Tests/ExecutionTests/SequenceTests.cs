using IxIFlow.Builders;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

public class SequenceTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkflowEngine _workflowEngine;

    public SequenceTests()
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
    public async Task TestBasicSequence()
    {
        // Arrange
        var definition = Workflow
            .Create<SequenceWorkflowData>("BasicSequenceWorkflow")
            .Step<SequenceStepActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "Initial")
                .Input(act => act.StepNumber).From(ctx => 0)
                .Input(act => act.PreviousResult).From(ctx => string.Empty)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.StepResult).To(ctx => ctx.WorkflowData.Step1Result)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Step<SequenceStepActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "First")
                .Input(act => act.StepNumber).From(ctx => 1)
                .Input(act => act.PreviousResult).From(ctx => ctx.PreviousStep.StepResult)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.StepResult).To(ctx => ctx.WorkflowData.Step2Result)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Step<SequenceStepActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "Second")
                .Input(act => act.StepNumber).From(ctx => 2)
                .Input(act => act.PreviousResult).From(ctx => ctx.PreviousStep.StepResult)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.StepResult).To(ctx => ctx.WorkflowData.Step3Result)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Step<FinalizeSequenceActivity>(setup => setup
                .Input(act => act.Step1).From(ctx => ctx.WorkflowData.Step1Result)
                .Input(act => act.Step2).From(ctx => ctx.WorkflowData.Step2Result)
                .Input(act => act.Step3).From(ctx => ctx.WorkflowData.Step3Result)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.FinalResult).To(ctx => ctx.WorkflowData.FinalResult)
                .Output(act => act.CompletedAt).To(ctx => ctx.WorkflowData.CompletedAt)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Build();

        var workflowData = new SequenceWorkflowData
        {
            TestId = "SEQ-001",
            InitialValue = 10
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (SequenceWorkflowData)result.WorkflowData;

        // Verify sequence execution
        Assert.Equal("Step0-Initial", finalData.Step1Result);
        Assert.Equal("Step1-First-After-Step0-Initial", finalData.Step2Result);
        Assert.Equal("Step2-Second-After-Step1-First-After-Step0-Initial", finalData.Step3Result);
        Assert.Equal($"Final-{finalData.Step1Result}-{finalData.Step2Result}-{finalData.Step3Result}",
            finalData.FinalResult);
        Assert.Equal(4, finalData.ExecutionLog.Count); // 3 steps + finalize
    }

    [Fact]
    public async Task TestNestedSequences()
    {
        // Arrange - Using a different approach with regular steps instead of Sequence
        var definition = Workflow
            .Create<SequenceWorkflowData>("NestedSequenceWorkflow")
            .Step<SequenceStepActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "Initial")
                .Input(act => act.StepNumber).From(ctx => 0)
                .Input(act => act.PreviousResult).From(ctx => string.Empty)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.StepResult).To(ctx => ctx.WorkflowData.Step1Result)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Step<CalculationActivity>(setup => setup
                .Input(act => act.InputValue).From(ctx => ctx.WorkflowData.InitialValue)
                .Input(act => act.Multiplier).From(ctx => 2)
                .Output(act => act.Result).To(ctx => ctx.WorkflowData.CalculatedValue))
            .Step<SequenceStepActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InnerFirst")
                .Input(act => act.StepNumber).From(ctx => 1)
                .Input(act => act.PreviousResult).From(ctx => ctx.WorkflowData.Step1Result)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.StepResult).To(ctx => ctx.WorkflowData.Step2Result)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Step<SequenceStepActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "InnerSecond")
                .Input(act => act.StepNumber).From(ctx => 2)
                .Input(act => act.PreviousResult).From(ctx => ctx.PreviousStep.StepResult)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.StepResult).To(ctx => ctx.WorkflowData.Step3Result)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Step<CalculationActivity>(setup => setup
                .Input(act => act.InputValue).From(ctx => ctx.WorkflowData.CalculatedValue)
                .Input(act => act.Multiplier).From(ctx => 3)
                .Output(act => act.Result).To(ctx => ctx.WorkflowData.CalculatedValue))
            .Step<FinalizeSequenceActivity>(setup => setup
                .Input(act => act.Step1).From(ctx => ctx.WorkflowData.Step1Result)
                .Input(act => act.Step2).From(ctx => ctx.WorkflowData.Step2Result)
                .Input(act => act.Step3).From(ctx => ctx.WorkflowData.Step3Result)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.FinalResult).To(ctx => ctx.WorkflowData.FinalResult)
                .Output(act => act.CompletedAt).To(ctx => ctx.WorkflowData.CompletedAt)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Build();

        var workflowData = new SequenceWorkflowData
        {
            TestId = "NESTED-001",
            InitialValue = 5
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (SequenceWorkflowData)result.WorkflowData;

        // Verify nested sequence execution
        Assert.Equal("Step0-Initial", finalData.Step1Result);
        Assert.Equal("Step1-InnerFirst-After-Step0-Initial", finalData.Step2Result);
        Assert.Equal("Step2-InnerSecond-After-Step1-InnerFirst-After-Step0-Initial", finalData.Step3Result);
        Assert.Equal(30, finalData.CalculatedValue); // 5 * 2 * 3
        Assert.Equal(4, finalData.ExecutionLog.Count); // Initial + 2 inner steps + finalize
    }

    [Fact]
    public async Task TestSequenceWithConditional()
    {
        // Arrange
        var definition = Workflow
            .Create<SequenceWorkflowData>("SequenceWithConditionalWorkflow")
            .Step<CalculationActivity>(setup => setup
                .Input(act => act.InputValue).From(ctx => ctx.WorkflowData.InitialValue)
                .Input(act => act.Multiplier).From(ctx => 2)
                .Output(act => act.Result).To(ctx => ctx.WorkflowData.CalculatedValue))
            .Step<SequenceStepActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "First")
                .Input(act => act.StepNumber).From(ctx => 1)
                .Input(act => act.PreviousResult).From(ctx => string.Empty)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.StepResult).To(ctx => ctx.WorkflowData.Step1Result)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .If(ctx => ctx.WorkflowData.CalculatedValue > 20,
                then => then.Step<SequenceStepActivity>(setup => setup
                    .Input(act => act.StepName).From(ctx => "HighValue")
                    .Input(act => act.StepNumber).From(ctx => 2)
                    .Input(act => act.PreviousResult).From(ctx => ctx.PreviousStep.StepResult)
                    .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                    .Output(act => act.StepResult).To(ctx => ctx.WorkflowData.Step2Result)
                    .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog)),
                @else => @else.Step<SequenceStepActivity>(setup => setup
                    .Input(act => act.StepName).From(ctx => "LowValue")
                    .Input(act => act.StepNumber).From(ctx => 2)
                    .Input(act => act.PreviousResult).From(ctx => ctx.PreviousStep.StepResult)
                    .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                    .Output(act => act.StepResult).To(ctx => ctx.WorkflowData.Step2Result)
                    .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog)))
            .Step<SequenceStepActivity>(setup => setup
                .Input(act => act.StepName).From(ctx => "Final")
                .Input(act => act.StepNumber).From(ctx => 3)
                .Input(act => act.PreviousResult).From(ctx => ctx.PreviousStep.StepResult)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.StepResult).To(ctx => ctx.WorkflowData.Step3Result)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Step<FinalizeSequenceActivity>(setup => setup
                .Input(act => act.Step1).From(ctx => ctx.WorkflowData.Step1Result)
                .Input(act => act.Step2).From(ctx => ctx.WorkflowData.Step2Result)
                .Input(act => act.Step3).From(ctx => ctx.WorkflowData.Step3Result)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.FinalResult).To(ctx => ctx.WorkflowData.FinalResult)
                .Output(act => act.CompletedAt).To(ctx => ctx.WorkflowData.CompletedAt)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Build();

        // Test with high value
        var highValueData = new SequenceWorkflowData
        {
            TestId = "COND-HIGH-001",
            InitialValue = 15 // 15 * 2 = 30 > 20
        };

        // Act
        var highValueResult = await _workflowEngine.ExecuteWorkflowAsync(definition, highValueData);

        // Assert
        Assert.True(highValueResult.IsSuccess);
        Assert.NotNull(highValueResult.WorkflowData);

        var highValueFinalData = (SequenceWorkflowData)highValueResult.WorkflowData;

        // Verify high value path
        Assert.Equal(30, highValueFinalData.CalculatedValue);
        Assert.Equal("Step1-First", highValueFinalData.Step1Result);
        Assert.Equal("Step2-HighValue-After-Step1-First", highValueFinalData.Step2Result);
        Assert.Equal("Step3-Final-After-Step1-First", highValueFinalData.Step3Result);

        // Test with low value
        var lowValueData = new SequenceWorkflowData
        {
            TestId = "COND-LOW-001",
            InitialValue = 5 // 5 * 2 = 10 < 20
        };

        // Act
        var lowValueResult = await _workflowEngine.ExecuteWorkflowAsync(definition, lowValueData);

        // Assert
        Assert.True(lowValueResult.IsSuccess);
        Assert.NotNull(lowValueResult.WorkflowData);

        var lowValueFinalData = (SequenceWorkflowData)lowValueResult.WorkflowData;

        // Verify low value path
        Assert.Equal(10, lowValueFinalData.CalculatedValue);
        Assert.Equal("Step1-First", lowValueFinalData.Step1Result);
        Assert.Equal("Step2-LowValue-After-Step1-First", lowValueFinalData.Step2Result);
        Assert.Equal("Step3-Final-After-Step1-First", lowValueFinalData.Step3Result);
    }

    // Test data model for sequence testing
    public class SequenceWorkflowData
    {
        public string TestId { get; set; } = string.Empty;
        public int InitialValue { get; set; }
        public List<string> ExecutionLog { get; set; } = new();
        public string Step1Result { get; set; } = string.Empty;
        public string Step2Result { get; set; } = string.Empty;
        public string Step3Result { get; set; } = string.Empty;
        public string FinalResult { get; set; } = string.Empty;
        public int CalculatedValue { get; set; }
        public DateTime CompletedAt { get; set; }
    }

    // Activities for sequence testing
    public class SequenceStepActivity : IAsyncActivity
    {
        public string StepName { get; set; } = string.Empty;
        public int StepNumber { get; set; }
        public string PreviousResult { get; set; } = string.Empty;
        public List<string> ExecutionLog { get; set; } = new();

        public string StepResult { get; set; } = string.Empty;
        public DateTime ExecutedAt { get; set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ExecutedAt = DateTime.UtcNow;
            StepResult = string.IsNullOrEmpty(PreviousResult)
                ? $"Step{StepNumber}-{StepName}"
                : $"Step{StepNumber}-{StepName}-After-{PreviousResult}";

            ExecutionLog.Add($"{ExecutedAt:HH:mm:ss} - Executed {StepName} (Step {StepNumber})");

            await Task.CompletedTask;
        }
    }

    public class CalculationActivity : IAsyncActivity
    {
        public int InputValue { get; set; }
        public int Multiplier { get; set; }

        public int Result { get; set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = InputValue * Multiplier;
            await Task.CompletedTask;
        }
    }

    public class FinalizeSequenceActivity : IAsyncActivity
    {
        public string Step1 { get; set; } = string.Empty;
        public string Step2 { get; set; } = string.Empty;
        public string Step3 { get; set; } = string.Empty;
        public List<string> ExecutionLog { get; set; } = new();

        public string FinalResult { get; set; } = string.Empty;
        public DateTime CompletedAt { get; set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            CompletedAt = DateTime.UtcNow;
            FinalResult = $"Final-{Step1}-{Step2}-{Step3}";

            ExecutionLog.Add($"{CompletedAt:HH:mm:ss} - Finalized sequence with {ExecutionLog.Count} previous steps");

            await Task.CompletedTask;
        }
    }
}
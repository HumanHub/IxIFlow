using IxIFlow.Builders;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

public class LoopTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkflowEngine _workflowEngine;

    public LoopTests()
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
    public async Task TestDoWhileLoop()
    {
        // Arrange
        var definition = Workflow
            .Create<LoopWorkflowData>("DoWhileLoopWorkflow")
            .Step<InitializeLoopActivity>(setup => setup
                .Input(act => act.TestId).From(ctx => ctx.WorkflowData.TestId)
                .Output(act => act.InitialValue).To(ctx => ctx.WorkflowData.Counter))
            .DoWhile(
                loopBody =>
                {
                    loopBody.Step<LoopIterationActivity>(setup => setup
                        .Input(act => act.CurrentCounter).From(ctx => ctx.WorkflowData.Counter)
                        .Input(act => act.IterationNumber).From(ctx => ctx.WorkflowData.Counter + 1)
                        .Input(act => act.IterationResults).From(ctx => ctx.WorkflowData.IterationResults)
                        .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                        .Output(act => act.NewCounter).To(ctx => ctx.WorkflowData.Counter)
                        .Output(act => act.IterationResults).To(ctx => ctx.WorkflowData.IterationResults)
                        .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog));
                },
                condition => condition.WorkflowData.Counter < condition.WorkflowData.MaxIterations
            )
            .Step<FinalizeLoopActivity>(setup => setup
                .Input(act => act.FinalCounter).From(ctx => ctx.WorkflowData.Counter)
                .Input(act => act.IterationResults).From(ctx => ctx.WorkflowData.IterationResults)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.Summary).To(ctx => ctx.WorkflowData.FinalSummary)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Build();

        var workflowData = new LoopWorkflowData
        {
            TestId = "DOWHILE-TEST-001",
            Counter = 0,
            MaxIterations = 3,
            ExecutionLog = new List<string>(),
            IterationResults = new List<int>()
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (LoopWorkflowData)result.WorkflowData;

        // Verify DoWhile behavior
        Assert.Equal(3, finalData.Counter);
        Assert.Equal(3, finalData.IterationResults.Count);
        Assert.Equal(1, finalData.IterationResults[0]);
        Assert.Equal(2, finalData.IterationResults[1]);
        Assert.Equal(3, finalData.IterationResults[2]);
        Assert.Contains("Completed 3 iterations", finalData.FinalSummary);
    }

    [Fact]
    public async Task TestWhileDoLoop()
    {
        // Arrange
        var definition = Workflow
            .Create<LoopWorkflowData>("WhileDoLoopWorkflow")
            .Step<InitializeLoopActivity>(setup => setup
                .Input(act => act.TestId).From(ctx => ctx.WorkflowData.TestId)
                .Output(act => act.InitialValue).To(ctx => ctx.WorkflowData.Counter))
            .WhileDo(
                condition => condition.WorkflowData.Counter < condition.WorkflowData.MaxIterations,
                loopBody =>
                {
                    loopBody.Step<LoopIterationActivity>(setup => setup
                        .Input(act => act.CurrentCounter).From(ctx => ctx.WorkflowData.Counter)
                        .Input(act => act.IterationNumber).From(ctx => ctx.WorkflowData.Counter + 1)
                        .Input(act => act.IterationResults).From(ctx => ctx.WorkflowData.IterationResults)
                        .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                        .Output(act => act.NewCounter).To(ctx => ctx.WorkflowData.Counter)
                        .Output(act => act.IterationResults).To(ctx => ctx.WorkflowData.IterationResults)
                        .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog));
                }
            )
            .Step<FinalizeLoopActivity>(setup => setup
                .Input(act => act.FinalCounter).From(ctx => ctx.WorkflowData.Counter)
                .Input(act => act.IterationResults).From(ctx => ctx.WorkflowData.IterationResults)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.Summary).To(ctx => ctx.WorkflowData.FinalSummary)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Build();

        var workflowData = new LoopWorkflowData
        {
            TestId = "WHILEDO-TEST-001",
            Counter = 0,
            MaxIterations = 3,
            ExecutionLog = new List<string>(),
            IterationResults = new List<int>()
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (LoopWorkflowData)result.WorkflowData;

        // Verify WhileDo behavior
        Assert.Equal(3, finalData.Counter);
        Assert.Equal(3, finalData.IterationResults.Count);
        Assert.Equal(1, finalData.IterationResults[0]);
        Assert.Equal(2, finalData.IterationResults[1]);
        Assert.Equal(3, finalData.IterationResults[2]);
        Assert.Contains("Completed 3 iterations", finalData.FinalSummary);
    }

    [Fact]
    public async Task TestDoWhileWithZeroIterations()
    {
        // Arrange - DoWhile should always execute at least once
        var definition = Workflow
            .Create<LoopWorkflowData>("DoWhileZeroIterationsWorkflow")
            .Step<InitializeLoopActivity>(setup => setup
                .Input(act => act.TestId).From(ctx => ctx.WorkflowData.TestId)
                .Output(act => act.InitialValue).To(ctx => ctx.WorkflowData.Counter))
            .DoWhile(
                loopBody =>
                {
                    loopBody.Step<LoopIterationActivity>(setup => setup
                        .Input(act => act.CurrentCounter).From(ctx => ctx.WorkflowData.Counter)
                        .Input(act => act.IterationNumber).From(ctx => ctx.WorkflowData.Counter + 1)
                        .Input(act => act.IterationResults).From(ctx => ctx.WorkflowData.IterationResults)
                        .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                        .Output(act => act.NewCounter).To(ctx => ctx.WorkflowData.Counter)
                        .Output(act => act.IterationResults).To(ctx => ctx.WorkflowData.IterationResults)
                        .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog));
                },
                condition => condition.WorkflowData.MaxIterations > 0 // Will be false immediately
            )
            .Step<FinalizeLoopActivity>(setup => setup
                .Input(act => act.FinalCounter).From(ctx => ctx.WorkflowData.Counter)
                .Input(act => act.IterationResults).From(ctx => ctx.WorkflowData.IterationResults)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.Summary).To(ctx => ctx.WorkflowData.FinalSummary)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Build();

        var workflowData = new LoopWorkflowData
        {
            TestId = "DOWHILE-ZERO-TEST-001",
            Counter = 0,
            MaxIterations = 0, // Condition will be false immediately
            ExecutionLog = new List<string>(),
            IterationResults = new List<int>()
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (LoopWorkflowData)result.WorkflowData;

        // Verify DoWhile executes at least once
        Assert.Equal(1, finalData.Counter);
        Assert.Equal(1, finalData.IterationResults.Count);
        Assert.Equal(1, finalData.IterationResults[0]);
        Assert.Contains("Completed 1 iterations", finalData.FinalSummary);
    }

    [Fact]
    public async Task TestWhileDoWithZeroIterations()
    {
        // Arrange - WhileDo should not execute if condition is false
        var definition = Workflow
            .Create<LoopWorkflowData>("WhileDoZeroIterationsWorkflow")
            .Step<InitializeLoopActivity>(setup => setup
                .Input(act => act.TestId).From(ctx => ctx.WorkflowData.TestId)
                .Output(act => act.InitialValue).To(ctx => ctx.WorkflowData.Counter))
            .WhileDo(
                condition => condition.WorkflowData.MaxIterations > 0, // Will be false immediately
                loopBody =>
                {
                    loopBody.Step<LoopIterationActivity>(setup => setup
                        .Input(act => act.CurrentCounter).From(ctx => ctx.WorkflowData.Counter)
                        .Input(act => act.IterationNumber).From(ctx => ctx.WorkflowData.Counter + 1)
                        .Input(act => act.IterationResults).From(ctx => ctx.WorkflowData.IterationResults)
                        .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                        .Output(act => act.NewCounter).To(ctx => ctx.WorkflowData.Counter)
                        .Output(act => act.IterationResults).To(ctx => ctx.WorkflowData.IterationResults)
                        .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog));
                }
            )
            .Step<FinalizeLoopActivity>(setup => setup
                .Input(act => act.FinalCounter).From(ctx => ctx.WorkflowData.Counter)
                .Input(act => act.IterationResults).From(ctx => ctx.WorkflowData.IterationResults)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.Summary).To(ctx => ctx.WorkflowData.FinalSummary)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Build();

        var workflowData = new LoopWorkflowData
        {
            TestId = "WHILEDO-ZERO-TEST-001",
            Counter = 0,
            MaxIterations = 0, // Condition will be false immediately
            ExecutionLog = new List<string>(),
            IterationResults = new List<int>()
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (LoopWorkflowData)result.WorkflowData;

        // Verify WhileDo doesn't execute when condition is false
        Assert.Equal(0, finalData.Counter);
        Assert.Empty(finalData.IterationResults);
        Assert.Contains("Completed 0 iterations", finalData.FinalSummary);
    }

    [Fact]
    public async Task TestNestedLoops()
    {
        // Arrange
        var definition = Workflow
            .Create<LoopWorkflowData>("NestedLoopsWorkflow")
            .Step<InitializeLoopActivity>(setup => setup
                .Input(act => act.TestId).From(ctx => ctx.WorkflowData.TestId)
                .Output(act => act.InitialValue).To(ctx => ctx.WorkflowData.Counter))
            .DoWhile(
                outerLoop =>
                {
                    outerLoop
                        .Step<OuterLoopActivity>(setup => setup
                            .Input(act => act.OuterCounter).From(ctx => ctx.WorkflowData.Counter)
                            .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                            .Output(act => act.InnerCounter).To(ctx => ctx.WorkflowData.InnerCounter)
                            .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
                        .DoWhile(
                            innerLoop =>
                            {
                                innerLoop.Step<InnerLoopActivity>(setup => setup
                                    .Input(act => act.InnerCounter).From(ctx => ctx.WorkflowData.InnerCounter)
                                    .Input(act => act.OuterCounter).From(ctx => ctx.WorkflowData.Counter)
                                    .Input(act => act.IterationResults).From(ctx => ctx.WorkflowData.IterationResults)
                                    .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                                    .Output(act => act.NewInnerCounter).To(ctx => ctx.WorkflowData.InnerCounter)
                                    .Output(act => act.IterationResults).To(ctx => ctx.WorkflowData.IterationResults)
                                    .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog));
                            },
                            condition => condition.WorkflowData.InnerCounter < condition.WorkflowData.InnerMaxIterations
                        )
                        .Step<LoopIterationActivity>(setup => setup
                            .Input(act => act.CurrentCounter).From(ctx => ctx.WorkflowData.Counter)
                            .Input(act => act.IterationNumber).From(ctx => ctx.WorkflowData.Counter + 1)
                            .Input(act => act.IterationResults).From(ctx => ctx.WorkflowData.IterationResults)
                            .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                            .Output(act => act.NewCounter).To(ctx => ctx.WorkflowData.Counter)
                            .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog));
                },
                condition => condition.WorkflowData.Counter < condition.WorkflowData.MaxIterations
            )
            .Step<FinalizeLoopActivity>(setup => setup
                .Input(act => act.FinalCounter).From(ctx => ctx.WorkflowData.Counter)
                .Input(act => act.IterationResults).From(ctx => ctx.WorkflowData.IterationResults)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.Summary).To(ctx => ctx.WorkflowData.FinalSummary)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Build();

        var workflowData = new LoopWorkflowData
        {
            TestId = "NESTED-LOOPS-TEST-001",
            Counter = 0,
            MaxIterations = 2, // Outer loop iterations
            InnerCounter = 0,
            InnerMaxIterations = 3, // Inner loop iterations
            ExecutionLog = new List<string>(),
            IterationResults = new List<int>()
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (LoopWorkflowData)result.WorkflowData;

        // Verify nested loops behavior
        Assert.Equal(2, finalData.Counter);
        Assert.Equal(8, finalData.IterationResults.Count); // 2 outer loops * 3 inner loops + 2 outer loop steps

        // Verify inner loop results for first outer iteration (0)
        Assert.Equal(1, finalData.IterationResults[0]);
        Assert.Equal(2, finalData.IterationResults[1]);
        Assert.Equal(3, finalData.IterationResults[2]);
        Assert.Equal(1, finalData.IterationResults[3]); // From LoopIterationActivity after inner loop

        // Verify inner loop results for second outer iteration (1)
        Assert.Equal(101, finalData.IterationResults[4]);
        Assert.Equal(102, finalData.IterationResults[5]);
        Assert.Equal(103, finalData.IterationResults[6]);
        Assert.Equal(2, finalData.IterationResults[7]); // From LoopIterationActivity after inner loop

        Assert.Contains("Completed 8 iterations", finalData.FinalSummary);
    }

    [Fact]
    public async Task TestLoopWithPreviousStepAccess()
    {
        // Arrange
        var definition = Workflow
            .Create<LoopWorkflowData>("LoopWithPreviousStepWorkflow")
            .Step<InitializeLoopActivity>(setup => setup
                .Input(act => act.TestId).From(ctx => ctx.WorkflowData.TestId)
                .Output(act => act.InitialValue).To(ctx => ctx.WorkflowData.Counter))
            .DoWhile(
                loopBody =>
                {
                    loopBody.Step<LoopIterationActivity>(setup => setup
                        .Input(act => act.CurrentCounter).From(ctx => ctx.WorkflowData.Counter)
                        .Input(act => act.IterationNumber)
                        .From(ctx =>
                            ctx.PreviousStep.InitialValue + ctx.WorkflowData.Counter + 1) // Access previous step
                        .Input(act => act.IterationResults).From(ctx => ctx.WorkflowData.IterationResults)
                        .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                        .Output(act => act.NewCounter).To(ctx => ctx.WorkflowData.Counter)
                        .Output(act => act.IterationResults).To(ctx => ctx.WorkflowData.IterationResults)
                        .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog));
                },
                condition => condition.WorkflowData.Counter < condition.WorkflowData.MaxIterations
            )
            .Step<FinalizeLoopActivity>(setup => setup
                .Input(act => act.FinalCounter)
                .From(ctx => ctx.WorkflowData.Counter) // Access previous step from last iteration
                .Input(act => act.IterationResults).From(ctx => ctx.WorkflowData.IterationResults)
                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                .Output(act => act.Summary).To(ctx => ctx.WorkflowData.FinalSummary)
                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
            .Build();

        var workflowData = new LoopWorkflowData
        {
            TestId = "LOOP-PREVIOUS-STEP-TEST-001",
            Counter = 0,
            MaxIterations = 3,
            ExecutionLog = new List<string>(),
            IterationResults = new List<int>()
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (LoopWorkflowData)result.WorkflowData;

        // Verify loop with previous step access
        Assert.Equal(3, finalData.Counter);
        Assert.Equal(3, finalData.IterationResults.Count);

        // Verify iteration results with previous step access
        Assert.Equal(1, finalData.IterationResults[0]); // 0 + 0 + 1 = 1
        Assert.Equal(2, finalData.IterationResults[1]); // 0 + 1 + 1 = 2
        Assert.Equal(3, finalData.IterationResults[2]); // 0 + 2 + 1 = 3

        Assert.Contains("Completed 3 iterations", finalData.FinalSummary);
    }

    // Test data model for loop testing
    public class LoopWorkflowData
    {
        public string TestId { get; set; } = string.Empty;
        public int Counter { get; set; }
        public int MaxIterations { get; set; }
        public int InnerCounter { get; set; }
        public int InnerMaxIterations { get; set; }
        public List<string> ExecutionLog { get; set; } = new();
        public List<int> IterationResults { get; set; } = new();
        public string FinalSummary { get; set; } = string.Empty;
    }

    // Activities for loop testing
    public class InitializeLoopActivity : IAsyncActivity
    {
        public string TestId { get; set; } = string.Empty;

        public int InitialValue { get; set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            InitialValue = 0;
            await Task.CompletedTask;
        }
    }

    public class LoopIterationActivity : IAsyncActivity
    {
        public int CurrentCounter { get; set; }
        public int IterationNumber { get; set; }
        public List<int> IterationResults { get; set; } = new();
        public List<string> ExecutionLog { get; set; } = new();

        public int NewCounter { get; set; }
        public int IterationResult { get; set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            NewCounter = CurrentCounter + 1;
            IterationResult = IterationNumber;

            IterationResults.Add(IterationResult);
            ExecutionLog.Add($"Iteration {IterationNumber} executed at {DateTime.UtcNow:HH:mm:ss.fff}");

            await Task.CompletedTask;
        }
    }

    public class FinalizeLoopActivity : IAsyncActivity
    {
        public int FinalCounter { get; set; }
        public List<int> IterationResults { get; set; } = new();
        public List<string> ExecutionLog { get; set; } = new();

        public string Summary { get; set; } = string.Empty;

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Summary = $"Completed {IterationResults.Count} iterations, final counter: {FinalCounter}";
            ExecutionLog.Add($"Finalized at {DateTime.UtcNow:HH:mm:ss.fff} with {IterationResults.Count} iterations");

            await Task.CompletedTask;
        }
    }

    public class OuterLoopActivity : IAsyncActivity
    {
        public int OuterCounter { get; set; }
        public List<string> ExecutionLog { get; set; } = new();

        public int InnerCounter { get; set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            InnerCounter = 0; // Reset inner counter for each outer iteration
            ExecutionLog.Add($"Outer loop iteration {OuterCounter + 1} started at {DateTime.UtcNow:HH:mm:ss.fff}");

            await Task.CompletedTask;
        }
    }

    public class InnerLoopActivity : IAsyncActivity
    {
        public int InnerCounter { get; set; }
        public int OuterCounter { get; set; }
        public List<int> IterationResults { get; set; } = new();
        public List<string> ExecutionLog { get; set; } = new();

        public int NewInnerCounter { get; set; }
        public int IterationResult { get; set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            NewInnerCounter = InnerCounter + 1;
            IterationResult = OuterCounter * 100 + InnerCounter + 1; // Unique result combining outer and inner

            IterationResults.Add(IterationResult);
            ExecutionLog.Add(
                $"Inner loop iteration {InnerCounter + 1} of outer {OuterCounter + 1} executed at {DateTime.UtcNow:HH:mm:ss.fff}");

            await Task.CompletedTask;
        }
    }
}
using IxIFlow.Builders;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

public class ExpressionTests
{
    private readonly IExpressionEvaluator _expressionEvaluator;
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkflowEngine _workflowEngine;

    public ExpressionTests()
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
        _expressionEvaluator = _serviceProvider.GetRequiredService<IExpressionEvaluator>();
    }

    [Fact]
    public async Task TestBasicExpressions()
    {
        // Arrange
        var definition = Workflow
            .Create<ExpressionTestData>("ExpressionTestWorkflow")
            .Step<ExpressionTestActivity>(setup => setup
                // Input mappings with various expressions
                .Input(act => act.StringInput).From(ctx => ctx.WorkflowData.StringValue)
                .Input(act => act.IntInput).From(ctx => ctx.WorkflowData.IntValue)
                .Input(act => act.DecimalInput).From(ctx => ctx.WorkflowData.DecimalValue)
                .Input(act => act.BoolInput).From(ctx => ctx.WorkflowData.BoolValue)
                .Input(act => act.DateInput).From(ctx => ctx.WorkflowData.DateValue)
                .Input(act => act.ListInput).From(ctx => ctx.WorkflowData.StringList)
                .Input(act => act.DictInput).From(ctx => ctx.WorkflowData.Dictionary)
                .Input(act => act.NestedInput).From(ctx => ctx.WorkflowData.NestedData)

                // Output mappings
                .Output(act => act.StringOutput).To(ctx => ctx.WorkflowData.Result)
                .Output(act => act.CalculatedOutput).To(ctx => ctx.WorkflowData.CalculatedValue)
                .Output(act => act.ComparisonOutput).To(ctx => ctx.WorkflowData.ComparisonResult)
                .Output(act => act.ConditionalOutput).To(ctx => ctx.WorkflowData.ConditionalResult))
            .Build();

        var workflowData = new ExpressionTestData
        {
            StringValue = "Test",
            IntValue = 42,
            DecimalValue = 123.45m,
            BoolValue = true,
            DateValue = new DateTime(2025, 1, 1),
            StringList = new List<string> { "Item1", "Item2", "Item3" },
            Dictionary = new Dictionary<string, int> { { "Key1", 1 }, { "Key2", 2 } },
            NestedData = new NestedObject { Name = "Nested", Value = 100 }
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ExpressionTestData)result.WorkflowData;

        // Verify expression results
        Assert.Equal("Test-42", finalData.Result);
        Assert.Equal(84, finalData.CalculatedValue);
        Assert.True(finalData.ComparisonResult);
        Assert.Equal("True Result", finalData.ConditionalResult);
    }

    [Fact]
    public async Task TestComplexExpressions()
    {
        // Arrange - Create a workflow with complex expressions
        var definition = Workflow
            .Create<ExpressionTestData>("ComplexExpressionWorkflow")
            .Step<ExpressionTestActivity>(setup => setup
                // Complex input expressions
                .Input(act => act.StringInput)
                .From(ctx => $"{ctx.WorkflowData.StringValue}-{ctx.WorkflowData.IntValue}")
                .Input(act => act.IntInput)
                .From(ctx => ctx.WorkflowData.IntValue * 2 + ctx.WorkflowData.NestedData.Value)
                .Input(act => act.DecimalInput).From(ctx => ctx.WorkflowData.DecimalValue * 2)
                .Input(act => act.BoolInput).From(ctx =>
                    ctx.WorkflowData.IntValue > 40 && ctx.WorkflowData.StringValue.StartsWith("T"))

                // Output mappings
                .Output(act => act.StringOutput).To(ctx => ctx.WorkflowData.Result)
                .Output(act => act.CalculatedOutput).To(ctx => ctx.WorkflowData.CalculatedValue)
                .Output(act => act.ComparisonOutput).To(ctx => ctx.WorkflowData.ComparisonResult)
                .Output(act => act.ConditionalOutput).To(ctx => ctx.WorkflowData.ConditionalResult))
            .Build();

        var workflowData = new ExpressionTestData
        {
            StringValue = "Test",
            IntValue = 42,
            DecimalValue = 123.45m,
            BoolValue = false,
            NestedData = new NestedObject { Name = "Nested", Value = 100 }
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ExpressionTestData)result.WorkflowData;

        // Verify complex expression results
        Assert.Equal("Test-42-184", finalData.Result); // StringInput = "Test-42", IntInput = 184 (42*2+100)
        Assert.Equal(368, finalData.CalculatedValue); // CalculatedOutput = IntInput * 2 = 184 * 2
        Assert.True(finalData.ComparisonResult); // DecimalInput = 123.45 * 2 = 246.9 > 100
        Assert.Equal("True Result",
            finalData.ConditionalResult); // BoolInput = true (42 > 40 && "Test".StartsWith("T"))
    }

    [Fact]
    public async Task TestConditionalExpressions()
    {
        // Arrange - Create a workflow with conditional expressions
        var definition = Workflow
            .Create<ExpressionTestData>("ConditionalExpressionWorkflow")
            .Step<ExpressionTestActivity>(setup => setup
                .Input(act => act.StringInput).From(ctx => ctx.WorkflowData.StringValue)
                .Input(act => act.IntInput).From(ctx => ctx.WorkflowData.IntValue)
                .Input(act => act.DecimalInput).From(ctx => ctx.WorkflowData.DecimalValue)
                .Input(act => act.BoolInput).From(ctx => ctx.WorkflowData.BoolValue)
                .Output(act => act.StringOutput).To(ctx => ctx.WorkflowData.Result)
                .Output(act => act.CalculatedOutput).To(ctx => ctx.WorkflowData.CalculatedValue))
            .If(ctx => ctx.PreviousStep.CalculatedOutput > 50,
                then => then.Step<ExpressionTestActivity>(setup => setup
                    .Input(act => act.StringInput).From(ctx => "Large Value")
                    .Input(act => act.IntInput).From(ctx => ctx.PreviousStep.CalculatedOutput)
                    .Input(act => act.DecimalInput).From(ctx => 200m)
                    .Input(act => act.BoolInput).From(ctx => true)
                    .Output(act => act.ConditionalOutput).To(ctx => ctx.WorkflowData.ConditionalResult)),
                @else => @else.Step<ExpressionTestActivity>(setup => setup
                    .Input(act => act.StringInput).From(ctx => "Small Value")
                    .Input(act => act.IntInput).From(ctx => ctx.PreviousStep.CalculatedOutput)
                    .Input(act => act.DecimalInput).From(ctx => 50m)
                    .Input(act => act.BoolInput).From(ctx => false)
                    .Output(act => act.ConditionalOutput).To(ctx => ctx.WorkflowData.ConditionalResult)))
            .Build();

        // Test with a value that will trigger the 'then' branch
        var workflowData = new ExpressionTestData
        {
            StringValue = "Test",
            IntValue = 30, // CalculatedOutput will be 60 (30*2)
            DecimalValue = 100m,
            BoolValue = true
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ExpressionTestData)result.WorkflowData;

        // Verify conditional expression results
        Assert.Equal("Test-30", finalData.Result);
        Assert.Equal(60, finalData.CalculatedValue);
        Assert.Equal("True Result", finalData.ConditionalResult); // From the 'then' branch
    }

    // Test data model for expression testing
    public class ExpressionTestData
    {
        public string StringValue { get; set; } = string.Empty;
        public int IntValue { get; set; }
        public decimal DecimalValue { get; set; }
        public bool BoolValue { get; set; }
        public DateTime DateValue { get; set; }
        public List<string> StringList { get; set; } = new();
        public Dictionary<string, int> Dictionary { get; set; } = new();
        public NestedObject NestedData { get; set; } = new();
        public string Result { get; set; } = string.Empty;
        public int CalculatedValue { get; set; }
        public bool ComparisonResult { get; set; }
        public string ConditionalResult { get; set; } = string.Empty;
    }

    public class NestedObject
    {
        public string Name { get; set; } = string.Empty;
        public int Value { get; set; }
    }

    // Activity for testing expressions
    public class ExpressionTestActivity : IAsyncActivity
    {
        // Input properties
        public string StringInput { get; set; } = string.Empty;
        public int IntInput { get; set; }
        public decimal DecimalInput { get; set; }
        public bool BoolInput { get; set; }
        public DateTime DateInput { get; set; }
        public List<string> ListInput { get; set; } = new();
        public Dictionary<string, int> DictInput { get; set; } = new();
        public NestedObject NestedInput { get; set; } = new();

        // Output properties
        public string StringOutput { get; set; } = string.Empty;
        public int CalculatedOutput { get; set; }
        public bool ComparisonOutput { get; set; }
        public string ConditionalOutput { get; set; } = string.Empty;

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            // String concatenation
            StringOutput = $"{StringInput}-{IntInput}";

            // Calculation
            CalculatedOutput = IntInput * 2;

            // Comparison
            ComparisonOutput = DecimalInput > 100;

            // Conditional
            ConditionalOutput = BoolInput ? "True Result" : "False Result";

            await Task.CompletedTask;
        }
    }
}
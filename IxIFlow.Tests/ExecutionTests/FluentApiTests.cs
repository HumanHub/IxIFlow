using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Tests.ExecutionTests.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

public class FluentApiTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkflowEngine _workflowEngine;

    public FluentApiTests()
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
    public async Task TestWorkflowBuilder_CreatesValidDefinition()
    {
        // Arrange
        var builder = Workflow.Create<OrderWorkflowData>("TestWorkflow");

        // Act
        var definition = builder
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult)
                .Output(act => act.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors))
            .Build();

        // Assert
        Assert.NotNull(definition);
        Assert.Equal("TestWorkflow", definition.Name);
        Assert.Equal(1, definition.Version);
        Assert.Equal(typeof(OrderWorkflowData), definition.WorkflowDataType);
        Assert.Single(definition.Steps);
        Assert.Equal(typeof(ValidateOrderAsyncActivity), definition.Steps[0].ActivityType);
        Assert.Equal(2, definition.Steps[0].InputMappings.Count);
        Assert.Equal(2, definition.Steps[0].OutputMappings.Count);
    }

    [Fact]
    public async Task TestSequenceBuilder_CreatesValidDefinition()
    {
        // Arrange
        var builder = Workflow.Create<OrderWorkflowData>("SequenceTestWorkflow");

        // Act
        var definition = builder
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .Sequence(seq => seq
                .Step<CheckInventoryAsyncActivity>(setup => setup
                    .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                    .Input(act => act.IsOrderValid).From(ctx => ctx.PreviousStep.IsValid)
                    .Output(act => act.InStock).To(ctx => ctx.WorkflowData.HasInventory))
                .Step<ReserveInventoryAsyncActivity>(setup => setup
                    .Input(act => act.ProductId).From(ctx => ctx.WorkflowData.ProductId)
                    .Input(act => act.InStock).From(ctx => ctx.PreviousStep.InStock)
                    .Output(act => act.ReservationId).To(ctx => ctx.WorkflowData.ReservationId)))
            .Build();

        // Assert
        Assert.NotNull(definition);
        Assert.Equal("SequenceTestWorkflow", definition.Name);
        Assert.Equal(2, definition.Steps.Count); // 2 root steps (step + sequence)
        Assert.Equal(2, definition.Steps[1].SequenceSteps.Count); // 2 sequence steps
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[0].StepType);
        Assert.Equal(WorkflowStepType.Sequence, definition.Steps[1].StepType);
        Assert.Equal(typeof(CheckInventoryAsyncActivity), definition.Steps[1].SequenceSteps[0].ActivityType);
        Assert.Equal(typeof(ReserveInventoryAsyncActivity), definition.Steps[1].SequenceSteps[1].ActivityType);
    }

    [Fact]
    public async Task TestConditionalBuilder_CreatesValidDefinition()
    {
        // Arrange
        var builder = Workflow.Create<OrderWorkflowData>("ConditionalTestWorkflow");

        // Act
        var definition = builder
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .If(ctx => ctx.PreviousStep.IsValid,
                then => then.Step<ProcessPaymentAsyncActivity>(setup => setup
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)),
                @else => @else.Step<UpdateAnalyticsAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Success).From(ctx => ctx.WorkflowData.ValidationResult == false)
                    .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsStatus)))
            .Build();

        // Assert
        Assert.NotNull(definition);
        Assert.Equal("ConditionalTestWorkflow", definition.Name);
        Assert.Equal(2, definition.Steps.Count); // 1 root step + 1 conditional step
        Assert.Equal(WorkflowStepType.Activity, definition.Steps[0].StepType);
        Assert.Equal(WorkflowStepType.Conditional, definition.Steps[1].StepType);
        Assert.NotNull(definition.Steps[1].CompiledCondition);
        Assert.Single(definition.Steps[1].ThenSteps);
        Assert.Single(definition.Steps[1].ElseSteps);
        Assert.Equal(typeof(ProcessPaymentAsyncActivity), definition.Steps[1].ThenSteps[0].ActivityType);
        Assert.Equal(typeof(UpdateAnalyticsAsyncActivity), definition.Steps[1].ElseSteps[0].ActivityType);
    }

    [Fact]
    public async Task TestWorkflowExecution_ValidOrder_SuccessfulExecution()
    {
        // Arrange
        var builder = Workflow.Create<OrderWorkflowData>("ExecutionTestWorkflow");
        var definition = builder
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult)
                .Output(act => act.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors))
            .If(ctx => ctx.PreviousStep.IsValid,
                then => then.Step<ProcessPaymentAsyncActivity>(setup => setup
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)),
                @else => @else.Step<UpdateAnalyticsAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Success).From(ctx => ctx.WorkflowData.ValidationResult == false)
                    .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsStatus)))
            .Build();

        var workflowData = new OrderWorkflowData
        {
            OrderId = "TEST-001",
            Amount = 100.0m,
            PaymentMethod = "CreditCard"
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (OrderWorkflowData)result.WorkflowData;
        Assert.True(finalData.ValidationResult);
        Assert.Empty(finalData.ValidationErrors);
        Assert.NotEmpty(finalData.TransactionId);
        Assert.StartsWith("TXN-", finalData.TransactionId);
    }

    [Fact]
    public async Task TestWorkflowExecution_InvalidOrder_FailsValidation()
    {
        // Arrange
        var builder = Workflow.Create<OrderWorkflowData>("ExecutionTestWorkflow");
        var definition = builder
            .Step<ValidateOrderAsyncActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult)
                .Output(act => act.ValidationErrors).To(ctx => ctx.WorkflowData.ValidationErrors))
            .If(ctx => ctx.PreviousStep.IsValid,
                then => then.Step<ProcessPaymentAsyncActivity>(setup => setup
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Input(act => act.PaymentMethod).From(ctx => ctx.WorkflowData.PaymentMethod)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)),
                @else => @else.Step<UpdateAnalyticsAsyncActivity>(setup => setup
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(act => act.Success).From(ctx => false)
                    .Output(act => act.AnalyticsUpdated).To(ctx => ctx.WorkflowData.AnalyticsStatus)))
            .Build();

        var workflowData = new OrderWorkflowData
        {
            OrderId = "TEST-002",
            Amount = -50.0m, // Invalid amount
            PaymentMethod = "CreditCard"
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (OrderWorkflowData)result.WorkflowData;
        Assert.False(finalData.ValidationResult);
        Assert.NotEmpty(finalData.ValidationErrors);
        Assert.Contains("Amount must be positive", finalData.ValidationErrors);
        Assert.Empty(finalData.TransactionId);
        Assert.True(finalData.AnalyticsStatus);
    }
}
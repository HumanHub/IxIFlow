using IxIFlow.Builders;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

public class ConditionalTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly WorkflowEngine _workflowEngine;

    public ConditionalTests()
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
    public async Task TestBasicConditional_ThenBranch()
    {
        // Arrange
        var definition = Workflow
            .Create<ConditionalTestData>("BasicConditionalWorkflow")
            .Step<ValidateOrderActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .If(ctx => ctx.PreviousStep.IsValid,
                then => then.Step<ProcessPaymentActivity>(setup => setup
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)),
                @else => @else.Step<LogExecutionActivity>(setup => setup
                    .Input(act => act.Message).From(ctx => "Order validation failed")
                    .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                    .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog)))
            .Build();

        var workflowData = new ConditionalTestData
        {
            OrderId = "ORD-001",
            Amount = 100.0m
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ConditionalTestData)result.WorkflowData;
        Assert.True(finalData.ValidationResult);
        Assert.NotEmpty(finalData.TransactionId);
        Assert.StartsWith("TXN-", finalData.TransactionId);
        Assert.Empty(finalData.ExecutionLog); // Log should not be executed
    }

    [Fact]
    public async Task TestBasicConditional_ElseBranch()
    {
        // Arrange
        var definition = Workflow
            .Create<ConditionalTestData>("BasicConditionalWorkflow")
            .Step<ValidateOrderActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .If(ctx => ctx.PreviousStep.IsValid,
                then => then.Step<ProcessPaymentActivity>(setup => setup
                    .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                    .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)),
                @else => @else.Step<LogExecutionActivity>(setup => setup
                    .Input(act => act.Message).From(ctx => "Order validation failed")
                    .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                    .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog)))
            .Build();

        var workflowData = new ConditionalTestData
        {
            OrderId = "ORD-002",
            Amount = -50.0m // Invalid amount
        };

        // Act
        var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.WorkflowData);

        var finalData = (ConditionalTestData)result.WorkflowData;
        Assert.False(finalData.ValidationResult);
        Assert.Empty(finalData.TransactionId);
        Assert.NotEmpty(finalData.ExecutionLog);
        Assert.Contains("Order validation failed", finalData.ExecutionLog[0]);
    }

    [Fact]
    public async Task TestNestedConditionals()
    {
        // Arrange
        var definition = Workflow
            .Create<ConditionalTestData>("NestedConditionalWorkflow")
            .Step<ValidateOrderActivity>(setup => setup
                .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(act => act.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(act => act.IsValid).To(ctx => ctx.WorkflowData.ValidationResult))
            .If(ctx => ctx.PreviousStep.IsValid,
                then => then
                    .Step<LogExecutionActivity>(setup => setup
                        .Input(act => act.Message).From(ctx => "Order validation passed")
                        .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                        .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))
                    .If(ctx => ctx.WorkflowData.IsVIP,
                        thenVip => thenVip
                            .Step<CalculateDiscountActivity>(setup => setup
                                .Input(act => act.OrderAmount).From(ctx => ctx.WorkflowData.Amount)
                                .Input(act => act.CustomerType).From(ctx => ctx.WorkflowData.CustomerType)
                                .Input(act => act.IsVIP).From(ctx => ctx.WorkflowData.IsVIP)
                                .Input(act => act.IsFirstPurchase).From(ctx => ctx.WorkflowData.IsFirstPurchase)
                                .Output(act => act.DiscountAmount).To(ctx => ctx.WorkflowData.DiscountAmount)
                                .Output(act => act.FinalAmount).To(ctx => ctx.WorkflowData.FinalAmount)
                                .Output(act => act.DiscountPath).To(ctx => ctx.WorkflowData.ProcessingPath))
                            .Step<LogExecutionActivity>(setup => setup
                                .Input(act => act.Message).From(ctx =>
                                    $"VIP discount applied: {ctx.PreviousStep.DiscountAmount}")
                                .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                                .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog)),
                        elseNonVip => elseNonVip
                            .If(ctx => ctx.WorkflowData.CustomerType == "Business",
                                thenBusiness => thenBusiness
                                    .Step<CalculateDiscountActivity>(setup => setup
                                        .Input(act => act.OrderAmount).From(ctx => ctx.WorkflowData.Amount)
                                        .Input(act => act.CustomerType).From(ctx => ctx.WorkflowData.CustomerType)
                                        .Input(act => act.IsVIP).From(ctx => ctx.WorkflowData.IsVIP)
                                        .Input(act => act.IsFirstPurchase).From(ctx => ctx.WorkflowData.IsFirstPurchase)
                                        .Output(act => act.DiscountAmount).To(ctx => ctx.WorkflowData.DiscountAmount)
                                        .Output(act => act.FinalAmount).To(ctx => ctx.WorkflowData.FinalAmount)
                                        .Output(act => act.DiscountPath).To(ctx => ctx.WorkflowData.ProcessingPath))
                                    .Step<LogExecutionActivity>(setup => setup
                                        .Input(act => act.Message).From(ctx =>
                                            $"Business discount applied: {ctx.PreviousStep.DiscountAmount}")
                                        .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                                        .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog)),
                                elseRegular => elseRegular
                                    .Step<CalculateDiscountActivity>(setup => setup
                                        .Input(act => act.OrderAmount).From(ctx => ctx.WorkflowData.Amount)
                                        .Input(act => act.CustomerType).From(ctx => ctx.WorkflowData.CustomerType)
                                        .Input(act => act.IsVIP).From(ctx => ctx.WorkflowData.IsVIP)
                                        .Input(act => act.IsFirstPurchase).From(ctx => ctx.WorkflowData.IsFirstPurchase)
                                        .Output(act => act.DiscountAmount).To(ctx => ctx.WorkflowData.DiscountAmount)
                                        .Output(act => act.FinalAmount).To(ctx => ctx.WorkflowData.FinalAmount)
                                        .Output(act => act.DiscountPath).To(ctx => ctx.WorkflowData.ProcessingPath))
                                    .Step<LogExecutionActivity>(setup => setup
                                        .Input(act => act.Message).From(ctx =>
                                            $"Standard processing: {ctx.PreviousStep.DiscountPath}")
                                        .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                                        .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog))))
                    .Step<ProcessPaymentActivity>(setup => setup
                        .Input(act => act.Amount).From(ctx => ctx.WorkflowData.FinalAmount)
                        .Input(act => act.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Output(act => act.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)),
                @else => @else
                    .Step<LogExecutionActivity>(setup => setup
                        .Input(act => act.Message).From(ctx => "Order validation failed")
                        .Input(act => act.ExecutionLog).From(ctx => ctx.WorkflowData.ExecutionLog)
                        .Output(act => act.ExecutionLog).To(ctx => ctx.WorkflowData.ExecutionLog)))
            .Build();

        // Test with VIP customer
        var vipData = new ConditionalTestData
        {
            OrderId = "ORD-VIP-001",
            Amount = 500.0m,
            CustomerType = "Individual",
            IsVIP = true,
            IsFirstPurchase = false
        };

        // Act
        var vipResult = await _workflowEngine.ExecuteWorkflowAsync(definition, vipData);

        // Assert
        Assert.True(vipResult.IsSuccess);
        Assert.NotNull(vipResult.WorkflowData);

        var vipFinalData = (ConditionalTestData)vipResult.WorkflowData;
        Assert.True(vipFinalData.ValidationResult);
        Assert.Equal("VIP", vipFinalData.ProcessingPath);
        Assert.Equal(100.0m, vipFinalData.DiscountAmount); // 20% of 500
        Assert.Equal(400.0m, vipFinalData.FinalAmount);
        Assert.NotEmpty(vipFinalData.TransactionId);
        Assert.Equal(2, vipFinalData.ExecutionLog.Count);
        Assert.Contains("Order validation passed", vipFinalData.ExecutionLog[0]);
        Assert.Contains("VIP discount applied: 100", vipFinalData.ExecutionLog[1]);

        // Test with Business customer
        var businessData = new ConditionalTestData
        {
            OrderId = "ORD-BUS-001",
            Amount = 500.0m,
            CustomerType = "Business",
            IsVIP = false,
            IsFirstPurchase = false
        };

        // Act
        var businessResult = await _workflowEngine.ExecuteWorkflowAsync(definition, businessData);

        // Assert
        Assert.True(businessResult.IsSuccess);
        Assert.NotNull(businessResult.WorkflowData);

        var businessFinalData = (ConditionalTestData)businessResult.WorkflowData;
        Assert.True(businessFinalData.ValidationResult);
        Assert.Equal("Business", businessFinalData.ProcessingPath);
        Assert.Equal(75.0m, businessFinalData.DiscountAmount); // 15% of 500
        Assert.Equal(425.0m, businessFinalData.FinalAmount);
        Assert.NotEmpty(businessFinalData.TransactionId);
        Assert.Equal(2, businessFinalData.ExecutionLog.Count);
        Assert.Contains("Order validation passed", businessFinalData.ExecutionLog[0]);
        Assert.Contains("Business discount applied: 75", businessFinalData.ExecutionLog[1]);

        // Test with Regular customer with first purchase
        var regularData = new ConditionalTestData
        {
            OrderId = "ORD-REG-001",
            Amount = 500.0m,
            CustomerType = "Individual",
            IsVIP = false,
            IsFirstPurchase = true
        };

        // Act
        var regularResult = await _workflowEngine.ExecuteWorkflowAsync(definition, regularData);

        // Assert
        Assert.True(regularResult.IsSuccess);
        Assert.NotNull(regularResult.WorkflowData);

        var regularFinalData = (ConditionalTestData)regularResult.WorkflowData;
        Assert.True(regularFinalData.ValidationResult);
        Assert.Equal("FirstPurchase", regularFinalData.ProcessingPath);
        Assert.Equal(50.0m, regularFinalData.DiscountAmount); // 10% of 500
        Assert.Equal(450.0m, regularFinalData.FinalAmount);
        Assert.NotEmpty(regularFinalData.TransactionId);
        Assert.Equal(2, regularFinalData.ExecutionLog.Count);
        Assert.Contains("Order validation passed", regularFinalData.ExecutionLog[0]);
        Assert.Contains("Standard processing: FirstPurchase", regularFinalData.ExecutionLog[1]);
    }

    // Test data model for conditional testing
    public class ConditionalTestData
    {
        public string OrderId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string CustomerType { get; set; } = string.Empty;
        public int CustomerAge { get; set; }
        public bool IsVIP { get; set; }
        public bool IsFirstPurchase { get; set; }

        // Output fields
        public bool ValidationResult { get; set; }
        public string ProcessingPath { get; set; } = string.Empty;
        public decimal DiscountAmount { get; set; }
        public decimal FinalAmount { get; set; }
        public string TransactionId { get; set; } = string.Empty;
        public List<string> ExecutionLog { get; set; } = new();
    }

    // Activities for conditional testing
    public class ValidateOrderActivity : IAsyncActivity
    {
        public string OrderId { get; set; } = string.Empty;
        public decimal Amount { get; set; }

        public bool IsValid { get; set; }

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            IsValid = !string.IsNullOrEmpty(OrderId) && Amount > 0;
            await Task.CompletedTask;
        }
    }

    public class CalculateDiscountActivity : IAsyncActivity
    {
        public decimal OrderAmount { get; set; }
        public string CustomerType { get; set; } = string.Empty;
        public bool IsVIP { get; set; }
        public bool IsFirstPurchase { get; set; }

        public decimal DiscountAmount { get; set; }
        public decimal FinalAmount { get; set; }
        public string DiscountPath { get; set; } = string.Empty;

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            decimal discountRate = 0;

            if (IsVIP)
            {
                discountRate = 0.2m; // 20% for VIP
                DiscountPath = "VIP";
            }
            else if (IsFirstPurchase)
            {
                discountRate = 0.1m; // 10% for first purchase
                DiscountPath = "FirstPurchase";
            }
            else if (CustomerType == "Business")
            {
                discountRate = 0.15m; // 15% for business customers
                DiscountPath = "Business";
            }
            else if (OrderAmount > 1000)
            {
                discountRate = 0.05m; // 5% for large orders
                DiscountPath = "LargeOrder";
            }
            else
            {
                DiscountPath = "Standard";
            }

            DiscountAmount = OrderAmount * discountRate;
            FinalAmount = OrderAmount - DiscountAmount;

            await Task.CompletedTask;
        }
    }

    public class ProcessPaymentActivity : IAsyncActivity
    {
        public decimal Amount { get; set; }
        public string OrderId { get; set; } = string.Empty;

        public string TransactionId { get; set; } = string.Empty;

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            TransactionId = $"TXN-{OrderId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            await Task.CompletedTask;
        }
    }

    public class LogExecutionActivity : IAsyncActivity
    {
        public string Message { get; set; } = string.Empty;
        public List<string> ExecutionLog { get; set; } = new();

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ExecutionLog.Add($"{DateTime.UtcNow:HH:mm:ss} - {Message}");
            await Task.CompletedTask;
        }
    }
}
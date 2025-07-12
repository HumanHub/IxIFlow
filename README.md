# IxIFlow Workflow Engine - Beta release

A simple, type-safe workflow engine for .NET that helps you build and orchestrate business processes. Build workflows using a fluent API that keeps your code clean and your logic clear.

## Features

- **Fluent API**: Build workflows with an intuitive, chainable interface
- **Type Safety**: Compile-time checking for all workflow inputs and outputs
- **Parallel Execution**: Run multiple tasks simultaneously
- **Exception Handling**: Built-in try-catch support for robust error handling
- **Saga Pattern**: Distributed transactions with automatic rollback
- **Suspend/Resume**: Pause workflows and continue when events occur
- **Conditional Logic**: Dynamic branching based on your data
- **Dependency Injection**: Works great with Microsoft DI container
- **In-Process or Distributed**: Run workflows locally or across multiple instances

## Installation

```bash
dotnet add package IxIFlow
```

## Quick Start

### Simple In-Process Execution

For basic scenarios, you can create and execute workflows directly:

```csharp
using IxIFlow.Builders;
using IxIFlow.Core;

// 1. Define your data
public class OrderData
{
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
    public bool IsValid { get; set; }
    public string TransactionId { get; set; } = "";
}

// 2. Create activities
public class ValidateOrderActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
    public bool IsValid { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        IsValid = !string.IsNullOrEmpty(OrderId) && Amount > 0;
        context.Logger.LogInformation($"Order {OrderId} validation: {IsValid}");
    }
}

public class ProcessPaymentActivity : IAsyncActivity
{
    public decimal Amount { get; set; }
    public string TransactionId { get; set; } = "";

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        // Simulate payment processing
        TransactionId = $"TXN-{Guid.NewGuid()}";
        context.Logger.LogInformation($"Payment processed: {TransactionId}");
    }
}

// 3. Build and execute workflow
var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole());
services.AddSingleton<IExpressionEvaluator, ExpressionEvaluator>();
services.AddSingleton<IActivityExecutor, ActivityExecutor>();
services.AddSingleton<WorkflowEngine>();
// ... add other required services

var serviceProvider = services.BuildServiceProvider();
var workflowEngine = serviceProvider.GetRequiredService<WorkflowEngine>();

// Build the workflow
var definition = Workflow.Create<OrderData>("OrderProcessing")
    .Step<ValidateOrderActivity>(setup => setup
        .Input(step => step.OrderId).From(data => data.WorkflowData.OrderId)
        .Input(step => step.Amount).From(data => data.WorkflowData.Amount)
        .Output(step => step.IsValid).To(data => data.WorkflowData.IsValid))
    
    .If(data => data.PreviousStep.IsValid,
        then => then.Step<ProcessPaymentActivity>(setup => setup
            .Input(step => step.Amount).From(data => data.WorkflowData.Amount)
            .Output(step => step.TransactionId).To(data => data.WorkflowData.TransactionId)),
        @else => @else.Step<LogErrorActivity>(setup => setup
            .Input(step => step.Message).From(data => "Invalid order")))
    .Build();

// Execute the workflow
var orderData = new OrderData { OrderId = "ORD-001", Amount = 100.50m };
var result = await workflowEngine.ExecuteWorkflowAsync(definition, orderData);

if (result.IsSuccess)
{
    var finalData = (OrderData)result.WorkflowData;
    Console.WriteLine($"Order processed! Transaction: {finalData.TransactionId}");
}
```

### Full DI Integration

For larger applications, use the full dependency injection setup:

```csharp
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// 1. Define workflow as a class
public class CustomerOrderWorkflow : IWorkflow<CustomerOrderData>
{
    public string Id => "CustomerOrderWorkflow";
    public int Version => 1;

    public void Build(IWorkflowBuilder<CustomerOrderData> builder)
    {
        builder
            .Step<ValidateCustomerActivity>(setup => setup
                .Input(step => step.CustomerId).From(data => data.WorkflowData.CustomerId)
                .Output(step => step.IsValid).To(data => data.WorkflowData.IsValidCustomer))
            
            .Step<ProcessOrderActivity>(setup => setup
                .Input(step => step.OrderAmount).From(data => data.WorkflowData.OrderAmount)
                .Input(step => step.IsValidCustomer).From(data => data.PreviousStep.IsValid)
                .Output(step => step.OrderId).To(data => data.WorkflowData.ProcessedOrderId));
    }
}

// 2. Register services
var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        // Register IxIFlow
        services.AddIxIFlow();
        
        // Register your workflow
        services.RegisterWorkflow<CustomerOrderWorkflow>();
        
        // Register activities
        services.AddTransient<ValidateCustomerActivity>();
        services.AddTransient<ProcessOrderActivity>();
    })
    .Build();

// 3. Execute
var workflowEngine = host.Services.GetRequiredService<IWorkflowEngine>();
var workflowDefinition = host.Services.GetWorkflowDefinition<CustomerOrderWorkflow>();

var workflowData = new CustomerOrderData
{
    CustomerId = "CUST-123",
    OrderAmount = 249.99m
};

var result = await workflowEngine.ExecuteWorkflowAsync(workflowDefinition, workflowData);
```

## Advanced Features

### Parallel Execution

Run multiple tasks at the same time:

```csharp
.Parallel(parallel =>
{
    parallel
        .Do(emailBranch =>
        {
            emailBranch.Step<SendEmailActivity>(setup => setup
                .Input(step => step.EmailAddress).From(data => data.WorkflowData.CustomerEmail));
        })
        .Do(smsBranch =>
        {
            smsBranch.Step<SendSmsActivity>(setup => setup
                .Input(step => step.PhoneNumber).From(data => data.WorkflowData.CustomerPhone));
        });
})
```

### Exception Handling

Handle errors gracefully:

```csharp
.Try(tryBlock =>
{
    tryBlock.Step<ChargeCardActivity>(setup => setup
        .Input(step => step.Amount).From(data => data.WorkflowData.OrderTotal));
})
.Catch<PaymentDeclinedException>(catchBlock =>
{
    catchBlock.Step<NotifyPaymentFailureActivity>(setup => setup
        .Input(step => step.Reason).From(data => data.Exception.Message));
})
.Catch<Exception>(catchBlock =>
{
    catchBlock.Step<LogUnknownErrorActivity>(setup => setup
        .Input(step => step.Error).From(data => data.Exception.ToString()));
})
```

### Saga Transactions

Handle complex distributed operations with automatic compensation:

```csharp
.Saga(saga =>
{
    saga.Step<ReserveProductActivity>(setup => setup
            .Input(step => step.ProductId).From(data => data.WorkflowData.ProductId)
            .Output(step => step.ReservationId).To(data => data.WorkflowData.ReservationId)
            .CompensateWith<ReleaseProductActivity>())
        
        .Step<ChargeCustomerActivity>(setup => setup
            .Input(step => step.Amount).From(data => data.WorkflowData.TotalAmount)
            .Output(step => step.PaymentId).To(data => data.WorkflowData.PaymentId)
            .CompensateWith<RefundCustomerActivity>());
})
.OnError<Exception>(error =>
{
    error.Compensate(); // Rolls back all successful steps
})
```

### Suspend and Resume

Pause workflows and continue when something happens:

```csharp
.Suspend<ManagerApprovalEvent>("Waiting for manager approval",
    (approval, context) => approval.OrderId == context.WorkflowData.OrderId && approval.Approved)

.Step<FinalizeOrderActivity>(setup => setup
    .Input(step => step.OrderId).From(data => data.WorkflowData.OrderId)
    .Input(step => step.IsApproved).From(data => data.PreviousStep.Approved))
```

## Configuration Options

### Basic Setup

```csharp
services.AddIxIFlow(options =>
{
    options.EnableDetailedLogging = true;
    options.MaxConcurrentExecutions = 100;
});
```

### Distributed Workflows

For running workflows across multiple servers:

```csharp
services.AddIxIFlowHost(options =>
{
    options.HostName = "WorkflowNode-01";
    options.EnableDistribution = true;
}, connectionString: "Server=localhost;Database=WorkflowEngine;");
```

### Multiple Workflows

Register several workflows at once:

```csharp
services.RegisterWorkflows(builder =>
{
    builder
        .RegisterWorkflow<OrderProcessingWorkflow>()
        .RegisterWorkflow<CustomerOnboardingWorkflow>()
        .RegisterWorkflow<InventoryManagementWorkflow>();
});
```

## Tips

- **Keep it simple**: Start with basic sequential steps, add complexity as needed
- **One job per activity**: Each activity should do one thing well
- **Test your activities**: Write unit tests for individual activities first
- **Handle errors**: Always think about what could go wrong and plan for it
- **Use meaningful names**: Future you will thank you for clear naming

## Contributing

Want to help make IxIFlow better? Check out our [Contributing Guide](CONTRIBUTING.md) to get started.

## License

MIT License - see [LICENSE](LICENSE) file for details.

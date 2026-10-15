# IxIFlow Workflow Engine

A type-safe, durable workflow engine for .NET. Define activities and control flow with a fluent C# API, then execute the definition through `IWorkflowClient`. Workflows can suspend, survive restarts with persisted state, and resume where they stopped.

## Features

- **Fluent API**: Build workflows with a chainable, strongly typed builder
- **Type Safety**: Activity inputs, outputs, and `PreviousStep` values are checked at compile time
- **Control Flow**: Sequences, conditions, `WhileDo`/`DoWhile` loops, and child workflow invocation
- **Parallel Execution**: Run branches together and join on all, any, or a condition
- **Exception Handling**: `Try`/`Catch`/`Finally` with typed fault data saved for handlers
- **Saga Pattern**: Compensation activities run in reverse order when later work fails
- **Suspend/Resume**: Wait for correlated events or delays, then continue at the saved position
- **Durable Execution**: Versioned checkpoints, recovery of interrupted instances, and explicit resolution of uncertain activity outcomes
- **Cancellation**: Cancel a running or suspended workflow and unwind through compensation and `Finally`
- **Dependency Injection**: Register engines, workflows, and activities with Microsoft DI
- **SQL Server Hosting**: Optional shared instance state, execution leases, host registry, and acknowledged message bus

See the [Roadmap](#roadmap) for work that is planned but not yet available.

## Installation

```bash
dotnet add package IxIFlow
```

Local execution uses the `IxIFlow` package. The solution also contains:

| Package | Purpose |
| --- | --- |
| `IxIFlow.Abstractions` | Activity contract and metadata attributes, for activity packages |
| `IxIFlow.Distributed` | Host, coordinator, and queue service contracts |
| `IxIFlow.SqlServer` | SQL Server state repository, host registry, and message bus |
| `IxIFlow.Authoring` | YAML workflow documents compiled to definitions (preview), and the activity package catalog |

## Quick Start

### 1. Define data and activities

An activity implements `IAsyncActivity`. Public properties receive mapped inputs and expose outputs.

```csharp
using IxIFlow.Core;

public sealed class OrderData
{
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
    public bool IsValid { get; set; }
    public string TransactionId { get; set; } = "";
}

public sealed class ValidateOrderActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
    public bool IsValid { get; private set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        IsValid = !string.IsNullOrWhiteSpace(OrderId) && Amount > 0;
        return Task.CompletedTask;
    }
}

public sealed class ProcessPaymentActivity : IAsyncActivity
{
    public decimal Amount { get; set; }
    public string TransactionId { get; private set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        TransactionId = $"TXN-{Guid.NewGuid():N}";
        return Task.CompletedTask;
    }
}

public sealed class RejectOrderActivity : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
```

### 2. Build and execute

```csharp
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLogging();
services.AddIxIFlow();
services.AddTransient<ValidateOrderActivity>();
services.AddTransient<ProcessPaymentActivity>();
services.AddTransient<RejectOrderActivity>();

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var client = scope.ServiceProvider.GetRequiredService<IWorkflowClient>();

var definition = Workflow.Create<OrderData>("OrderProcessing", version: 1)
    .Step<ValidateOrderActivity>(step => step
        .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
        .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
        .Output(activity => activity.IsValid).To(ctx => ctx.WorkflowData.IsValid))
    .If(ctx => ctx.PreviousStep.IsValid,
        then => then.Step<ProcessPaymentActivity>(step => step
            .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
            .Output(activity => activity.TransactionId).To(ctx => ctx.WorkflowData.TransactionId)),
        @else => @else.Step<RejectOrderActivity>())
    .Build();

var result = await client.RunAsync(
    definition, new OrderData { OrderId = "ORD-001", Amount = 100.50m });

if (result.IsSuccess)
    Console.WriteLine($"Transaction: {((OrderData)result.WorkflowData!).TransactionId}");
else
    Console.WriteLine($"{result.Status}: {result.ErrorMessage}");
```

`WorkflowExecutionResult` includes `Status`, `InstanceId`, `WorkflowData`, `ErrorMessage`, and `TraceEntries`.

### 3. Register a workflow class

`IWorkflow<TData>` packages a named, versioned definition for dependency injection.

```csharp
using IxIFlow.Builders.Interfaces;

public sealed class OrderWorkflow : IWorkflow<OrderData>
{
    public int Version => 1;

    public void Build(IWorkflowBuilder<OrderData> builder)
    {
        builder.Step<ValidateOrderActivity>(step => step
            .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
            .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
            .Output(activity => activity.IsValid).To(ctx => ctx.WorkflowData.IsValid));
    }
}
```

```csharp
services.RegisterWorkflow<OrderWorkflow>();

// Or several at once
services.RegisterWorkflows(registry => registry
    .RegisterWorkflow<OrderWorkflow>()
    .RegisterWorkflow<RefundWorkflow>());

var registered = provider.GetWorkflowDefinition<OrderWorkflow>();
```

`RegisterWorkflow<TWorkflow>(name)` can supply a name; otherwise the class name is used.

## Advanced Features

### Parallel Execution

Branches share workflow data, so write to distinct properties.

```csharp
.Parallel(parallel => parallel
    .Do(email => email.Step<SendEmailActivity>(step => step
        .Input(activity => activity.Address).From(ctx => ctx.WorkflowData.Email)
        .Output(activity => activity.Sent).To(ctx => ctx.WorkflowData.EmailSent)))
    .Do(sms => sms.Step<SendSmsActivity>(step => step
        .Input(activity => activity.Number).From(ctx => ctx.WorkflowData.Phone)
        .Output(activity => activity.Sent).To(ctx => ctx.WorkflowData.SmsSent))))
```

By default the join waits for all branches. `WaitAny()` continues after the first branch and cancels the rest; `WaitConditionally(predicate)` continues when the predicate becomes true.

### Exception Handling

`Catch<TException, TFault>` saves selected exception properties in a fault record that handlers read through `ctx.Fault`.

```csharp
public sealed record PaymentFault(string Message);
```

```csharp
.Try(tryBlock => tryBlock.Step<ChargeCardActivity>())
.Catch<PaymentException, PaymentFault>(catchBlock => catchBlock
    .Step<LogPaymentFailureActivity>(step => step
        .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)))
.Finally(finallyBlock => finallyBlock.Step<AuditOrderActivity>())
```

### Saga Transactions

Each saga step can name a compensation activity. When a later step fails, completed steps are compensated in reverse order.

```csharp
.Saga(saga => saga
    .Step<ReserveProductActivity>(step => step
        .Input(activity => activity.ProductId).From(ctx => ctx.WorkflowData.ProductId)
        .CompensateWith<ReleaseProductActivity>())
    .Step<ChargeCustomerActivity>(step => step
        .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.TotalAmount)
        .CompensateWith<RefundCustomerActivity>()))
.OnError<Exception>(error => error.Compensate().ThenTerminate())
```

Compensation is an application action, not an automatic rollback of another system. See [sagas](docs/sagas.md).

### Suspend and Resume

A wait is identified by a correlation key. On resume, the event becomes `PreviousStep`.

```csharp
var definition = Workflow.Create<OrderData>("Approval")
    .Step<PrepareOrderActivity>()
    .WaitFor<ApprovalEvent>("manager-approval",
        (approval, ctx) => approval.OrderId == ctx.WorkflowData.OrderId)
    .Step<FinalizeOrderActivity>(step => step
        .Input(activity => activity.Approved).From(ctx => ctx.PreviousStep.Approved))
    .Build();

var started = await client.RunAsync(definition, orderData);
// started.Status is Suspended
var resumed = await client.ResumeAsync(started.InstanceId, "manager-approval", approvalEvent);
```

`Delay(TimeSpan)` suspends until a deadline, and `Invoke<TWorkflow, TChildData>` runs a child workflow with its own saved state.

## Configuration

### In-process

`AddIxIFlow()` registers in-memory state and event storage shared across scopes in one process. Custom implementations can replace them:

```csharp
services.AddIxIFlow(options =>
{
    options.UseCustomStateRepository = typeof(MyStateRepository);
    options.UseCustomTracer = typeof(MyTracer);
});
```

### SQL Server hosting

`IxIFlow.SqlServer` stores instance state in SQL Server with revisioned checkpoints and renewable execution leases, and adds a SQL host registry and message bus.

```csharp
services.AddIxIFlow();
services.AddHttpClient();
services.AddIxIFlowHost(options =>
{
    options.HostId = "workflow-node-01";
    options.MaxConcurrentWorkflows = 50;
    options.Tags = ["payments"];
}, connectionString: "Server=localhost;Database=IxIFlow;Trusted_Connection=True;");
```

Every host that may execute or resume a workflow must register the same workflow definitions and activity code. See [coordinator and hosts](docs/coordinator-and-hosts.md).

## Roadmap

These items are planned and not yet available. The [roadmap](docs/roadmap.md) describes the target design and remaining work for each area.

- **Debugging and observability**: step-level trace store, breakpoints and step-through, structured logging with instance correlation, and OpenTelemetry traces and metrics
- **Coordinator dispatch**: routing queued execute, resume, and cancel commands to a selected host, an HTTP host endpoint, host affinity on resume, and live load accounting
- **Management API**: instance inspection, dead-letter inspection and requeue, and start triggers
- **Workflow documents**: JSON schema and full YAML parity with the fluent API (parallel, loops, `Try`, sagas, delays), reusable step templates, and publish and version flow
- **Storage adapters**: PostgreSQL, SQLite, and message broker adapters; persisted definition and event registries
- **Visual editor**: graph editor and debugger UI on top of the document model

## Tips

- **Start simple**: Begin with sequential steps and add control flow as needed
- **One job per activity**: Keep each activity focused and unit-testable
- **Make side effects idempotent**: Activities and compensations can be retried after recovery
- **Name and version workflows**: Explicit names and versions keep saved instances resumable
- **Register everywhere**: Any host that resumes a workflow needs its definition and activities

## Documentation

- [Getting started](docs/getting-started.md)
- [Fluent API](docs/fluent-api.md)
- [Execution model](docs/execution-model.md)
- [Sagas and compensation](docs/sagas.md)
- [Engine capabilities](docs/engine-capabilities.md)
- [Coordinator and hosts](docs/coordinator-and-hosts.md)
- [Roadmap](docs/roadmap.md)

## Contributing

See the [Contributing Guide](CONTRIBUTING.md).

## License

MIT. See [LICENSE](LICENSE).

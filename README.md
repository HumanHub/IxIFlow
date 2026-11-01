# IxIFlow

A durable workflow engine for .NET 10. You write workflows in C# or YAML. IxIFlow saves each instance after every step, so it survives restarts and can wait days for a person or an event before it continues.

[Documentation](https://github.com/HumanHub/IxIFlow/blob/main/docs/index.md) · [Samples](https://github.com/HumanHub/IxIFlow/blob/main/samples/README.md) · [Roadmap](https://github.com/HumanHub/IxIFlow/blob/main/docs/roadmap.md)

## What you get

- **C# or YAML.** A typed fluent API, or YAML files that compile into the same definitions at build time.
- **Durable instances.** Checkpoints in memory, SQLite, SQL Server or PostgreSQL. After a crash or a deploy, an instance resumes from its last saved step.
- **Waits.** Delays, events, and request/reply with deadlines and timeout paths.
- **Failure handling.** `Try`/`Catch`/`Finally`, retries, and sagas that undo completed steps in reverse order.
- **Control flow.** Conditions, switches, loops, `ForEach`, parallel branches, child workflows and state machines.
- **Publishing.** Publish YAML workflows and activity packages to running hosts without redeploying them.
- **Operations.** Search, inspect, replay and cancel instances, debug them with breakpoints, and trace them with OpenTelemetry.

## Install

```sh
dotnet add package IxIFlow
dotnet add package IxIFlow.Sqlite
```

Use `IxIFlow.SqlServer` or `IxIFlow.PostgreSql` instead of `IxIFlow.Sqlite` for a shared database.

## Quick start

An activity is a class with typed inputs and outputs. This one adds a fee to an amount:

<!-- snippet: samples/IxIFlow.Samples/Quote.cs -->
```csharp
[WorkflowActivity("samples.add-fee", Version = "1.0")]
public sealed class AddFee : IAsyncActivity
{
    [ActivityInput, Required, Display(Name = "Customer", Description = "The customer receiving the quote"), UIHint("text")]
    public string Customer { get; set; } = "";

    [ActivityInput, DefaultValue(typeof(decimal), "2.50")]
    public decimal Fee { get; set; }

    [ActivityInputOutput, Display(Name = "Amount", Description = "The quote before and after the fee")]
    public decimal Amount { get; set; }

    [ActivityOutput, Display(Name = "Receipt")]
    public string Receipt { get; private set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Amount += Fee;
        Receipt = "Quote for " + Customer;

        return Task.CompletedTask;
    }
}
```

A workflow maps its data into each step and the step's results back:

<!-- snippet: tests/IxIFlow.Samples.Tests/ReadmeExamples.cs -->
```csharp
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Samples;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLogging();
services.AddIxIFlow();
await using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IWorkflowClient>();

var definition = Workflow.Create<QuoteData>("QuickStart")
    .Step<AddFee>(step => step
        .Input(activity => activity.Customer).From(ctx => ctx.WorkflowData.Customer)
        .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
        .Output(activity => activity.Amount).To(ctx => ctx.WorkflowData.Amount))
    .Build();

WorkflowDefinitionValidator.Validate(definition);

var result = await client.RunAsync(definition, new QuoteData());

if (result.Status != WorkflowExecutionStatus.Success)
    throw new InvalidOperationException(result.ErrorMessage);

Console.WriteLine(((QuoteData)result.WorkflowData!).Amount); // 12.50
```

## Wait for a person

This workflow notifies an approver and waits up to two days for a decision. If nobody answers, it takes the timeout path:

<!-- snippet: samples/IxIFlow.Samples/OrderApproval.cs#order-approval -->
```csharp
public static WorkflowDefinition Definition() =>
    Workflow.Create<OrderData>("OrderApproval")
        .Step<NotifyApprover>(step => step
            .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
            .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount))
        .WaitFor<OrderDecision>("approval", configure: wait => wait
            .Output(decision => decision.Approved).To(ctx => ctx.WorkflowData.Approved)
            .TimeoutAfter(TimeSpan.FromDays(2))
            .OnTimeout(timeout => timeout.Step<Label>(step => step
                .Input(activity => activity.Value).From(ctx => "escalated")
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))))
        .If(ctx => ctx.WorkflowData.Approved,
            then => then.Step<Label>(step => step
                .Input(activity => activity.Value).From(ctx => "approved")
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))
        .Build();
```

`RunAsync` returns as soon as the instance is waiting. The decision can arrive later, from an API call, a UI or another process, even after a restart:

<!-- snippet: tests/IxIFlow.Samples.Tests/ReadmeExamples.cs -->
```csharp
var started = await client.RunAsync(OrderApproval.Definition(), new OrderData());
// started.Status is Suspended: the instance is saved and waits for a decision.

var wait = (await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId))).Single();
var finished = await client.ReplyAsync(wait.Token, new OrderDecision { Approved = true, By = "manager" });
```

Whoever has `wait.Token` can answer the wait. Send it only to the approver, and don't log it. See [reply tokens](https://github.com/HumanHub/IxIFlow/blob/main/docs/reply-tokens.md).

## Write it in YAML

YAML files named `*.workflow.yaml` compile with your project into the same definitions. This saga takes a payment and reserves stock. If the stock is missing, it refunds the payment and tries once more:

<!-- snippet: samples/IxIFlow.Samples/Definitions/saga-payment.workflow.yaml -->
```yaml
# Refund the first payment if stock is unavailable, then retry with a fresh reply token.
workflow:
  name: PaymentInASaga
  dataType: IxIFlow.Recipes.Payment
definitions:
  - kind: activity
    activity: IxIFlow.Recipes.ValidateOrder
  - kind: saga
    steps:
      - kind: request
        activity: IxIFlow.Recipes.CreatePayment
        key: payment
        input:
          - target: Amount
            from: { path: workflow.Amount }
        output:
          - source: Reference
            to: { path: workflow.PaymentReference }
        compensation:
          - activity: IxIFlow.Recipes.RefundPayment
            input:
              - target: Reference
                from: { path: currentStep.Reference }
      - kind: activity
        activity: IxIFlow.Recipes.ReserveStock
        input:
          - target: OrderId
            from: { path: workflow.OrderId }
      - kind: waitFor
        key: payment
        event: IxIFlow.Recipes.PaymentReceived
        resumeCondition: { path: 'event.Status != "pending"' }
        output:
          - source: Status
            to: { path: workflow.PaymentStatus }
    onError:
      - exception: IxIFlow.Recipes.StockUnavailable
        compensate: all
        then: retry
        retry:
          maximumAttempts: 1
```

The build turns this file into `Workflow_saga_payment_workflow.Build()`. An unknown or misspelled key fails the build at that line, and the bundled JSON Schema gives editors completion. See the [YAML guide](https://github.com/HumanHub/IxIFlow/blob/main/docs/yaml-authoring.md).

## Run it in a host

The generic host recovers unfinished instances after a restart, delivers completion events, cleans up old results and drains running work on shutdown:

<!-- snippet: samples/IxIFlow.Samples/DocumentationExamples.cs#hosted-run -->
```csharp
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Samples;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddIxIFlow(options => options.Slots = 8);
using var host = builder.Build();
await host.StartAsync();
WorkflowExecutionResult result;

try
{
    var client = host.Services.GetRequiredService<IWorkflowClient>();
    result = await client.RunAsync(Quote.Definition(), new QuoteData());
    Console.WriteLine(result.Status);
}
finally
{
    await host.StopAsync();
}
```

Pick a store on the builder that `AddIxIFlow()` returns:

| Store | Registration | Use it for |
| --- | --- | --- |
| Memory | `services.AddIxIFlow()` | Tests and demos; instances are lost on restart |
| SQLite | `.UseSqlite("Data Source=workflows.db")` | One host with a local file |
| SQL Server | `.UseSqlServer(connectionString)` | Several hosts sharing one database |
| PostgreSQL | `.UsePostgreSql(connectionString)` | Several hosts sharing one database, with `LISTEN`/`NOTIFY` wake-ups |

See [stores](https://github.com/HumanHub/IxIFlow/blob/main/docs/stores.md) and [hosting](https://github.com/HumanHub/IxIFlow/blob/main/docs/coordinator-and-hosts.md).

## Packages

| Package | What it adds |
| --- | --- |
| `IxIFlow` | Engine, fluent API, client, host and memory store; includes the YAML generator and analyzers |
| `IxIFlow.Abstractions` | Activity interfaces and attributes, for activity libraries |
| `IxIFlow.Sqlite`, `IxIFlow.SqlServer`, `IxIFlow.PostgreSql` | Durable stores |
| `IxIFlow.Authoring` | YAML documents, validation and publishing |
| `IxIFlow.AspNetCore` | HTTP triggers, management and publishing endpoints |
| `IxIFlow.Webhooks` | Signed outgoing webhooks for completion events |
| `IxIFlow.Coordinator` | Fast wake-ups and placement across several hosts |
| `IxIFlow.Templates`, `IxIFlow.Templates.Liquid` | Versioned message templates rendered with Liquid |
| `IxIFlow.Debugging` | Breakpoints and a Debug Adapter Protocol endpoint |

## Learn more

- [Getting started](https://github.com/HumanHub/IxIFlow/blob/main/docs/getting-started.md): a first workflow, step by step
- [Fluent API](https://github.com/HumanHub/IxIFlow/blob/main/docs/fluent-api.md) and [YAML](https://github.com/HumanHub/IxIFlow/blob/main/docs/yaml-authoring.md): every construct
- [Execution model](https://github.com/HumanHub/IxIFlow/blob/main/docs/execution-model.md): checkpoints, recovery and waits
- [Sagas](https://github.com/HumanHub/IxIFlow/blob/main/docs/sagas.md): compensation and retries
- [Samples](https://github.com/HumanHub/IxIFlow/blob/main/samples/README.md): runnable examples for each feature
- [All guides](https://github.com/HumanHub/IxIFlow/blob/main/docs/index.md)

## Status and roadmap

IxIFlow 1.0 is the engine, and it runs workflows inside your process. Next is a visual designer for workflows and activities; see the [roadmap](https://github.com/HumanHub/IxIFlow/blob/main/docs/roadmap.md).

## License

[Apache License 2.0](https://github.com/HumanHub/IxIFlow/blob/main/LICENSE). See [NOTICE](https://github.com/HumanHub/IxIFlow/blob/main/NOTICE) and [CONTRIBUTING.md](https://github.com/HumanHub/IxIFlow/blob/main/CONTRIBUTING.md).

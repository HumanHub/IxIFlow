# IxIFlow Workflow Engine

IxIFlow is a .NET workflow engine with a fluent C# API. Define typed activities and control flow in code, then execute the definition through `IWorkflowEngine`.

## Features

- Typed activity input and output mappings
- Sequences, conditions, loops, and parallel branches
- `Try`/`Catch`/`Finally` and saga compensation
- Event waits, delays, suspension, and resume
- Child workflow invocation by type or by name and version
- Microsoft dependency injection integration
- In-memory execution by default, with optional SQL Server state and host services

## Install

```bash
dotnet add package IxIFlow
```

Local execution uses the `IxIFlow` package. The solution also includes `IxIFlow.Authoring`, `IxIFlow.Distributed`, and `IxIFlow.Distributed.SqlServer` for document authoring and optional hosting.

## Define and run a workflow

An activity implements `IAsyncActivity`. Public properties receive mapped inputs and expose outputs. `Workflow.Create<TData>` creates a builder; `Build()` returns a `WorkflowDefinition`.

```csharp
using IxIFlow.Core;

public sealed class OrderData
{
    public string OrderId { get; set; } = "";
    public bool IsValid { get; set; }
}

public sealed class ValidateOrderActivity : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public bool IsValid { get; private set; }

    public Task ExecuteAsync(
        IActivityContext context,
        CancellationToken cancellationToken = default)
    {
        IsValid = !string.IsNullOrWhiteSpace(OrderId);
        return Task.CompletedTask;
    }
}
```

```csharp
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddLogging();
services.AddIxIFlow();
services.AddTransient<ValidateOrderActivity>();

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

var definition = Workflow.Create<OrderData>("ValidateOrder", version: 1)
    .Step<ValidateOrderActivity>(step => step
        .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
        .Output(activity => activity.IsValid).To(ctx => ctx.WorkflowData.IsValid))
    .Build();

var result = await engine.ExecuteWorkflowAsync(
    definition, new OrderData { OrderId = "ORD-001" });

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Valid: {((OrderData)result.WorkflowData!).IsValid}");
```

`WorkflowExecutionResult` includes `Status`, `InstanceId`, `WorkflowData`, `ErrorMessage`, and trace entries. `IsSuccess` is true when `Status` is `Success`.

## Compose steps

After the first activity, `PreviousStep` has that activity's type. The builder can use it in a mapping or condition:

```csharp
var definition = Workflow.Create<OrderData>("RouteOrder")
    .Step<ValidateOrderActivity>(step => step
        .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
        .Output(activity => activity.IsValid).To(ctx => ctx.WorkflowData.IsValid))
    .If(ctx => ctx.PreviousStep.IsValid,
        then => then.Step<AcceptOrderActivity>(),
        @else => @else.Step<RejectOrderActivity>())
    .Build();
```

`AcceptOrderActivity` and `RejectOrderActivity` are application activities implementing `IAsyncActivity`. A workflow can also use `Sequence`, `Parallel`, `WhileDo`, `DoWhile`, `Try`/`Catch`/`Finally`, `Saga`, `WaitFor`, `Suspend`, `Delay`, and `Invoke` after an activity. See the [fluent API guide](docs/fluent-api.md) for their builder forms.

## Register a workflow class

`IWorkflow<TData>` packages a named, versioned definition for dependency injection. Its `Build` method adds steps to the supplied builder.

```csharp
using IxIFlow.Builders.Interfaces;

public sealed class OrderWorkflow : IWorkflow<OrderData>
{
    public int Version => 1;

    public void Build(IWorkflowBuilder<OrderData> builder)
    {
        builder.Step<ValidateOrderActivity>(step => step
            .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
            .Output(activity => activity.IsValid).To(ctx => ctx.WorkflowData.IsValid));
    }
}
```

```csharp
var workflowServices = new ServiceCollection();
workflowServices.AddLogging();
workflowServices.AddIxIFlow();
workflowServices.AddTransient<ValidateOrderActivity>();
workflowServices.RegisterWorkflow<OrderWorkflow>();

using var workflowProvider = workflowServices.BuildServiceProvider();
var registeredDefinition = workflowProvider.GetWorkflowDefinition<OrderWorkflow>();
```

`RegisterWorkflow<TWorkflow>(name)` can supply a name; otherwise the workflow class name is used.

## State and hosting

`AddIxIFlow()` uses in-memory workflow state and event storage. State is shared across scopes in one process. For persisted instance state and distributed host services, `IxIFlow.Distributed.SqlServer` provides `AddIxIFlowHost(options, connectionString)`. Each resuming host must register the matching workflow definition and activity code.

See [getting started](docs/getting-started.md), [execution model](docs/execution-model.md), and [coordinator and hosts](docs/coordinator-and-hosts.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT. See [LICENSE](LICENSE).

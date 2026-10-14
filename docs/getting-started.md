---
title: Getting started
description: Define and run a small IxIFlow workflow in a .NET application.
---

This guide uses the fluent C# API and executes a workflow in one application process.

## Install

```bash
dotnet add package IxIFlow
```

## Define data and an activity

An activity implements `IAsyncActivity`. Public properties can receive inputs and expose outputs through the builder.

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
    public bool IsValid { get; set; }

    public Task ExecuteAsync(
        IActivityContext context,
        CancellationToken cancellationToken = default)
    {
        IsValid = !string.IsNullOrWhiteSpace(OrderId);
        return Task.CompletedTask;
    }
}
```

## Build and run the workflow

Register the engine and activity in Microsoft dependency injection. Resolve the scoped engine from a scope.

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

var definition = Workflow.Create<OrderData>("ValidateOrder")
    .Step<ValidateOrderActivity>(step => step
        .Input(x => x.OrderId).From(ctx => ctx.WorkflowData.OrderId)
        .Output(x => x.IsValid).To(ctx => ctx.WorkflowData.IsValid))
    .Build();

var data = new OrderData { OrderId = "ORD-001" };
var result = await engine.ExecuteWorkflowAsync(definition, data);

Console.WriteLine($"Status: {result.Status}");
Console.WriteLine($"Valid: {((OrderData)result.WorkflowData!).IsValid}");
```

`WorkflowExecutionResult` contains the status, instance ID, workflow data, error information, and trace entries. `IsSuccess` is true only when the status is `Success`.

## Next

Read the [execution model](/docs/execution-model/) before adding suspension or saga compensation. The built-in state repository can retain an instance across scopes in the same process, but not across a process restart or another host.

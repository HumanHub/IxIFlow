---
title: Custom activity SDK
description: Define activity metadata and register installed activity assemblies.
---

An IxIFlow activity implements `IAsyncActivity`. The fluent C# builder can use any such class through `Step<TActivity>`; metadata attributes are optional for that path. `IxIFlow.ActivitySdk` uses attributes to derive an activity catalog from an installed assembly for document authoring.

## Define an activity

`WorkflowActivityAttribute` supplies a stable key and optional version, name, category, icon name, and designer ID. Mark input properties with `WorkflowInputAttribute` and output properties with `WorkflowOutputAttribute`.

```csharp
using IxIFlow.ActivitySdk;
using IxIFlow.Core;

[WorkflowActivity("orders.validate", Version = "1.0", Name = "Validate order",
    Category = "Orders", Icon = "check-circle")]
public sealed class ValidateOrderActivity : IAsyncActivity
{
    [WorkflowInput(Label = "Order ID", Required = true)]
    public string OrderId { get; set; } = "";

    [WorkflowOutput]
    public bool IsValid { get; private set; }

    public Task ExecuteAsync(IActivityContext context,
        CancellationToken cancellationToken = default)
    {
        IsValid = !string.IsNullOrWhiteSpace(OrderId);
        return Task.CompletedTask;
    }
}
```

`WorkflowInput` also accepts `Control`, `Help`, and a string `Default`. The supported catalog controls are `text`, `code`, `binding`, and `connection`. The metadata identifies fields and display choices; the activity's `ExecuteAsync` method defines its runtime behavior.

## Register an installed assembly

`ActivityPackageRegistry.AddAssembly` scans exported attributed activity types and derives a versioned package manifest. `RegisterActivities` adds the resolved activity types to Microsoft dependency injection.

```csharp
var registry = new ActivityPackageRegistry();
registry.AddAssembly(typeof(ValidateOrderActivity).Assembly,
    packageName: "Orders.Activities", packageVersion: "1.0.0");
registry.RegisterActivities(services);

var manifest = registry.Packages.Single();
var activity = await registry.FindAsync("orders.validate");
```

The registry validates package metadata, duplicate activity keys, and the link from a catalog entry to an `IAsyncActivity` implementation. It also accepts a YAML package manifest through `AddPackage(manifestYaml, assembly)` or `AddEmbeddedPackage(assembly, resourceName)`. The assembly and its dependencies are installed by the application; the registry resolves types already present in that assembly.

## Use an activity from a workflow

The fluent API maps activity properties directly:

```csharp
var definition = Workflow.Create<OrderData>("ValidateOrder")
    .Step<ValidateOrderActivity>(step => step
        .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
        .Output(activity => activity.IsValid).To(ctx => ctx.WorkflowData.IsValid))
    .Build();
```

The `IxIFlow.DemoActivities` project contains file and database activity examples. `QueryCustomerActivity` takes a named connection reference and uses a host-provided `IDemoConnectionFactory`; credentials remain outside the workflow definition. See [getting started](/docs/getting-started/) for engine registration and execution.

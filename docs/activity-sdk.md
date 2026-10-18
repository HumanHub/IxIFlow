---
title: Custom activity SDK
description: Define activity metadata and register installed activity assemblies.
---

An IxIFlow activity implements `IAsyncActivity`. The fluent C# builder can use any such class through `Step<TActivity>`; metadata attributes are optional for that path. The attributes are defined in `IxIFlow.Abstractions`, so an activity package depends only on that small contract. `ActivityPackageRegistry` in `IxIFlow.Authoring` reads them to derive an activity catalog from an installed assembly for document authoring.

## Define an activity

`WorkflowActivityAttribute` supplies a stable key and optional version, name, category, icon name, and designer ID. The property markers `ActivityInput`, `ActivityOutput`, and `ActivityInputOutput` declare inputs, outputs, and properties mapped both ways. They have no properties of their own.

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using IxIFlow.ActivitySdk;
using IxIFlow.Core;

[WorkflowActivity("orders.validate", Version = "1.0", Name = "Validate order",
    Category = "Orders", Icon = "check-circle")]
public sealed class ValidateOrderActivity : IAsyncActivity
{
    [ActivityInput, Required, Display(Name = "Order ID", Description = "The order to validate")]
    public string OrderId { get; set; } = "";

    [ActivityOutput]
    public bool IsValid { get; private set; }

    public Task ExecuteAsync(IActivityContext context,
        CancellationToken cancellationToken = default)
    {
        IsValid = !string.IsNullOrWhiteSpace(OrderId);
        return Task.CompletedTask;
    }
}
```

A marker declares direction: an input needs a public setter, an output a public getter, and an in-out property both. Invalid accessors fail workflow building and produce analyzer errors at the property. Once an activity uses any marker, only marked properties are exposed. An activity without markers keeps the convention: every public setter is an input and every public getter is an output.

Use .NET attributes for metadata and validation:

- `[Required]` requires an input mapping, a `[DefaultValue]`, or a step-level YAML `defaults` entry at build time. Before `ExecuteAsync`, a null or empty string fails the attempt with an `InvalidInput` message and the existing `WorkflowErrorKind.InvalidRequest`. Output-only properties cannot have `[Required]`.
- `[DefaultValue(2)]` supplies a typed default for an integer input; `[DefaultValue("customer-db")]` supplies a string. Build validation refuses values that cannot be assigned or converted to the property type, and defaults on properties without a public setter. A mapping or YAML step default takes precedence. The engine applies an attribute default before saving the input copy, so every retry starts from that same saved value, even if the failed attempt changed its property. An initializer is separate: a defaulted input may keep `= string.Empty;` and the engine supplies its declared default.
- `[Display(Name = "Label", Description = "Help")]` supplies labels and help for inputs and outputs, including both sides of in-out properties. The label falls back to the property name.
- `[UIHint("code")]` selects the input control. The catalog validates `text`, `code`, `binding`, and `connection`; an omitted hint means `text`.

The catalog keeps default values typed when serializing them. Its `Fields` describe inputs; `Outputs` maps each output property name to its label and help. In-out properties appear in both.

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

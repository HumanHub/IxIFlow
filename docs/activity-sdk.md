---
title: Activity SDK
description: Define activity contracts, metadata and installed catalogs.
---

An activity implements `IAsyncActivity.ExecuteAsync(IActivityContext, CancellationToken)`. Its contracts and attributes live in `IxIFlow.Abstractions`; an activity library can reference that package alone. Metadata is optional for direct fluent use. Attributed activities also expose stable keys to YAML and the installed catalog.

## Declare inputs and outputs

This complete activity is compiled in [Quote.cs](../samples/IxIFlow.Samples/Quote.cs) and exercised by the sample tests. Imports are `System.ComponentModel`, `System.ComponentModel.DataAnnotations`, `IxIFlow.ActivitySdk` and `IxIFlow.Core`, plus implicit .NET usings.

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

The three direction markers have no settings of their own:

| Marker | Contract |
| --- | --- |
| `[ActivityInput]` | Public setter required |
| `[ActivityOutput]` | Public getter required |
| `[ActivityInputOutput]` | Both public setter and getter required |

Once any property uses a marker, only marked properties belong to the contract. Without markers, public setters are inputs and public getters are outputs. An in-out property still needs explicit input and output mappings when both directions should reach workflow data.

## Use .NET metadata

| Attribute | Behavior |
| --- | --- |
| `[Required]` | Requires an input mapping or declared default at build time. Before execution, null/empty values fail validation with an InvalidInput message and `WorkflowErrorKind.InvalidRequest`. Output-only properties cannot be required. |
| `[DefaultValue(...)]` | Supplies a typed input default. Unassignable/unconvertible defaults and defaults without public setters fail validation. Explicit mappings or YAML step defaults override it. |
| `[Display(Name = ..., Description = ...)]` | Labels and help for inputs and outputs; absent labels fall back to property names. |
| `[UIHint(...)]` | Input-control hint; the catalog accepts `text`, `code`, `binding` and `connection`, defaulting to `text`. |

Attribute defaults are applied before saving the input snapshot. A retry receives the saved values even if a prior attempt mutated its inputs. A property initializer alone does not satisfy a required mapping/default contract. `[Required]` checks null and empty strings; whitespace and empty collections pass this engine check, regardless of `AllowEmptyStrings`. Public getter-only activity state needs a setter (which may be private) for checkpoint restoration, or exclusion with `[JsonIgnore]`.

The bundled analyzers check mapping direction, required mappings, accessors and convertible defaults when the activity type is used. Full validation also runs through fluent `Build()` and `WorkflowDefinitionValidator.Validate`; custom conversion code is checked at build time rather than executed by an analyzer.

## Register installed activities

`[WorkflowActivity]` supplies the key plus optional `Version`, `Name`, `Category`, `Icon` and `Designer`. Register installed activities through the core builder, using `IxIFlow.Extensions`:

<!-- snippet: samples/IxIFlow.Samples/Contracts.cs#register-activities -->
```csharp
services.AddIxIFlow()
    .AddActivities(typeof(AddFee).Assembly);
// To select one activity instead:
// services.AddIxIFlow().AddActivity<AddFee>();
```

Register constructor dependencies, such as `IDemoConnectionFactory`, in the same service collection. Activities must be visible, concrete, non-generic types in the default assembly load context. Registration checks their declarations and refuses invalid attributed types. Repeating a type is harmless; duplicate activity keys or package ids from different assemblies are errors.

The package id comes from the normalized assembly name. Its version comes from the informational version with build metadata removed, falling back to the assembly version. `IActivityRegistry` exposes descriptors with typed inputs, outputs, converted JSON defaults, labels, help and controls. In-out members appear on both sides. [Contracts.Catalog](../samples/IxIFlow.Samples/Contracts.cs) shows this registration.

Each registered activity has exactly one unkeyed transient DI registration. Register an application factory before `AddActivity` or `AddActivities`: these methods use `TryAddTransient`, preserving an existing registration. Adding a factory afterwards leaves two descriptors and is refused at startup. Factories are honoured on first execution and restoration and must return a fresh object every time. Disposable transient activities are disposed when the run scope ends. Fluent activity types that were never registered and bundle activities are constructed directly with constructor dependencies; saved properties are restored after construction.

Every store records the selected package metadata and the host that registered it. Canonical activity JSON includes member types, and its SHA-256 fingerprint identifies that metadata. Different selections or metadata for one package version coexist. Listings retain packages without live hosts and filter host marks using the store clock. These records do not advertise capabilities or supply publishing dependencies; published workflows use packages pinned by artifact digest.

[Demo activities](../samples/IxIFlow.DemoActivities) include a database activity that receives a host-provided connection factory through its constructor and uses a connection reference instead of credentials.

## Execution context and external effects

`IActivityContext` supplies services, a logger, instance/workflow identity, correlation, invocation and attempt IDs, previous attempt errors/notes, and optional recovery state. The invocation ID identifies a logical occurrence; the attempt ID identifies a physical execution or recovery attempt. A step retry keeps the invocation identity; executing an undone saga step again creates another invocation.

Mapped inputs are copies. Output mappings propagate results. `WorkflowData` and `PreviousStepData` on the activity context are explicit live-object access paths; direct changes are saved at the next commit. Do not retain those objects outside the invocation.

Implement `IRequestActivity<TReply>` when sending a request that expects an engine reply; `context.Reply` supplies its token/address. Implement `IRecoverableActivity<TState>` when an interrupted external effect can be checked safely. [Recovery.cs](../samples/IxIFlow.Samples/Recovery.cs) and the [execution guide](execution-model.md#interrupted-activities) show the recovery and operator-resolution paths.

Broader catalog services and published activity packages/code activities are [planned](roadmap.md).

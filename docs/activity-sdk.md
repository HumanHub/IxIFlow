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

`[WorkflowActivity]` supplies the key plus optional `Version`, `Name`, `Category`, `Icon` and `Designer`. The `IxIFlow.Authoring` package provides `ActivityPackageRegistry` in the `IxIFlow.ActivitySdk` namespace. Its `AddAssembly(assembly, packageName, packageVersion)` method scans exported attributed types. `RegisterActivities(services)` registers resolved implementations with DI.

[Contracts.Catalog](../samples/IxIFlow.Samples/Contracts.cs) compiles installed-assembly catalog registration. Its manifest's `Fields` describe input controls, labels, help and required flags; `Defaults` holds declared attribute values, and `Outputs` describes output labels/help. In-out properties appear on both sides. `AddPackage(manifestYaml, assembly)` and `AddEmbeddedPackage` accept installed package manifests too. This registry does not fetch or isolate assemblies; the application installs dependencies.

Parameterless activities can be constructed automatically. Register activities with constructor dependencies in DI. [Demo activities](../samples/IxIFlow.DemoActivities) include a database activity that consumes a host-provided connection factory and a connection reference instead of embedding credentials.

## Execution context and external effects

`IActivityContext` supplies services, a logger, instance/workflow identity, correlation, invocation and attempt IDs, previous attempt errors/notes, and optional recovery state. The invocation ID identifies a logical occurrence; the attempt ID identifies a physical execution or recovery attempt. A step retry keeps the invocation identity; executing an undone saga step again creates another invocation.

Mapped inputs are copies. Output mappings propagate results. `WorkflowData` and `PreviousStepData` on the activity context are explicit live-object access paths; direct changes are saved at the next commit. Do not retain those objects outside the invocation.

Implement `IRequestActivity<TReply>` when sending a request that expects an engine reply; `context.Reply` supplies its token/address. Implement `IRecoverableActivity<TState>` when an interrupted external effect can be checked safely. [Recovery.cs](../samples/IxIFlow.Samples/Recovery.cs) and the [execution guide](execution-model.md#interrupted-activities) show the recovery and operator-resolution paths.

Broader catalog services, published activity packages/code activities and isolated version loading are [planned](roadmap.md).

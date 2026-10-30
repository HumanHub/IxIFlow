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

Workflow data has the corresponding `[WorkflowInput]`, `[WorkflowOutput]` and `[WorkflowInputOutput]` markers in the same namespace and package. They have the same accessor requirements and use the .NET metadata below. Workflow markers describe child data independently of activity markers, including when a data type also implements `IAsyncActivity`. Root fluent declarations can add members; see [workflow contracts](fluent-api.md#declare-workflow-data).

## Use .NET metadata

| Attribute | Behavior |
| --- | --- |
| `[Required]` | Requires an input mapping or declared default at build time. Before execution, null/empty values fail validation with an InvalidInput message and `WorkflowErrorKind.InvalidRequest`. Output-only properties cannot be required. |
| `[DefaultValue(...)]` | Supplies a typed input default. Unassignable/unconvertible defaults and defaults without public setters fail validation. Explicit mappings or YAML step defaults override it. |
| `[Display(Name = ..., Description = ...)]` | Labels and help for inputs and outputs; absent labels fall back to property names. |
| `[UIHint(...)]` | Input-control hint; the catalog accepts `text`, `code`, `binding` and `connection`, defaulting to `text`. |

Attribute defaults are applied before saving the input snapshot. A retry receives the saved values even if a prior attempt mutated its inputs. A property initializer alone does not satisfy a required mapping/default contract. `[Required]` checks null and empty strings; whitespace passes this engine check, regardless of `AllowEmptyStrings`. Public getter-only activity state needs a setter (which may be private) for checkpoint restoration, or exclusion with `[JsonIgnore]`.

For workflow data, attribute defaults apply to child invocation inputs. Root starts validate the data the client supplies without applying attribute defaults; use a property initializer for client-facing defaults. Labels, descriptions and control hints are retained in the workflow's portable contract alongside its converted defaults.

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

The [activity and package catalog](activity-catalog.md) lists local and artifact providers. [YAML code activities](yaml-authoring.md#code-activity-documents) publish as NuGet packages; fluent applications reference the package and use its generated type. YAML consumers import the exact package and select the activity key and integer activity version. The [published activity sample](../samples/IxIFlow.Samples/PublishedActivities.cs) and its [tests](../tests/IxIFlow.Samples.Tests/PublishedArtifactSampleTests.cs) exercise both consumers from the published bytes.

## Consume a published activity

Add a PackageReference to `acme.double` version `1.0.0` from the authorized publishing feed. The generated type has ordinary typed inputs and outputs:

<!-- snippet: samples/IxIFlow.Samples/PublishedActivities.cs#package-consumer-fluent -->
```csharp
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Samples;

public static class DoubleConsumer
{
    public static WorkflowDefinition Build() => Workflow.Create<PublishedNumber>("FluentDouble")
        .Step<IxIFlow.Activities.Acme.Double>(step => step
            .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Value)
            .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Value))
        .Build();
}
```

The sample test compiles this source against the DLL extracted from the published nupkg and executes it. Register `AddActivities` when the application also needs installed catalog metadata or custom activity factories. [Package publication and withdrawal](activity-catalog.md#publication-withdrawal-and-retention) describes immutable identities, retained bytes and pinned consumers.

## Scope activities

Implement `IScopeActivity<TValue>` for a resource shared by a nested workflow body. `TValue` is a reference type and the lookup key. A scope has `OpenAsync(ScopeOpening, IScopeContext, CancellationToken)` and `CloseAsync(TValue, ScopeClosing, IScopeContext, CancellationToken)`; it does not implement `IAsyncActivity`. Configure it with the continuation builder's `Scope<TScope>(setup, body)` method. Scope setup offers ordinary input/output mappings, `Retry(RetryPolicy)`, and `Threading(ScopeThreading)`.

Body activities obtain the nearest assignable resource with `context.Scope<TValue>()`, or use `ScopeOrDefault<TValue>()` for an optional resource. `[RequiresScope<TValue>]` adds lexical validation and descriptor metadata. An inline reusable workflow can declare `.RequiresScope<TValue>()` on its root builder. Its caller must supply that scope. Independent child invocations and root starts cannot supply it.

The engine saves configuration and reconstructs the resource on each pass that needs it. Open must be repeatable with the same copied inputs and `context.InvocationId`, which is committed before the first open. `ScopeOpening.RecordedOpens` counts opens recorded by successful commits. Transient session state is lost at waits, parks, pauses, recovery, and host moves; store required state in workflow data or the remote system. A database transaction belongs in a single activity.

Lifecycle code receives identity, services, logging, and enclosing scopes through `IScopeContext`; it has no workflow data or previous value. Scope inputs cannot be delegates. Public scope properties are saved configuration and outputs: they must serialize and must never reach the live resource, including through an object, collection, or wrapper. Body activities must likewise keep resource references out of their saved state. Outputs and the previous value come from a copy saved after the first recorded open. Re-open and close mutations do not change those outputs.

Close is best-effort cleanup, invoked at most once per live resource and possibly never. Its reason is `Completed`, `Failed`, `Cancelled`, or `Suspended`. A completed close failure fails the scope step; other close failures preserve the existing outcome. Required effects, such as saving a workbook, belong in recoverable body activities. On termination, ownership loss, or abandoned callbacks, the engine performs local release after every user of the resource has ended. It prefers `IAsyncDisposable` to `IDisposable` and never disposes again after a successful normal close. Remote sessions may survive a process crash.

`[ScopeConcurrency(ScopeConcurrency.Serialized)]` is the default: an entire author callback holds each enclosing serialized resource across its awaits. `Shared` permits concurrent callbacks and is an author promise about resource safety. Nested permits are acquired together. Do not await workflow work that needs a resource permit the current callback holds; express that dependency as workflow steps after returning.

`[ScopeThreading(ScopeThreading.ThreadAffine)]` requires a dedicated thread. A use site may strengthen an ordinary scope with `.Threading(ScopeThreading.ThreadAffine)`. Nested resources share the enclosing dedicated thread. All body author callbacks, including recovery-state capture, recovery, compensation, and unannotated dynamic lookup, run there. Captured awaits return there; `ConfigureAwait(false)`, `Task.Run`, detached work, and explicit thread switches cannot touch the resource. Lookup rejects the wrong thread or an expired operation. Never synchronously block on work that needs this thread, synchronously send to it from another thread, or re-enter workflow execution from an affine callback.

On Windows the thread is STA and pumps managed continuations and native messages. On other platforms it provides managed thread affinity without COM or a platform UI thread. Windows-only resources must fail explicitly on unsupported platforms and use deployment capabilities for placement. Resource creation belongs in open, not a constructor or property getter. A disposable COM wrapper must release its own references on the owning thread; the engine does not choose a COM release policy.

Close has a fresh cleanup token and a bounded wait, normally 30 seconds. Timeout does not stop author code: the value, its enclosing resources, permits, services, and thread remain retained until actual completion and local release. The engine bounds its release wait too. A permanently blocked callback can retain a background thread indefinitely; it is counted against `MaxScopeThreadsPerHost` (default 64). The thread is never aborted or disposed from another thread.

Cancellation uses the original operation token, so synchronous work can poll it while the owning thread is busy. Token registrations run on the cancelling thread. A registration touching an affine resource must asynchronously post to the captured synchronization context and keep the registration alive until that notification finishes. It must never synchronously send or wait for the owning thread. The engine retains the operation and value through its cancellation posts. Failed opens reuse their eligible thread on retry; a thread slot returns after all its resources and operations have finished and the thread has exited.

Scope activities support both [fluent authoring](fluent-api.md#scope-activities) and [YAML scope documents](yaml-authoring.md#scope-activities). The compiled connection, mailbox and workbook samples show host-resolved names and explicit saving. A successful CloseAsync owns normal resource cleanup, including disposal; the engine's fallback disposal is for abandoned values or failed cleanup.

---
title: Fluent C# API
description: Build typed workflows, mappings and control flow.
---

Start with `Workflow.Create<TData>(name, version)`, configure the fluent graph, then call its instance `Build()` to produce a `WorkflowDefinition`. `Build()` validates contracts and structure; `WorkflowDefinitionValidator.Validate` also exposes validation to applications.

Workflow identity is `(name, version)`. A nonblank name is required; definitions no longer receive implicit or hash-derived names. Declare the version and change it when changing behavior.

## Map activity data

With `IxIFlow.Builders`, `IxIFlow.Core` and the sample types in scope, this definition uses the same activity contract as the quick start:

<!-- snippet: tests/IxIFlow.Samples.Tests/ReadmeExamples.cs -->
```csharp
var definition = Workflow.Create<QuoteData>("QuickStart")
    .Step<AddFee>(step => step
        .Input(activity => activity.Customer).From(ctx => ctx.WorkflowData.Customer)
        .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
        .Output(activity => activity.Amount).To(ctx => ctx.WorkflowData.Amount))
    .Build();
```

It compiles and executes in [ReadmeExamples.cs](../tests/IxIFlow.Samples.Tests/ReadmeExamples.cs); [Quote.cs](../samples/IxIFlow.Samples/Quote.cs) extends it with a sequence and condition.

Mapped activity inputs and declared defaults are serialized before invocation; property initializers alone are not frozen inputs. Each attempt receives copies of that saved input; mutating an input object does not mutate workflow data through a shared reference. Outputs explicitly map results back to data after execution. `ctx.PreviousStep` is the typed preceding result in that builder scope; do not rely on it retaining a DI service's dependencies after recovery.

Mapping contexts expose workflow data, typed previous results, loop items and the engine clock. Resume contexts add the accepted event; compensation contexts add the saved activity and, for requests, its reply. Read execution identity and attempt metadata from `IActivityContext`.

The activity context also exposes live `WorkflowData` and `PreviousStepData` objects. Direct changes there participate in the next commit. Prefer declared mappings for reusable contracts and clear data flow. Within a parallel branch, that live data is the branch's isolated copy.

## Control-flow reference

The continuation builder exposes these families. The root builder exposes initial activities, requests and delays; samples that begin with another construct use `new WorkflowBuilder<TData, object>([], name)`.

| Method | Behavior and compiled example |
| --- | --- |
| `Step<TActivity>` | Map typed inputs/outputs and expose the result; [Quote](../samples/IxIFlow.Samples/Quote.cs) |
| `Sequence` | Group ordered steps; [Quote](../samples/IxIFlow.Samples/Quote.cs) |
| `If` | Select a then or optional else path; [Quote](../samples/IxIFlow.Samples/Quote.cs) |
| `WhileDo`, `DoWhile` | Test before or after the body; [Restock](../samples/IxIFlow.Samples/Restock.cs) |
| `Parallel` with `Do` | Fork branches, then all/any/conditional join and optional `Combine`; [ParallelQuotes](../samples/IxIFlow.Samples/ParallelQuotes.cs) |
| `ForEach`, `Result` | Sequential or bounded parallel items and ordered results; [Batch](../samples/IxIFlow.Samples/Batch.cs) |
| `Try`, `Catch`, `Finally` | Typed saved fault projection and durable cleanup; [FailedQuote](../samples/IxIFlow.Samples/FailedQuote.cs) |
| `Saga`, `OnError`, `OutcomeOn`, `Outcome` | Compensable steps, typed outcome branches and error policies; [payment recipe](../samples/IxIFlow.Recipes/Payments.cs) and [saga guide](sagas.md) |
| `Request<TActivity>` | Run an `IRequestActivity<TReply>`, opening its reply address before the effect; [approval recipes](../samples/IxIFlow.Recipes/Approvals.cs) |
| `WaitFor<TEvent>`, `Suspend<TEvent>` | Save an event/reply wait, predicate, output mappings, default suggestion and timeout path; [Review](../samples/IxIFlow.Samples/Review.cs) |
| `Delay` | Durable positive duration, retaining the preceding value; [Reminder](../samples/IxIFlow.Samples/Reminder.cs) |
| `Invoke<TWorkflow,TChildData>(setup)`, `Invoke<TChildData>(name, version)` | Independent child instance with mapped input/output; [Review](../samples/IxIFlow.Samples/Review.cs) |

Workflow classes declare identity once with `[Workflow("Customer approval", Version = 3)]` from `IxIFlow.Core`, and implement only `Build` on `IWorkflow<TData>`. Register with `services.RegisterWorkflow<TWorkflow>()` or `services.RegisterWorkflow<TWorkflow,TData>()`; invoke with `Invoke<TWorkflow,TChildData>(setup)`. Both read the attribute from the type without constructing the class to discover identity. A missing attribute, blank name or nonpositive version is refused. The parent's structure hash includes the child's declared name and version. Registration constructs the class only when building its definition. Named children use `Invoke<TChildData>(name, version, setup)` and require that exact identity in `IWorkflowCatalog`. Both forms execute the registered definition and verify its structure hash. Every process that can resume the parent or child must load matching definitions.

## Parallel work and lists

General parallel joins wait for all branches by default. `WaitAny()` selects the first completed branch; `WaitConditionally` evaluates provisional merged data after completions. Losing branches unwind cancellation before the parent continues. The [execution guide](execution-model.md#parallel-data-and-joins) explains conflict rules and combiners.

`ForEach` snapshots its item list at entry. The body receives a typed handle; read it with `ctx.Item(handle)` and its position with `ctx.Index(handle)`. Nested bodies may read enclosing handles. Items are read-only copies and handles cannot escape their scope.

End result-producing paths with `Result`; `Into` writes a `List<ItemResult<T>>` in input order. Setup can select `Parallel(maxConcurrency: ...)`, `WaitAny`, `WaitConditionally<T>` and combiner overrides. [Batch.cs](../samples/IxIFlow.Samples/Batch.cs) demonstrates all modes, including expression-preserving overloads. Use named arguments such as `itemsExpression`, `valueExpression` and `completeWhenExpression` when matching a YAML twin's structure hash; expression overloads retain tree shape and member references, while delegate bodies are ignored.

## Faults and recovery

A typed `Catch<TException,TFault>` or saga handler saves a projection of the exception. Public fault properties must match readable exception properties by name and type. Handler mappings use `ctx.Fault`. The live exception is not the durable contract. `Catch` covers its preceding `Try` body, and `Finally` runs during normal completion and unwinding. Saved handler waits continue from their saved position.

Saga step retries and saga retries are different scopes. See [sagas](sagas.md), and use [recoverable activities](execution-model.md#interrupted-activities) when an effect may have happened before its result was committed.

## Identity and expressions

`WithId(id)` sets a unique author step ID and captures caller file/line by default; explicit source arguments are available. Fluent step objects otherwise receive random IDs; YAML supplies explicit or structural IDs. Persisted continuations and trace step references use the runtime's structural paths, independently of these author IDs. `Named(name)` changes the step's name. Step names, author IDs and source locations are presentation metadata and do not change the structure hash.

The checkpoint stores a SHA-256 `StructureHash` of the executable structure. Step kinds and order, scope topology, activity/data/previous types, mapping contracts, wait keys and event/reply types, durations, join modes and combiner types (including attributes on data types), loop/result contracts, exception/fault types, handler policies and child identities with versions change it. Expression trees contribute node shape, literal nodes and referenced members, with parameter names and closure receivers normalized. Delegate bodies, helper implementations, captured values, descriptions, display names, tags and step names do not contribute. Changing behavior still requires a new workflow version.

Registration refuses the same `(name, version)` with a different structure hash. Equal structure with different descriptions is accepted; the first registered definition supplies metadata. Start, resume, recovery and child resolution refuse incompatible saved state and report the workflow, version and both hashes. Checkpoint schema 4 refuses older saved instances without migration. Reset development state and coordinate host restarts for this one-time break.

Conditions, mappings, result selectors, wait predicates and combiners can run again after recovery. Keep them pure. Use `ctx.UtcNow` for workflow time, not a system-clock call or external I/O.

Build validation rejects known clock/environment reads, random values, `Guid.NewGuid`, blocking/delays and supported file, network and database I/O in expression trees and delegate IL. It follows nested lambdas/local functions, not ordinary helper bodies. The bundled analyzer reports `IXIF0101` for expressions and `IXIF0102` for combiners, following helper code in the same compilation and checking combiner methods, constructors and initializers. Neither check is a sandbox for arbitrary C#; build validation does not inspect combiner implementations.

Run the resulting definition through [the client](getting-started.md#understand-the-result). C# definitions and [YAML](yaml-authoring.md) share these runtime rules.

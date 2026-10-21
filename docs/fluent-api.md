---
title: Fluent C# API
description: Build typed workflows, mappings and control flow.
---

Start with `Workflow.Create<TData>(name, version)`, configure the fluent graph, then call its instance `Build()` to produce a `WorkflowDefinition`. `Build()` validates contracts and structure; `WorkflowDefinitionValidator.Validate` also exposes validation to applications.

Workflow identity is `(name, version)`. A nonblank name is required; definitions no longer receive implicit or hash-derived names. Declare the version and change it when changing behavior.

## Flowcharts

`Flowchart` is available on root and continuation builders, including sequence, branch, loop, catch, timeout and ForEach bodies. Each node body receives the graph's incoming previous type; its edge builder receives the type returned by the body lambda.

<!-- snippet: samples/IxIFlow.Samples/Flowcharts.cs#stock-flowchart -->
```csharp
public static WorkflowDefinition Definition() =>
    Workflow.Create<Order>("FulfilOrder")
        .Flowchart(f => f.Start("check")
            .Node("check", n => n.Step<CheckStock>(), edges => edges
                .When(ctx => ctx.PreviousStep.InStock, to: "ship")
                .Otherwise(to: "backorder"))
            .Node("ship", n => n.Step<Ship>())
            .Node("backorder", n => n.Delay(TimeSpan.FromDays(1)),
                edges => edges.Always("check")))
        .Build();
```

[Flowcharts.cs](../samples/IxIFlow.Samples/Flowcharts.cs) contains the complete compiled example and its activity types.

Use `.Node("route", edges => edges.Switch(ctx => ctx.WorkflowData.Region).Case("EU", "eu").Otherwise("other"))` for an empty switch node. Guards and selectors retain their expression trees for purity validation and hashing. A node cannot mix guards with switch cases. Keys are non-empty, ordinal-unique, contain no `/`, and cannot be `end`. Start and destination keys must exist. A null destination ends the chart; a node with no matching edge ends with `NoTransition`.

`.Named(name)` on the flowchart builder names the preceding node. `.WithId("approval")` on the enclosing workflow builder gives generated picks author IDs such as `approval/pending/enter`, `approval/pending/decision` and `approval/pending/to/0`. Every pick appears in `StepTable`.

A timeout path directly leaving a node must end in `.Transition("destination")`. Explicit transitions elsewhere are build errors. Graphs preserve their incoming previous value after completion. A saga may run inside a node; saga activity bodies retain their existing restricted builder surface. YAML, schema and generator support follow separately.

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

The continuation builder exposes these families. The root builder exposes initial activities, requests, delays, flowcharts and state machines; samples that begin with another construct use `new WorkflowBuilder<TData, object>([], name)`.

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

Workflow classes declare identity once with `[Workflow("Customer approval", Version = 3)]` from `IxIFlow.Core`, and implement only `Build` on `IWorkflow<TData>`. Register with `services.RegisterWorkflow<TWorkflow>()` or `services.RegisterWorkflow<TWorkflow,TData>()`; invoke with `Invoke<TWorkflow,TChildData>(setup)`. Both read the attribute from the type without constructing the class to discover identity. A missing attribute, blank name or nonpositive version is refused. The parent's structure hash includes the child's declared name and version. Registration constructs the class only when building its definition. Named children use `Invoke<TChildData>(name, version, setup)` and require that exact identity in `IWorkflowCatalog`, with the executable definition loaded locally. Both forms execute the registered definition and verify its structure hash. Every process that can resume the parent or child must load matching definitions.

## Parallel work and lists

General parallel joins wait for all branches by default. `WaitAny()` selects the first completed branch; `WaitConditionally` evaluates provisional merged data after completions. Losing branches unwind cancellation before the parent continues. The [execution guide](execution-model.md#parallel-data-and-joins) explains conflict rules and combiners.

`ForEach` snapshots its item list at entry. The body receives a typed handle; read it with `ctx.Item(handle)` and its position with `ctx.Index(handle)`. Nested bodies may read enclosing handles. Items are read-only copies and handles cannot escape their scope.

End result-producing paths with `Result`; `Into` writes a `List<ItemResult<T>>` in input order. Setup can select `Parallel(maxConcurrency: ...)`, `WaitAny`, `WaitConditionally<T>` and combiner overrides. [Batch.cs](../samples/IxIFlow.Samples/Batch.cs) demonstrates all modes, including expression-preserving overloads. Use named arguments such as `itemsExpression`, `valueExpression` and `completeWhenExpression` when matching a YAML twin's structure hash; expression overloads retain tree shape and member references, while delegate bodies are ignored.

## Native state machines

`StateMachine` is available wherever `Flowchart` is offered: root and continuation builders, sequences, branches, loops, items, catches, timeout paths, graph entry/exit and action bodies. Saga bodies retain their existing activity/request shape.

<!-- snippet: samples/IxIFlow.Samples/StateMachines.cs#approval-state-machine -->
```csharp
public static WorkflowDefinition Definition() =>
    Workflow.Create<Approval>("ApproveOrder")
        .StateMachine(m => m.Start("pending")
            .State("pending", s => s
                .Entry(e => e.Request<SendApprovalMail>("decision"))
                .Exit(e => e.Step<LogExit>())
                .OnReply<Decision>("decision", t => t
                    .Output(d => d.Approved).To(ctx => ctx.WorkflowData.Approved)
                    .When(ctx => ctx.PreviousStep.Approved, to: "approved",
                        a => a.Step<RecordDecision>())
                    .Otherwise(to: "rejected"))
                .After(TimeSpan.FromDays(3), to: "escalated")
                .On<CancelRequested>("cancel", to: "cancelled"))
            .State("approved", s => s.Final().Entry(e => e.Step<Notify>()))
            .State("rejected", s => s.Final())
            .State("escalated", s => s.Final())
            .State("cancelled", s => s.Final()))
        .Build();
```

[StateMachines.cs](../samples/IxIFlow.Samples/StateMachines.cs) supplies the compiled activities; [StateMachineExamples.cs](../tests/IxIFlow.Samples.Tests/StateMachineExamples.cs) executes the reply, exit, action and final entry.

Use `Start(key)` and `State(key, configure)`. Keys are required, ordinal-unique, non-empty, cannot contain `/`, and cannot be `end`. A null destination or `"end"` completes the machine. `Final()` permits entry only. `Named(name)` names a state without changing its structural identity. A machine may have no final state.

`On<TEvent>(key, to:)`, `OnReply<TReply>(requestName, to:)` and `After(duration, to:)` are single-alternative shorthands. Their configuration overloads expose ordered `When(whenExpression, to, action?)` and last `Otherwise(to, action?)` alternatives. `On` and `OnReply` also expose `Matches(matchesExpression)`, `Input(...).From(...)`, `Output(...).To(...)` and `DefaultPayload(...)`. Input suggestions read the machine's incoming value; output mappings are applied once when the event is dispatched, before its guards. `Matches` is an intake filter, and a false guard consumes the event and reschedules the state. A reply trigger must have a final unguarded alternative and pair with a request directly in entry; the request cannot also pair with `WaitFor`.

`When(whenExpression, to, action?)` on a state adds a guarded immediate trigger; `Otherwise(to, action?)` adds an unconditional immediate trigger and must be last. Already-bound replies and immediates compete in declaration order. A state containing only guarded immediate triggers is refused at build. `After(afterExpression, to:)` and its configuration overload accept a pure expression returning a positive `TimeSpan`. There is no trigger timeout branch: add another `After` transition. Failed guards re-arm timers with full durations.

Entry, exit and action bodies use ordinary fluent steps. Entry's last result is not available to `Arm` or exit; map required results to workflow data. Exit, immediate/timer guards and their actions use the incoming previous type. Event/reply guards and actions use the event type. A timeout directly leaving non-final entry must end with `Transition(key)`; its saved choice runs exit and bypasses trigger actions. Cancellation uses normal `Finally`/compensation and does not execute exit.

`OnActivity<TActivity>(to:)` races one code activity against the other triggers. Its configuration overload takes the same ordered `When`/`Otherwise` alternatives, with activity `Input` and `Output` mappings. Inputs read the machine's incoming previous value; guards and actions read the completed activity. The activity cannot be a request. Only the winning branch's data merges, and losing activities settle before guards run. A false guard re-arms every trigger without repeating entry.

<!-- snippet: samples/IxIFlow.Samples/ActivityTriggers.cs#activity-trigger-state-machine -->
```csharp
public static WorkflowDefinition Definition() =>
    Workflow.Create<Review>("AutomatedReview")
        .StateMachine(m => m.Start("pending")
            .State("pending", s => s
                .OnActivity<CheckRisk>(t => t
                    .Input(a => a.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Output(a => a.Approved).To(ctx => ctx.WorkflowData.Approved)
                    .When(ctx => ctx.PreviousStep.Approved, to: "approved")
                    .Otherwise(to: "manual"))
                .On<Decision>("decision", t => t
                    .Output(d => d.Approved).To(ctx => ctx.WorkflowData.Approved)
                    .When(ctx => ctx.PreviousStep.Approved, to: "approved")
                    .Otherwise(to: "manual"))
                .After(TimeSpan.FromDays(1), to: "manual"))
            .State("approved", s => s.Final())
            .State("manual", s => s.Final()))
        .Build();
```

[ActivityTriggers.cs](../samples/IxIFlow.Samples/ActivityTriggers.cs) supplies the compiled activities; [ActivityTriggerExamples.cs](../tests/IxIFlow.Samples.Tests/ActivityTriggerExamples.cs) executes this sample. Such a state uses one branch per activity plus one shared listener when event, reply or timer triggers exist. This counts against `MaxActiveBranches` and requires copyable workflow data and mergeable named output paths. Alternatives may write the same property. All armed machines in one instance share event keys; an event matching two waits is refused as ambiguous. Reply tokens keep their original state activation and remain usable after a losing listener or failed activity guards.

Code-activity triggers are included; composed workflow triggers are not yet specified. YAML state machines and `GraphTable` remain planned. See the [execution model](execution-model.md#state-machines) for arbitration and recovery.

## Faults and recovery

A typed `Catch<TException,TFault>` or saga handler saves a projection of the exception. Public fault properties must match readable exception properties by name and type. Handler mappings use `ctx.Fault`. The live exception is not the durable contract. `Catch` covers its preceding `Try` body, and `Finally` runs during normal completion and unwinding. Saved handler waits continue from their saved position.

Saga step retries and saga retries are different scopes. See [sagas](sagas.md), and use [recoverable activities](execution-model.md#interrupted-activities) when an effect may have happened before its result was committed.

## Identity and expressions

`WithId(id)` sets a unique author step ID and captures caller file/line by default; explicit source arguments are available. Fluent step objects otherwise receive random IDs; YAML supplies explicit or structural IDs. Persisted continuations and trace step references use the runtime's structural paths, independently of these author IDs. `Named(name)` changes the step's name. Step names, author IDs and source locations are presentation metadata and do not change the structure hash.

The checkpoint stores a SHA-256 `StructureHash` of the executable structure. Step kinds and order, scope topology, activity/data/previous types, mapping contracts, wait keys and event/reply types, durations, join modes and combiner types (including attributes on data types), loop/result contracts, exception/fault types, handler policies and child identities with versions change it. Expression trees contribute node shape, literal nodes and referenced members, with parameter names and closure receivers normalized. Delegate bodies, helper implementations, captured values, descriptions, display names, tags and step names do not contribute. Changing behavior still requires a new workflow version.

Registration refuses the same `(name, version)` with a different structure hash. Equal structure with different descriptions is accepted; the first registered definition supplies metadata. Start, resume, recovery and child resolution refuse incompatible saved state and report the workflow, version and both hashes. Checkpoint schema 6 refuses older saved instances without migration. Reset development state and coordinate host restarts for this one-time break.

Conditions, mappings, result selectors, wait predicates and combiners can run again after recovery. Keep them pure. Use `ctx.UtcNow` for workflow time, not a system-clock call or external I/O.

Build validation rejects known clock/environment reads, random values, `Guid.NewGuid`, blocking/delays and supported file, network and database I/O in expression trees and delegate IL. It follows nested lambdas/local functions, not ordinary helper bodies. The bundled analyzer reports `IXIF0101` for expressions and `IXIF0102` for combiners, following helper code in the same compilation and checking combiner methods, constructors and initializers. Neither check is a sandbox for arbitrary C#; build validation does not inspect combiner implementations.

Run the resulting definition through [the client](getting-started.md#understand-the-result). C# definitions and [YAML](yaml-authoring.md) share these runtime rules.

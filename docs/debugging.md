---
title: Pausing, breakpoints, stepping and edits
description: Pause workflow instances, step through branches, and make validated, audited data edits.
---

# Pausing, breakpoints, stepping and edits

Enable debugging on hosts that will accept operator commands:

<!-- snippet: samples/IxIFlow.Samples/DebuggingExamples.cs#configure -->
```csharp
services.AddIxIFlow(options => options.Debugging = true);
```

Debugging is off by default. The application must authorize access to each instance and operation before calling `IWorkflowOperations`. The actor is an audit label supplied by that authorized application.

<!-- snippet: samples/IxIFlow.Samples/DebuggingExamples.cs#operate -->
```csharp
var operations = provider.GetRequiredService<IWorkflowOperations>();
await operations.PauseAsync(instanceId, actor: "operator@example.com");
await operations.ContinueAsync(instanceId, actor: "operator@example.com");
```

A pause request is saved before it is acknowledged. For a running instance, `PauseAsync` returns the current state while the owner drains: it starts no new steps, lets activities already in flight finish, and commits their results before committing `Paused`. Use `IWorkflowClient.AwaitAsync` or `GetStatusAsync` to observe the committed pause. An idle instance pauses immediately under its execution lease.

A committed pause has no runnable time and releases its lease. Commands live outside the checkpoint, so an owner crash cannot discard a pending pause. Each command has an identity, an order, a pause-generation precondition and a durable acknowledgement. Applying a command and acknowledging it is one transaction; a Continue arriving during a pause commit keeps its wake.

`ContinueAsync` obtains a fresh lease and records the continuation before executing more workflow code. A different enabled host can continue the instance. Every host honors the paused state; a host with debugging disabled refuses both operations and leaves a pending Continue held. A cancellation ends a pause on any host.

Events for existing waits are appended while paused and remain pending until continue. Normal arrival validation still applies. Timers keep advancing; due deadlines are handled when execution continues. Pausing a parent does not pause its children.

`WorkflowDefinition.StepTable` lists each executable step's catalog ID, author ID, name, kind, scope path and source location. Builder calls capture their file and line automatically. Source locations do not affect the workflow structure hash. Snapshot pointers expose the catalog address as `StepId` and the author identity as `AuthorStepId`; frames include loop iteration and ForEach item index. Activity trace entries expose the catalog address as `StepId`.

## Stepping

Choose a live continuation from `IWorkflowClient.GetSnapshotAsync`: its `ExecutionPointer.PointerId` is the continuation ID. A committed pause is required. The application authorizes the instance and operation before issuing the command.

<!-- snippet: samples/IxIFlow.Samples/DebuggingExamples.cs#step -->
```csharp
await operations.StepAsync(instanceId, continuationId, DebugStepKind.Over,
    actor: "operator@example.com");
```

| Kind | Stop |
| --- | --- |
| `Over` | Complete the current step, including its nested scopes and every branch it forks through the join; stop before the selected continuation's next step in the same or an outer scope. |
| `Into` | Stop before the selected continuation's next step at any depth, or the first step in a branch it forks. |
| `Out` | Leave the current scope occurrence, then stop before the selected continuation's next step. A new loop iteration or item is a different occurrence. Leaving the root completes the workflow. |
| `Run` | Run the selected continuation and its newly forked branches until a breakpoint. |

Breakpoints remain active during every kind of step. A step from a breakpoint skips that stopped occurrence once. A step can reach workflow completion without another pick to stop at. Stepping over a loop or ForEach at its entry runs that whole step; stepping out of its body stops at the parent's next pick, which may be the loop guard or ForEach cursor.

Only the selected continuation and branches created during that step may advance. Other branches keep their positions, events remain queued for their waits, their deadlines are deferred, their joins stay open, and their queued ForEach items are not admitted. The workflow execution deadline is also deferred while stepping. Timers still advance on the store clock; Continue processes due work normally.

When selected work cannot advance without waiting for an event, retry, child, or a join involving branches outside the step, the run commits `Paused` with stop reason `Blocked` and releases its lease. Continue resumes all branches. Child workflows run independently; stepping does not enter or pause them.

Step is an ordered command with the current pause generation and an acknowledgement. A fresh lease commits its origin, progress marker and eligible continuations before dispatch. Intermediate commits preserve that state, so recovery resumes the same step after a crash. An incapable host leaves a pending Step or Continue unacknowledged; a host with debugging disabled, or without the condition compiler when conditions are stored, also holds a step interrupted by a crash. Ownership loss always takes precedence over committing a pause.

Snapshot frame names include loop iteration numbers and zero-based ForEach item indices. Frame state carries those numbers and the continuation, parent join, branch sequence and fork activation. Pointers expose `ParentPointerId`, `ParentJoinId`, `Sequence` and `ForkActivationId`; each fork inside a new loop iteration has a distinct activation, so its branches have distinct thread identities.

## Breakpoints

`SetBreakpointsAsync` replaces the instance's entire breakpoint set. Supply stable, unique breakpoint IDs, an actor, and either an author step ID, a catalog path, or a source path and line:

<!-- snippet: samples/IxIFlow.Samples/DebuggingExamples.cs#breakpoints -->
```csharp
var verified = await operations.SetBreakpointsAsync(instanceId,
[
    new Breakpoint("charge", "charge-card"),
    new Breakpoint("third-item", "root/2/foreach/0", HitCondition: "== 3"),
    new Breakpoint("source", Source: new BreakpointSource("order.yaml", 20))
], actor: "operator@example.com");
var current = await operations.GetBreakpointsAsync(instanceId);
```

A function address must resolve uniquely. A source address resolves to the first step in that file at or after the requested line, including generated YAML steps. The returned `StepId` is the resolved catalog path; `Source` retains the requested location, `VerifiedSource` answers the actual line, and `StructureHash` pins the definition used for resolution. Unresolved or ambiguous addresses and invalid hit conditions are refused before saving the change.

An enabled breakpoint counts each visit before its step runs. `HitCondition` accepts `"== n"` (the nth visit), `">= n"` (the nth and subsequent visits), or `"% n"` (every nth visit), with positive `n`. Omit it to match every visit. Counts survive recovery, and an abandoned execution pass abandons its uncommitted counts. Updating an existing ID at the same catalog step preserves its count; removing it and later adding it resets the count. Disabled breakpoints neither count nor pause.

On a hit, all branches stop admitting new work. Activities already in flight finish and commit before `Paused` is saved and the lease released. The committed stop names the branch, step and breakpoint, and the trace records `BreakpointHit`. Continue records a durable skip for that stopped occurrence before dispatch; the first pick of that branch at that step skips all breakpoints once without incrementing counts. Later loop visits and ForEach items can stop again.

Changes use the same ordered, acknowledged command rows and watch as pause and continue, so a change submitted on another host reaches the running owner. The set returned by the operations read includes the latest requested configuration and committed counts; command acknowledgement marks when the owner has applied it. A configuration accepted just before an automatic breakpoint pause remains applicable after that pause. Setting breakpoints does not continue a paused instance; passing an empty list removes them. Debug-disabled hosts ignore breakpoint matching, refuse breakpoint writes, and continue to honor durable pauses.

## Conditions

Install `IxIFlow.Debugging` on hosts that compile conditions, and register its compiler:

<!-- snippet: samples/IxIFlow.Samples/DebuggingExamples.cs#configure-conditions -->
```csharp
using IxIFlow.Debugging;

services.AddIxIFlow(options => options.Debugging = true);
services.AddBreakpointConditions();
```

The core engine has no Roslyn dependency. Debugging hosts can omit the compiler and still pause, continue, and set unconditional breakpoints.

`Condition` is one C# Boolean expression. The typed `context` exposes `WorkflowData`, `PreviousStep` (the output type before the target step), `Item` and nullable `Index` (the innermost enclosing ForEach item and its zero-based position), and `UtcNow`. Outside a ForEach, `Item` is an object with a null value and `Index` is null. When there is no previous output type, `PreviousStep` is an object with a null value.

<!-- snippet: samples/IxIFlow.Samples/DebuggingExamples.cs#conditions -->
```csharp
await operations.SetBreakpointsAsync(instanceId,
[
    new Breakpoint("large-order", "charge-card",
        Condition: "context.WorkflowData.Total > 1000"),
    new Breakpoint("failed-output", "after-charge",
        Condition: "context.PreviousStep.Succeeded == false"),
    new Breakpoint("second-item", "root/2/foreach/0", HitCondition: ">= 2",
        Condition: "context.Index == 1 && context.Item.Amount > 100")
], actor: "operator@example.com");
```

The count increments first, then the hit condition is checked, and only a passing hit condition evaluates the expression. A false expression continues execution. Before a conditional evaluation, the engine commits the count, occurrence, and evaluation time together. Recovery evaluates that same occurrence before taking new events or deadlines, using the saved time for `context.UtcNow`, and never counts it twice. The next ordinary commit saves the result. This adds a checkpoint for each visit that reaches a condition.

The compiler caches successful and refused compilations by workflow structure hash, exact text, and input types. The input types distinguish steps with different previous outputs or items within the same workflow.

### Allowed expressions

The compiler checks every resolved operation and member, including lambda bodies, before emitting code. The allowlist includes:

| Area | Allowed reads and operations |
| --- | --- |
| Inputs | Context properties; instance properties and fields reached through data, previous output, items, or lambda parameters. Property getters are part of the application's data contract; their bodies are not inspected. |
| Expressions | Literals, arithmetic, comparisons, Boolean operators, conditional and null operators, array indexing, constant and relational patterns, casts without application-defined operators, and expression lambdas. Application-defined operators and conversions are refused. |
| Strings | `Length`, character indexing, `IsNullOrEmpty`, `IsNullOrWhiteSpace`, `Substring`, invariant casing, trimming, and string/character overloads of `Replace`, `Concat`, `Equals`, and `Contains`. `Compare`, `Equals`, `Contains`, `StartsWith`, `EndsWith`, `IndexOf`, and `LastIndexOf` may take an explicit `StringComparison.Ordinal` or `OrdinalIgnoreCase`. |
| Math | Numeric operators and constants, and explicitly listed `Math`/`MathF` members: absolute value, min/max, clamp, rounding, sign, powers, logarithms, roots, trigonometry, hyperbolic functions, and the deterministic floating-point helpers. |
| Collections | Array lengths and indexers; standard `List`, `Dictionary`, and `HashSet` counts; list/dictionary indexers; dictionary keys, values and key/value pairs. |
| Nullable | `HasValue`, `Value`, and `GetValueOrDefault`. |
| Date/time values | Calendar components, ticks, UTC components, offsets, duration components, deterministic date arithmetic, Unix-time conversion, and minimum/maximum/epoch constants. The only source of current time is `context.UtcNow`. |
| LINQ | In-memory arrays, lists, dictionaries, sets, and permitted LINQ pipelines, using inline expression lambdas subject to the same allowlist. |

Permitted LINQ methods are `All`, `Any`, `Append`, `Average`, `Cast`, `Chunk`, `Concat`, `Count`, `DefaultIfEmpty`, `Distinct`, `ElementAt`, `ElementAtOrDefault`, `Empty`, `First`, `FirstOrDefault`, `Last`, `LastOrDefault`, `LongCount`, `Max`, `MaxBy`, `Min`, `MinBy`, `OfType`, `Order`, `OrderBy`, `OrderByDescending`, `OrderDescending`, `Prepend`, `Range`, `Repeat`, `Reverse`, `Select`, `SelectMany`, `SequenceEqual`, `Single`, `SingleOrDefault`, `Skip`, `SkipLast`, `SkipWhile`, `Sum`, `Take`, `TakeLast`, `TakeWhile`, `ThenBy`, `ThenByDescending`, `ToArray`, `ToList`, `Where`, and `Zip`. Ordering, equality-based sequence operations, and extrema require numeric comparison types; custom comparers are refused. Arbitrary `IEnumerable` implementations and deferred sequences supplied by application properties are refused, including those returned from a `SelectMany` selector.

For example:

<!-- snippet: samples/IxIFlow.Samples/DebuggingExamples.cs#pure-conditions -->
```csharp
Breakpoint[] examples =
[
    new("total", "charge-card", Condition: "context.WorkflowData.Items.Where(x => x.Amount > 0).Sum(x => x.Amount) > 1000"),
    new("name", "charge-card", Condition: "context.WorkflowData.Name.StartsWith(\"test\", StringComparison.OrdinalIgnoreCase)"),
    new("time", "charge-card", Condition: "context.UtcNow.Hour >= 18")
];
```

Everything outside the allowlist is refused, including helper methods, application constructors, mutation, statement lambdas, I/O, reflection, `dynamic`, wall-clock APIs such as `DateTime.Now`, culture-dependent formatting, string interpolation, and implicit culture-sensitive string comparisons or casing. A refusal raises `BreakpointConditionException` with diagnostics containing a code, message, zero-based text span, and one-based line and column. The entire replacement is refused before saving a command, preserving the installed set.

If an owning host encounters a stored condition it cannot handle, it pauses at that breakpoint. The persisted stop and `BreakpointHit` trace contain `ConditionCompilerUnavailable`, `ConditionCompilationFailed` (with diagnostics), or `ConditionEvaluationFailed` (with the exception type). Exception messages and application values are omitted. A failure takes precedence over an ordinary match at the same step. A mixed fleet therefore stops safely when a debugging host lacks the compiler. Correct or remove the condition, then continue; the stopped occurrence is skipped once as with any other breakpoint.


## Data edits

The application authorizes each edit and supplies the actor. Editing hosts need a secret audit key of at least 32 bytes, obtained from application secret configuration. Use the same key across hosts that apply edits; keep it out of workflow data and debugger responses. A host without a key refuses edits and leaves pending edits paused.

<!-- snippet: samples/IxIFlow.Samples/DebuggingExamples.cs#edit-host -->
```csharp
services.AddIxIFlow(options =>
{
    options.Debugging = true;
    options.DataEditAuditKey = auditKey;
});
```

Read the committed paused revision and pause generation together from the application's inspection view. Give each edit a durable ID, retained by the caller across retries:

<!-- snippet: samples/IxIFlow.Samples/DebuggingExamples.cs#edit-data -->
```csharp
var receipt = await operations.EditDataAsync(instanceId, editId, pausedRevision,
    new DataEditTarget(pauseGeneration),
    [new DataPatch(["Total"], System.Text.Json.JsonSerializer.SerializeToElement(125.50m))],
    actor: "operator@example.com");
```

`DataEditTarget(pauseGeneration)` selects root data. Supply a continuation ID as its second argument to select a live branch's copy. Paths are arrays of serialized property names, including any `JsonPropertyName` names. A path must exist and be settable through the data contract. Dictionary keys, collection indices, extension-data entries and traversal through custom converters are refused; replace their containing settable property instead. `DataPatch.Remove` removes a property before reading the candidate back, so removing a required member is refused.

The host checks status, revision, generation and definition structure while holding the execution lease. It applies all patches to a detached copy and reads the entire candidate through the data type's contract, including converters, required members and setters. It also rebuilds and validates descendant branch copies. A refused patch changes no saved data, revision, command or audit. An earlier pending debug command must be acknowledged before a new edit can be accepted. The checkpoint size limit applies.

A branch edit starts with the parent view plus that branch's saved changes and records the new changes against its parent. They merge through the usual join rules. A root edit with live branches changes the fork baseline: every descendant inherits the new baseline, with its own saved changes overlaid. Branch changes continue to win where they already changed a property. Replacing an ancestor with null can prevent a saved descendant path from rebuilding; that refuses the entire edit. **Combiners receive the edited baseline as their original value**, so editing that baseline can change their eventual merge result.

Success returns a receipt with the edit ID, committed revision and target. The instance stays paused and releases the lease; continue separately when ready. Edits use the same ordered command rows as other debugger operations. A continue accepted during the edit keeps its wake and is applied afterward. Recovery can finish an accepted edit on another editing host.

If a response is lost, retry with exactly the same instance ID, edit ID, expected revision, target, patches and actor. The store returns the original receipt, even if another host has since continued or completed the instance. Reusing an ID with different fields is refused. Receipts last with the retained instance and are removed with it.

Each edit atomically records a `DataEdited` trace entry with its actor, edit ID, target, committed revision and paths. Before and after values are represented only by HMAC-SHA-256 digests using the configured key, bound to the instance, edit, path and before/after position. Values and dictionary key values are absent from the audit. This audit is mandatory even with tracing disabled, activity-only tracing, or exhausted trace limits. A digest cannot be checked by hashing guesses without the secret key.

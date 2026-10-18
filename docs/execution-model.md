---
title: Execution model
description: Checkpoints, ownership, inboxes, parallel state and recovery.
---

A definition is a validated graph and data contract. An instance adds saved data, continuations, scope frames, activity attempts and waits. `IWorkflowClient` submits execution to the core host; the current executor runs inside that host process.

## Checkpoints and commits

Checkpoints save workflow data, branch state, loop/exception frames, saga and compensation progress, previous-step values, retries and waits. History is stored separately in the trace store.

The runner commits at effect boundaries: instance start, activity start and outcome, child start/outcome, opening or closing waits, retry scheduling, completed joins, cancellation/unwinding boundaries and stopping. Ordinary control flow between boundaries can be re-derived after a crash. A condition or mapping may therefore execute more than once.

Each immutable commit includes an execution epoch, commit ID, expected revision and payload hash. Retrying identical content under the same commit ID returns the original receipt. A stale epoch, revision conflict, changed inbox state, retryable infrastructure failure and unknown commit outcome are distinguished by the store protocol. Instance progress and its event settlements, wait changes, child starts, trace entries and terminal outbox event can commit atomically.

Conditions, mappings, wait predicates, result/outcome selectors and combiners must be pure. Read workflow time through `ctx.UtcNow`. Memory and SQLite use the registered `TimeProvider`; SQL Server and PostgreSQL use database time for deadlines and event ordering. Purity validation and analyzers cover supported patterns, not arbitrary external code.

`WorkflowOptions.PersistState = false` skips saved instances for single-call execution. Waits, delays and child invocations are rejected in that mode.

## Leases and recovery

An instance has one executing owner, protected by an instance gate and a renewable store lease with a monotonically increasing epoch. Commits also compare revisions. The host stops work when ownership is lost; another host can claim expired work and continue from its checkpoint.

`RunnableAtUtc` on the instance is the backlog. Local scheduling and store polling recover due, unowned work. A long retry parks the instance and releases its slot and lease; foreground callers continue waiting across that park, while `RecoverAsync` can return `Scheduled`. Always read the returned data.

A completed activity does not rerun merely because another branch waited. An interrupted activity whose start was saved but whose outcome was not saved follows its recovery contract or stops for resolution. External services do not participate in the checkpoint transaction.

## Interrupted activities

`IRecoverableActivity<TState>.CaptureRecoveryState` runs before the first effect and must not itself perform that effect. Its value is saved with the invocation. After interruption, `RecoverAsync` receives the saved state and chooses:

| Recovery result | Meaning |
| --- | --- |
| `Completed` | The outcome is known; restore output properties and continue |
| `Execute` | It is safe to execute the effect |
| `Unresolved(reason)` | Stop at `NeedsResolution` for an operator |
| `Faulted(error)` | Apply ordinary exception/saga handling |

[ReserveQuote](../samples/IxIFlow.Samples/Recovery.cs) uses the invocation ID to look up an external reservation. [RecoveryTests.cs](../tests/IxIFlow.Samples.Tests/RecoveryTests.cs) interrupts the outcome commit to prove recovery and operator resolution.

Without a recovery contract, an uncertain start stops for resolution. `GetPendingActivitiesAsync` lists attempts; `ResolveAsync` saves explicit outputs or a failure, note and actor before continuing. [Recovery.ConfirmAsync](../samples/IxIFlow.Samples/Recovery.cs) compiles that operator call. Resolve the uncertain forward effect before compensating earlier work.

## Inbox, waits and replies

Events enter a durable per-instance inbox before acknowledgement. The executing owner settles them in arrival order, atomically with the progress that consumes or rejects them. Caller-supplied event IDs support deduplication: the same ID/content returns its original outcome; different content conflicts.

`ResumeAsync` addresses an instance's open wait by key or unambiguous event type. `PublishAsync(key, event, eventId)` fans out to instances with that open key. A keyed event can answer only a wait already open when it arrived; it cannot be saved for a future loop iteration. Predicates can reject an event while leaving the wait open. Examine `result.Event` for the decision or refusal.

`Request<TActivity>(name)` opens an activation-specific reply slot before running an `IRequestActivity<TReply>`. The activity sends `context.Reply.Token` to its recipient. A later `WaitFor<TReply>(name)` consumes the answer, including one received while intervening activities ran. A request pairs with exactly one wait in the same body. Optional subjects bind the reply address to what was requested.

`ReplyAsync(token, reply)` addresses that one activation. A reply to a closed token reports answered, timed out, cancelled, replaced or finished as appropriate; it never answers a later request with the same name.

For a human task without a request, configure a discoverable wait. This method compiles in [DocumentationExamples.cs](../samples/IxIFlow.Samples/DocumentationExamples.cs), using `IxIFlow.Builders`, `IxIFlow.Core` and the sample data types:

<!-- snippet: samples/IxIFlow.Samples/DocumentationExamples.cs#discoverable-wait -->
```csharp
public static WorkflowDefinition DiscoverableReview() =>
    new WorkflowBuilder<QuoteData, object>([], "DiscoverableReview")
        .WaitFor<ReviewAnswer>("review", configure: wait => wait
            .DefaultPayload(new ReviewAnswer { Customer = "Ada" })
            .Output(answer => answer.Customer).To(ctx => ctx.WorkflowData.Customer))
        .Build();
```

`GetOpenWaitsAsync` returns request-free tasks with owner instance, activation ID, event type/schema, deadline, token and optional `DefaultPayloadJson`. The default is a UI suggestion; it never answers automatically. A child task belongs to the child instance. Only trusted callers should receive this listing: the token grants the ability to answer.

Inside a method with `client`, `instanceId` and `customer` parameters:

<!-- snippet: samples/IxIFlow.Samples/DocumentationExamples.cs#answer-review -->
```csharp
var waits = await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: instanceId));
var wait = waits.Single();
return await client.ReplyAsync(wait.Token, new ReviewAnswer { Customer = customer });
```

Both examples execute in [DocumentationTests.cs](../tests/IxIFlow.Samples.Tests/DocumentationTests.cs). [Wait recipes](../samples/README.md#wait-and-reply-recipes) cover early replies, subjects, repeat clicks, children and list approvals.

## Deadlines and cancellation

`Delay` persists a timer. Wait setup supports `TimeoutAfter` and `OnTimeout`; without a timeout handler, the timeout follows failure handling. Wait deadlines are stamped by the opening commit; request reply slots are armed when their paired wait is reached. Event arrival and deadlines use the same store clock, so an admissible answer received before the deadline can win even if processed later. Delays and execution deadlines cannot be answered by events.

A caller's cancellation token stops the current call/run and leaves durable state recoverable. `CancelAsync` records durable cancellation, cancels open scopes and unwinds compensation and `Finally`. An uncertain activity may still need resolution during cleanup. Cancellation and timer handling also apply inside nested handlers and compensation.

## Child workflows

Both typed and name/version `Invoke` forms create an independent persisted child with a stable child instance ID and mapped inputs. The parent commits its wait and child start together. Child completion reaches the parent's durable inbox; the runner applies output mappings on the parent's thread.

The default `CancelWithParent` behavior cancels the child and waits for it to settle when the invoking branch is cancelled. Detached children can continue. Child business failures follow the parent's error handling; infrastructure failures leave work recoverable. Missing child data cannot silently skip declared outputs.

Definitions are resolved from the host's registry and checked for compatibility. See [Review.cs](../samples/IxIFlow.Samples/Review.cs) for a pinned child. Child placement demands are planned.

## Parallel data and joins

Each branch starts from an isolated serialization copy of data at the fork. Branch writes cannot race through shared workflow-data references. Changes are tracked against that baseline and saved with their continuation; nested joins first merge into their enclosing branch.

At a successful join, independent property changes merge. Multiple branches changing the same property, or a property and one nested inside it, conflict without a combiner. Lists and other atomic serialized values count as one value. The merged representation is validated before applying it; a conflict raises `JoinConflictException` and leaves parent data unchanged.

Use `Combine<TProperty,TCombiner>` on a join or `[Combine<TCombiner>]` on data properties. A pure `ICombiner<T>` receives the baseline and changed branch values in declaration order. Join overrides take precedence.

| Built-in combiner | Behavior |
| --- | --- |
| `FirstWins<T>`, `LastWins<T>` | Choose by declaration order |
| `Sum<T>` | Add each branch's delta to the baseline |
| `Min<T>`, `Max<T>` | Choose among changed branch values |
| `Append<T>` | Preserve the original list prefix and append each branch's additions |
| `Union<TKey,TValue>` | Merge dictionary keys; conflicting changes to a key fail |
| `Union<T>` | Combine set additions/removals |

All joins wait for all branches by default. `WaitAny` uses the first completion and excludes losing branch outputs. A conditional join tests provisional merged data after completions and also ends when all branches finish. Losing branches unwind cleanup before continuation. Failed/unwound outer joins do not publish losing branch changes.

[ParallelQuotes.cs](../samples/IxIFlow.Samples/ParallelQuotes.cs) and its YAML twins demonstrate all modes and sum merging.

## ForEach

`ForEach` snapshots a finite input list on entry, preserving its order through recovery. Sequential bodies share the evolving enclosing data. Parallel bodies start isolated copies, use the same merge rules, and can cap active items. A suspended item still occupies its item concurrency position.

The typed item handle supplies a read-only item copy and index; nested bodies can read outer handles. Result-producing paths end in `Result`, and `Into` receives one `ItemResult<T>` per input position with `Completed`, `Cancelled` or `NotStarted` status. Partial results are not installed when the construct fails. See [Batch.cs](../samples/IxIFlow.Samples/Batch.cs) and [list recipes](../samples/IxIFlow.Recipes/Lists.cs).

## Resource stops

| Option | Default | Effect |
| --- | --- | --- |
| `MaxCheckpointBytes` | 4 MiB | Stop before saving oversized workflow/execution state |
| `MaxActiveBranches` | 1,000 | Stop before an excessive fork; includes root and waiting parents |
| `MaxForEachItems` | 1,000 | Stop before executing an oversized list |
| `MaxActivitiesPerRun` | 100 | Throttle in-flight activities within a run |
| `MaxCrashes` | 3 | Quarantine after consecutive owner losses without progress |

Limit stops preserve the last checkpoint at `NeedsResolution`. Raise the relevant limit and call `ReleaseAsync`, or cancel. Oversized starts, event submissions or resolutions can be refused with `ResourceExhausted`. `RecoverAsync` does not release operator stops. Trace limits bound diagnostics separately; see [observability](observability.md).

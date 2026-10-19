---
title: Execution model
description: Checkpoints, ownership, inboxes, parallel state and recovery.
---

A definition is a validated graph and data contract. An instance adds saved data, continuations, scope frames, activity attempts and waits. `IWorkflowClient` submits execution to the core host; the current executor runs inside that host process.

## Checkpoints and commits

Checkpoints save workflow data, branch state, loop/exception frames, saga and compensation progress, previous-step values, retries and waits. History is stored separately in the trace store.

The runner commits at effect boundaries: instance start, activity start and outcome, child start/outcome, opening or closing waits, retry scheduling, completed joins, cancellation/unwinding boundaries and stopping. Ordinary control flow between boundaries can be re-derived after a crash. A condition or mapping may therefore execute more than once.

Each immutable commit includes an execution epoch, commit ID, expected revision and payload hash. Retrying identical content under the same commit ID returns the original receipt. The store protocol distinguishes a stale epoch, revision conflict, changed inbox state and a retryable failure that did not apply the commit. The host classifies an infrastructure exception as an unknown commit outcome; the executor resends the same immutable commit, with up to three attempts in total. Instance progress and its event settlements, wait changes, child starts, trace entries and terminal outbox event can commit atomically.

Conditions, mappings, wait predicates, result/outcome selectors and combiners must be pure. Read workflow time through `ctx.UtcNow`. Memory and SQLite use the registered `TimeProvider`; SQL Server and PostgreSQL use database time for deadlines and event ordering. Purity validation and analyzers cover supported patterns, not arbitrary external code.

Every execution acquires a lease and saves its state through fenced commits, including local and single-call execution. Waits, delays and child invocations use the same durable path.

## Leases and recovery

An instance has one executing owner, protected by an instance gate and a renewable store lease. An epoch is never granted twice in a store, and epochs strictly increase for each instance ID, including across deletions and re-creations. Across instances, epochs follow allocation order, with gaps allowed; grants may complete in a different order. Every commit must match the active lease's positive epoch. Commits also compare revisions, and every commit after the first must carry the row's incarnation. The host stops work when ownership is lost; another host can claim expired work and continue from its checkpoint.

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

Without a recovery contract, an uncertain start stops for resolution. `GetPendingActivitiesAsync` lists invocations awaiting resolution, with their reason and required output properties; it does not return attempt history. `ResolveAsync` saves explicit outputs or a failure, note and actor before continuing. [Recovery.ConfirmAsync](../samples/IxIFlow.Samples/Recovery.cs) compiles that operator call. Resolve the uncertain forward effect before compensating earlier work.

## Inbox, waits and replies

Events enter a durable per-instance inbox before acknowledgement. The executing owner settles them in arrival order, atomically with the progress that consumes or rejects them. Caller-supplied event IDs support deduplication: the same ID/content returns its original outcome; different content conflicts.

`ResumeAsync` addresses an instance's open event wait by key or unambiguous event type. `PublishAsync(key, event, eventId)` fans out to instances with that open key. A keyed event can answer only a wait already open when it arrived; it cannot be saved for a future loop iteration. Request replies require `ReplyAsync`. A false or throwing predicate rejects the event as `NotMatched` and keeps the wait open. Examine `result.Event` for `Pending`, `Consumed`, `Rejected` or `Closed`, independently of the instance status.

`Request<TActivity>(name)` opens an activation-specific reply slot before running an `IRequestActivity<TReply>`. The activity sends `context.Reply.Token` to its recipient. A later `WaitFor<TReply>(name)` consumes the answer, including one received while intervening activities ran. A request pairs with exactly one later wait of the same name and reply type in the same body; duplicate request names in that body fail build validation. Optional subjects bind the reply address to what was requested.

Each [reply token](reply-tokens.md) is an opaque capability for one wait. `ReplyAsync(token, reply)` addresses that one activation. A closed token reports `WaitAnswered`, `WaitTimedOut`, `WaitCancelled`, `WaitReplaced` or `InstanceFinished`; an unknown address reports `UnknownWait`. It never answers a later request with the same name. Untargeted events for terminal instances return `Closed` / `InstanceFinished`; addressed events retain the closed wait's specific refusal. Client writes to an active tombstone throw `InstanceRemoved`; an unknown instance throws `InvalidRequest`.

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

`GetOpenWaitsAsync` returns request-free event waits with owner instance, `WaitId`, key, event type and optional schema, opening time, deadline, token and optional `DefaultPayloadJson`. `WaitQuery` filters by instance, key or event type and pages in diagnostic wait-reference order with `AfterWaitRef` and `Limit` (100 by default). Request replies, child-completion waits, delays and execution deadlines are excluded. The default is a UI suggestion; it never answers automatically. A child task belongs to the child instance. Only trusted callers should receive this listing: the token grants the ability to answer.

Wait input mappings compute that suggestion once when the wait opens, reading workflow data and the previous step. They overlay a deserialized copy of `DefaultPayload`, or a new event instance when no default is declared. The wait saves the resulting JSON, so recovery and replay never evaluate the mappings again. Mapping failures fail the wait step. Request reply tokens are reserved before the request runs; their suggestion is computed when execution reaches `WaitFor`, using the completed previous step.

Inside a method with `client`, `instanceId` and `customer` parameters:

<!-- snippet: samples/IxIFlow.Samples/DocumentationExamples.cs#answer-review -->
```csharp
var waits = await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: instanceId));
var wait = waits.Single();
return await client.ReplyAsync(wait.Token, new ReviewAnswer { Customer = customer });
```

Both examples execute in [DocumentationTests.cs](../tests/IxIFlow.Samples.Tests/DocumentationTests.cs). [Wait recipes](../samples/README.md#wait-and-reply-recipes) cover early replies, subjects, repeat clicks, children and list approvals.

## Deadlines and cancellation

`Delay` persists a positive-duration timer and preserves the previous-step value. A timed wait requires both a positive `TimeoutAfter` and a nonempty `OnTimeout` branch; public validation and start reject either one alone. After the timeout branch runs, execution leaves the body containing the wait, skipping its remaining steps. Wait deadlines are stamped by the opening commit; request reply slots are armed when their paired wait is reached. Event arrival must be strictly before the wait deadline on the store clock; an admissible answer can win even if processed later. A newly observed cancellation or a due execution deadline takes precedence over inbox processing. Delays and execution deadlines cannot be answered by events.

A caller's cancellation token stops the current call/run and leaves durable state recoverable. `CancelAsync` records durable cancellation, cancels open scopes and unwinds compensation and `Finally`. The first cancellation request wins. A running instance can return `Running` before cleanup finishes; use `AwaitAsync(id, WorkflowAwaitCondition.Finished)` to wait for its terminal result. An uncertain activity may still need resolution during cleanup. Cancellation and timer handling also apply inside nested handlers and compensation.

## Child workflows

Both typed and name/version `Invoke` forms create an independent persisted child with a stable child instance ID and mapped inputs. The parent commits its wait and child start together. Child completion reaches the parent's durable inbox; the runner applies output mappings on the parent's thread.

The default `CancelWithParent` behavior cancels the child and waits for it to settle when the invoking branch is cancelled. A child awaiting resolution can hold a cancelling parent. With `CancelWithParent(false)`, cancellation closes the parent's child wait and the invoking branch gives up the result and unwinds immediately; an already started child continues. A detached child whose start was never committed is not started; a default child is started and cancelled in the same commit. Child business failures follow the parent's error handling; infrastructure failures leave work recoverable. Cancellation, timeout or termination raises `ChildWorkflowEndedException` at the invocation. Deleting a child, or cancelling a missing child, atomically sends a terminated-child notice to a waiting parent. Missing child data cannot silently skip declared outputs.

Named invocations resolve a pinned `(name, version)` from the host's registry. Typed invocations read `[Workflow("Name", Version = n)]` on the `IWorkflow<T>` class, registered with `services.RegisterWorkflow<TWorkflow>()` before building the service provider. Identity discovery uses reflection without constructing the workflow. The child's declared name and version enter the parent's structure hash; child resolution verifies the registered identity and structure hash. Deactivated definitions refuse new child starts. See [Review.cs](../samples/IxIFlow.Samples/Review.cs) for a named, pinned child. Child placement demands are planned.

## Parallel data and joins

Each branch starts from isolated serialization copies of workflow data and the preceding step value at the fork. Branch writes cannot race through shared workflow-data references. Changes are tracked against that baseline and saved with their continuation; nested joins first merge into their enclosing branch.

At a successful join, independent property changes merge. Multiple branches changing the same property, even to identical values, or a property and one nested inside it, conflict without a combiner. Mutable dictionary classes merge per key; ordinary class objects merge per property. Lists, structs, polymorphic or extension-data objects, objects with constructor-bound getter-only state and custom-converted values merge as whole values. The merged representation is validated before applying it; a conflict raises `JoinConflictException` with a `Path` and leaves parent data unchanged. Branch-data setters must allow rollback of an assignment.

Use `Combine<TProperty,TCombiner>` on a join or `[Combine<TCombiner>]` on data properties. A pure `ICombiner<T>` receives the baseline and changed branch values in declaration order. Join overrides take precedence on the same property. The outermost combiner on a path merges that whole value; an override inside an attribute-combined property is refused. Combiners need a public parameterless constructor and exactly one `ICombiner<T>` matching a settable serialized property.

Build validation rejects statically overlapping output mappings under all/conditional joins without a combiner. A parallel ForEach body under either join requires a combiner even for a single output mapping to a workflow-data property: every item can make the same write. `WaitAny` is exempt from overlap checks because it merges one winner; nested joins are still checked. Unnamed output targets and writes the serializer cannot preserve are also restricted. Direct activity mutations and dictionary-key collisions are checked at runtime. An enclosing catch can handle a join conflict.

| Built-in combiner | Behavior |
| --- | --- |
| `FirstWins<T>`, `LastWins<T>` | Choose by declaration order |
| `Sum<T>` | Add each branch's delta to the baseline |
| `Min<T>`, `Max<T>` | Choose among changed branch values |
| `Append<T>` | Preserve the original list prefix and append each branch's additions |
| `Union<TKey,TValue>` | Merge dictionary keys; conflicting changes to a key fail |
| `Union<T>` | Combine set additions/removals |

All parallel blocks wait for all branches by default. `WaitAny` uses the first completion and excludes losing branch outputs. A conditional join tests the merge of completed branches after each completion and also ends when all branches finish, even if its predicate stayed false. A provisional merge conflict or unhandled branch failure cancels the remaining branches and fails the construct. Losing branches unwind cleanup before continuation. A join that ends through a branch failure, or whose parent is being cancelled, publishes no branch changes at all. Joins inside Finally or compensation still merge during unwinding unless the join itself fails.

[ParallelQuotes.cs](../samples/IxIFlow.Samples/ParallelQuotes.cs) and its YAML twins demonstrate all modes and sum merging.

## ForEach

`ForEach` snapshots a finite input list on entry, preserving its order through recovery. Sequential bodies share the evolving enclosing data. Parallel bodies start isolated copies and use the same merge rules. `maxConcurrency` must be positive; omitting it starts all items, subject to `MaxActiveBranches`. Queued items start in input order. Suspended and nested-joining items still occupy their item concurrency positions. Empty lists finish immediately. Conditional joins receive only completed `ItemResult<T>` values in input order.

The typed item handle supplies a separate item copy per evaluation context and a zero-based index; changes to that copy are not saved. Output mappings through a handle are rejected, and handles cannot be read outside their body. Nested bodies can read outer handles. Result-producing bodies and timeout paths that leave them must end in `Result` of the stored type. Body mappings cannot overwrite the `Into` destination. `Into` receives one `ItemResult<T>` per input position with `Completed`, `Cancelled` or `NotStarted` status. Partial results are not installed when the construct fails. See [Batch.cs](../samples/IxIFlow.Samples/Batch.cs) and [list recipes](../samples/IxIFlow.Recipes/Lists.cs).

## Resource stops

| Option | Default | Effect |
| --- | --- | --- |
| `MaxCheckpointBytes` | 4 MiB | Stop before saving oversized workflow/execution state |
| `MaxActiveBranches` | 1,000 | Limit live branches; excludes root, joining parents, completed and cancelling branches |
| `MaxForEachItems` | 1,000 | Stop before executing an oversized list |
| `MaxActivitiesPerRun` | 100 | Throttle in-flight activities within a run |
| `MaxCrashes` | 3 | Quarantine after consecutive owner losses without progress |

Limit stops preserve the last checkpoint at `NeedsResolution`. Raise the relevant limit and call `ReleaseAsync`, or cancel. Oversized start and resolution checkpoints are refused with `WorkflowErrorKind.ResourceExhausted`. Inbox payloads have no separate byte limit; consuming an event that makes a checkpoint too large can stop the instance. `RecoverAsync` does not release operator stops. Trace limits bound diagnostics separately; see [observability](observability.md).

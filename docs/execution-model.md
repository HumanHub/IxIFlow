---
title: Execution model
description: Checkpoints, ownership, inboxes, parallel state and recovery.
---

A definition is a validated graph and data contract. An instance adds saved data, continuations, scope frames, activity attempts and waits. `IWorkflowClient` submits execution to the core host; the current executor runs inside that host process.

## Flowcharts

A flowchart is a compound step with string-keyed node bodies and ordered edges. Each visit starts a fresh node activation with the graph's incoming previous value. The body's last value is available to its trailing `Decision` and selected `Edge`; after the node leaves, the incoming value is restored before the next node enters. Parallel joins, saga completion and nested scopes finish before a transition is taken.

Guards use first-match order. A switch evaluates its selector once and compares case labels with `Equals`; a fallback edge is last. If no edge matches and there is no fallback, the graph completes with `NoTransition`. Node keys and edge destinations affect definition identity. Node display names, source locations and node declaration order do not.

A timeout branch directly leaving a node must end with `.Transition(key)` (or `.Transition(null)` to end the chart). It lands on the trailing decision with a null previous value; the saved choice bypasses guard evaluation. A timeout in a nested sequence leaves that sequence and continues the node normally.

Cycles are allowed. `MaxGraphTransitionsPerPass` defaults to 1,000 node entries per graph activation between prepared commits. Exceeding it stops for an operator with the source frame and pending transition intact. `ReleaseAsync` takes that transition with a fresh budget. Effects that commit also reset the budget; pure control flow adds no independent commit.

## State machines

A state machine is a compound graph step with keyed entry, exit and transition bodies. Every state visit has a fresh activation. Entry runs once; its last result is routed through workflow-data outputs. Before each `Arm` pick the engine hydrates and restores the machine's incoming previous value. Immediate and timer guards, the shared exit body and the next state's entry read that incoming value. Event and reply guards and actions read the accepted event. A final state runs entry only and completes the machine. A machine without a final state is allowed; it can remain armed until cancellation or its enclosing scope ends.

`Arm` checks transitions in declaration order. An immediate whose guard matches or a reply already bound to the current state can win before waits open. When there are no activity triggers, the state otherwise opens ordinary event, reply and duration waits on its own continuation, without adding branches. Events and replies use the existing addressed-wait, subject and `matches` rules. `matches` rejects an intake without firing; guards run after consumption and output mappings. Reply triggers pair with exactly one request directly in the same state's entry body. A request nested in a sequence or another body must use its ordinary `WaitFor` there. Reply alternatives end with an unguarded case because a request has only one answer.

One fire closes the sibling event and timer waits atomically with its consumption or timeout. Outstanding request replies remain open until a transition is chosen. A reply arriving before `Arm`, or while another fire awaits its guards, binds to that state activation. If all guards fail, entry stays completed and `Arm` arbitrates again, preserving bound answers and original request tokens. Event waits reopen and timers restart with their full durations. Keyed events arriving between closure and the new arming commit are refused `NoOpenWait`. An immediate-only state needs an unconditional fallback; otherwise it has nothing to wake it.

Timers accept positive durations through `After`, including pure duration expressions. The opening commit sends the duration and the store stamps its deadline. An on-time event on the same inbox page takes precedence over due timers; several due timers of one state use declaration order. Guards run inside ordinary workflow failure capture, so a throwing guard can reach an enclosing `Catch`.

Choosing a transition records the edge and trigger, closes all remaining state waits and bound replies, then runs exit, the selected transition's `Trigger` pick, its case `Edge` and action. The destination is entered only after the source and all its children have popped. A timeout path directly leaving a non-final entry body must end with `.Transition(key)`; it passes through normalized `Arm` and exit without a trigger action. Final entry cannot explicitly transition. Cancellation closes waits and unwinds `Finally` and compensation; state exit does not run on cancellation. An exit or action already in progress unwinds through the same rules as other bodies.

A state with code-activity triggers forks one data copy per activity and a shared listener only when event, reply or timer triggers exist. It uses the ordinary fork, cancellation and WaitAny join machinery. The branch limit charges exactly that count, replacing the parent leaf in a nested fork. Each run branch ends at a real `Fire` pick; the listener has real `Listen` and `Fire` picks. Activity outputs settle once in their branch, and listener outputs apply once on acceptance. Guards run on the parent only after losers settle and only the winner's data merges.

The join records one durable `WinningChildId` and the corresponding fired trigger. Several deferred completions compete in persisted child declaration order; late notifications cannot replace the winner or add losing outputs. A loser whose external outcome needs resolution holds the parent at `Joining`, with the fire saved and no guard or exit executed. Recovery continues settlement. A later failure or state cancellation discards the speculative fire and merges no branch data. A successful join returns to `Arm`, preserving its cursor and advancing its occurrence once; false guards clear the fire and create a new arming generation.

Entry requests retain their state activation and original reply token. Their waits and bound answers temporarily move to the listener, then atomically return to the parent before listener completion or speculative cancellation. Both storage owners are marked for the commit. A reply consumed by a listener that loses remains answered and its event consumed; a saved receipt returns to the parent as a bound answer, even if the listener has not yet dispatched that acceptance. Choosing or unwinding the state closes its outstanding replies and drops its receipts.

State keys, final flags, trigger kinds, event keys/types, duration expressions, ordered alternatives and destinations participate in structure hash format 3. State declaration order and display names do not. State frames, fired values and receipts, pending event values, join winners and `ArmOccurrence` use checkpoint contract 7 in existing trunk and continuation parts. Claim-checked entry snapshots and pending trigger payloads are hydrated before synchronous restoration, including when the current step finishes a nested entry body. `MaxGraphTransitionsPerPass` applies to state entries as it does to nodes; native trigger waits do not consume the branch limit.

YAML and fluent definitions lower to these same graph scopes, executable picks and structure hash. YAML adds no execution phase or checkpoint field. `GraphTable` describes the static topology; it contains no saved visit, accepted payload or branch winner. A code-activity trigger is one activity. Composed durable-step and workflow-call triggers remain unspecified, so state-machine support does not yet cover every Windows Workflow Foundation trigger composition.

The fluent API supports event, reply, timer, immediate and single code-activity triggers. Code-activity triggers are included; composed workflow triggers are not yet specified. Activity-valued conditions remain unsupported.

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

Named invocations resolve a pinned `(name, version)` from the host's catalog and require a locally loaded definition. Typed invocations read `[Workflow("Name", Version = n)]` on the `IWorkflow<T>` class, registered with `services.RegisterWorkflow<TWorkflow>()` before building the service provider. Identity discovery uses reflection without constructing the workflow. The child's declared name and version enter the parent's structure hash; child resolution verifies the registered identity and structure hash. Retired versions refuse new child starts. See [Review.cs](../samples/IxIFlow.Samples/Review.cs) for a named, pinned child. Children combine their own definition demands with the parent's saved demands marked `WorkflowDemand.Inherit`; [placement and residency](coordinator-and-hosts.md#placement-and-data-residency) explains the regional pattern.

## Inline workflows

`Use` executes a built workflow body within the caller's instance. Each activation has its own durable data frame. Inputs are copied through the checkpoint serializer after defaults, mappings and required-input validation, so mutable lists and nested objects are isolated. On successful completion, output mappings update the caller and a separate serialized copy becomes the next step's previous value. Failures and cancellation discard outputs. A `Finally` inside the call sees inline data; a handler outside sees caller data.

Parallel branches and item bodies inside a call keep the inline baseline that existed at their fork, including during cancellation, compensation and recovery after the owner frame unwinds. Frame values participate in checkpoint size limits and optional payload claim check. Flowcharts and state machines use that same data ownership, and inline calls can run inside their bodies.

Inspection exposes the data type and value of each inline frame. Debugger workflow-data scopes select the nearest inline frame below the selected stack frame; selecting the caller shows caller data. A paused edit can target a frame by `DataEditTarget.FrameId`. Editing an inline frame with live forked descendants changes their baseline; their saved branch changes are reapplied, following the same rule as editing root data. Step Into enters the body, Over completes the call including joins, and Out leaves its selected enclosing body or graph activation.

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

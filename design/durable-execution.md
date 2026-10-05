# Durable execution model

The sections below describe the target model. The last section records what
the current implementation supports and what remains to be built.

## Scope

The workflow definition is a versioned, structured tree. A running workflow is a
set of continuations over that tree. Each continuation owns a stack of scope
frames. Parallel creates child continuations and a join; it does not flatten
Try, Catch, Finally, loops, or Saga into unrelated step pointers.

The same execution model runs with an in-memory store or a durable store. A
distributed host changes how an instance is claimed and awakened, not how its
steps execute.

## Persisted state

- A definition node has a stable ID within its name and version.
- Every entry into a node has a fresh activation ID. Repeated loop iterations
  therefore have distinct activations even when they use the same definition.
- A continuation has a status, previous value, pending outcome, and an ordered
  stack of frames. A frame records its node ID, activation ID, phase, and next
  child index. Construct-specific state is typed, not an unstructured bag.
- A parallel join owns its child continuation IDs, completion policy, completed
  children, and cancellation progress. Its parent remains parked at the join.
- A wait has an ID, owning continuation and activation, event type, correlation
  key, and state. One instance can have any number of waits.
- A checkpoint has a schema version and revision. The store rejects a save made
  against an obsolete revision or by an obsolete owner.

Live C# stacks, delegates, exception objects, and activity instances are not
persisted. A checkpoint refers to the registered definition and contains only
serializable values and error descriptions.

## Runner

The runner is the only writer of one instance's state. It advances runnable
continuations through short transitions. An activity may perform asynchronous
work, but the runner applies its declared outputs to workflow data in a defined
order. An activity cannot safely mutate the shared workflow data object while
another branch runs.

`WaitFor<TEvent>` registers a wait and parks its continuation. Other branches
keep running. The instance is suspended when no continuation is runnable, no
activity is in flight, and at least one valid wait or timer remains. It is
completed when the root has finished and all required child work is settled.
Unfinished work with no runnable continuation or wake source is a fault.

An accepted event targets one wait by instance, event type, and correlation
key or wait ID. Ambiguous matches are rejected. Event consumption, output
mapping, wait removal, branch wakeup, join updates, and checkpoint revision are
one state transition. Duplicate event IDs do not run a continuation twice.

## Scope behavior

- Sequence saves the index of the next child.
- If saves the selected arm. Resume does not evaluate the condition again.
- Loop saves its phase and iteration. Resume does not repeat prior children.
- Try saves its Try, Catch, or Finally phase and the outcome that must propagate
  after Finally. A failure unwinds the continuation's frames to its nearest
  eligible handler.
- Saga saves the forward or compensating phase, completed effect ledger,
  compensation cursor, and retry state. Saga children use the same scheduler.
- Parallel saves one join and one continuation per branch. A branch reaching a
  wait does not complete the branch. WaitAll is the default. WaitAny joins on
  the first completed branch. WaitConditionally evaluates its predicate after
  each branch completion; when it becomes true, other branches are cancelled.
  If every branch completes with a false predicate, the parallel step ends.

Cancellation removes outstanding waits, requests cancellation of in-flight
activities, and runs required Finally and Saga transitions before the parent
continues. External effects already performed by an activity need their own
business-level reversal or idempotency rules.

## Hosting and recovery

In-process hosting uses a per-instance gate and can use an in-memory store.
Multi-node hosting uses a shared inbox, an instance lease with fencing, and
revision-checked commits. API nodes record events; one worker owns an instance
at a time. The lease is released while the instance waits for external events.
A broker message is a wakeup hint; the inbox and checkpoint are the source of
truth. A repeated delivery is recognized by its event ID.

After a crash, another owner reloads the last committed checkpoint. External
side effects can have succeeded before the checkpoint; activity invocations
need stable idempotency keys and documented retry behavior. A state transaction
alone cannot make arbitrary external systems exactly once.

## Migration

There are no live instances. The old execution driver can be removed once the
new runner passes the existing behavior suite and the new multi-wait and crash
boundary tests. No snapshot conversion or long-term dual runtime is required.

## Current implementation boundary

The structured runner now covers Activity, Sequence, If, Parallel, and
`WaitFor<TEvent>`. It stores one continuation per branch, one wait per parked
continuation, and a join for the parent. It writes a checkpoint at each
transition and can recover a Running instance from that checkpoint. Saved
positions are structural paths; a definition fingerprint rejects a rebuilt
definition with a different shape or step type.

Parallel activities start concurrently. Each branch receives a snapshot of
workflow data; declared output mappings are applied to shared data by the
single runner when an activity finishes. Direct mutations to the branch's
workflow data snapshot are not merged. A losing WaitAny or conditional branch
receives cancellation, and the runner waits for its in-flight activity to
settle before completing the instance.

Try/Catch/Finally, Saga, loops, workflow invocation, and legacy Suspend are
not yet supported by the structured runner. It rejects a definition using
them before executing any activity. Definitions without `WaitFor` or a
non-default parallel join still use the previous runner while parity work
continues.

The instance gate currently protects only processes using this runtime.
Shared-store revision checks, a distributed lease, an event inbox, and host
wakeup integration are still required before multi-node approval delivery.
Activity effects remain at-least-once across a crash boundary and need stable
idempotency keys. The in-memory store provides no process-crash durability.

# Durable execution model

## Execution state

The fluent definition is a versioned tree. The runner saves a checkpoint with a
continuation for each runnable branch, a stack of scope frames for each
continuation, a join for each active parallel step, and a wait for each parked
continuation. A definition fingerprint prevents recovery against a different
tree. Fresh execution and recovery use the same runner.

`WaitFor<TEvent>` parks only its continuation. Other parallel branches keep
running. The instance becomes Suspended when no work is runnable and at least
one wait remains. `WaitAll` is the default join. `WaitAny` joins when one branch
completes; a conditional join evaluates its predicate after each branch
completion. A losing branch receives cancellation, settles in-flight work, and
runs required `Finally` work before the parent moves on.

Parallel activities receive the same workflow data object. Their direct
mutations can overlap. The engine applies declared output mappings as each
activity finishes; workflow authors must synchronize conflicting shared
mutations themselves.

## Checkpoints and effects

The runner saves an attempt before invoking an activity and saves its observed
result afterward. A process can die after an external effect but before the
result checkpoint. On recovery, `IRecoverableActivity` decides whether the
effect completed, should execute, failed, or cannot be determined. An activity
without that contract, or one whose recovery cannot establish the outcome,
stops at `NeedsResolution`. An operator can record a completed or failed
decision with the required outputs and an audit note. The engine does not
promise exactly-once effects in an external system.

Saga uses the same checkpoints. It records each completed forward effect and
compensates in reverse order on failure or business cancellation. A forward
effect whose output cannot be saved stops for resolution; earlier effects are
not compensated while that outcome is unknown. Cancellation of an interrupted
parallel branch likewise keeps an unconfirmed activity at `NeedsResolution`.

Caught exceptions crossing a checkpoint are projected into serializable fault
data. A handler can use `Catch<TException, TFault>` and `ctx.Fault`; whole
exception objects are not persisted. Workflow data, mapped activity outputs,
and operator-provided values must be serializable. A caller's
`CancellationToken` stops the current engine call and leaves a durable instance
recoverable. `CancelWorkflowAsync` records a business cancellation request;
the runner unwinds through `Finally` and saga compensation. If all work has
already finished when the request arrives, the completed result wins.

## Stores and hosting

`AddIxIFlow()` uses an in-memory state repository and runs in the host process.
Memory checkpoints survive calls, not process loss. The optional SQL Server
host shares instance state across processes. A renewable execution lease and
revision-checked commit fence stale writers. Resume delivery has a stable ID
recorded with the checkpoint, so redelivery cannot satisfy a later wait.

SQL commands are claimed, renewed, acknowledged after handling, delayed on
transient failure, and dead-lettered after the configured failure limit. Busy
instances defer a command without spending that limit. A completion outbox
holds terminal results until publication succeeds. Publishers claim pending
results so hosts do not routinely send the same completion. The SQL message
bus uses a stable message ID for each instance's started and completed event.
The SQL host registry creates its tables on first use under a database lock.
Health reports remove unhealthy hosts from routing while preserving their last
status for inspection. Background maintenance retires stale registrations and
prunes old metric samples.
An adapter without durable deduplication may still deliver a completion twice
if publication succeeds and the host dies before recording its receipt;
consumers should deduplicate by instance ID.

## Current limits

- Invoked child workflows have their own persisted checkpoint and a stable
  instance ID. The parent saves projected child waits and resumes when the
  child completes. If cancellation interrupts a child activity before its
  outcome is recorded, the child and parent wait for activity resolution.
- The SQL host still uses in-memory definition registration and event storage.
  Every host must load the same code-defined workflow version and activities.
- The structured runner writes traces into the instance checkpoint. The older
  `IWorkflowTracer` save API is not a separate revision-safe trace store, and
  growing history enlarges checkpoint writes.
- The SQL bus indexes pending polling and prunes acknowledged rows after the
  configured retention period. Dead letters are retained, but need an
  inspection and requeue API.
- Start triggers, a management API, a debugger, and a canonical YAML and editor
  model with fluent parity remain separate product work. SQLite, PostgreSQL,
  and broker adapters are not implemented.

There are no live instances to migrate from an older execution driver. The
structured runner is the sole execution path in this branch.

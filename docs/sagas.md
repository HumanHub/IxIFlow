---
title: Sagas and compensation
description: Reverse compensation, saved handlers and retry scopes.
---

A saga records completed forward steps and their compensations. If later work fails, it can undo completed effects in reverse order. Compensation is application code: it does not roll back a remote service automatically.

## Associate compensation with the effect

[Payments.cs](../samples/IxIFlow.Recipes/Payments.cs) contains the complete payment recipe. A payment request records its provider reference and associates a refund with it. If stock reservation fails, the saga refunds and retries.

The same runnable definition is [saga-payment.workflow.yaml](../samples/IxIFlow.Samples/Definitions/saga-payment.workflow.yaml). Its C# and YAML forms are executed together by [YamlSampleTests.cs](../tests/IxIFlow.Samples.Tests/YamlSampleTests.cs), including the old-token refusal and the fresh request on retry.

Compensation mappings can read `CurrentStep`, the completed forward activity's saved result, and workflow data. Request compensation can also use a typed reply and whether the request was answered. Chained compensations can map incoming `PreviousStep` and `PreviousCompensation`. Declare outputs explicitly when undo work changes workflow data.

## Choose how much to undo

Saga `OnError` handlers can run activities and waits before choosing their compensation/continuation policy.

| Policy | Behavior |
| --- | --- |
| `Compensate()` | Undo every completed step, newest first |
| `CompensateNone()` | Keep completed steps; a retry restarts the failed step |
| `CompensateUpTo<TActivity>()` | Undo through that activity inclusively, preserving earlier steps; the activity must occur exactly once in the saga |

If the selected activity never completed, the up-to policy undoes all completed steps. Kept requests retain their wait and any answer. Undone requests close their waits; retrying those steps creates new invocation IDs and reply tokens.

## Choose what follows

| Continuation | Behavior |
| --- | --- |
| `ThenContinue()` | Continue after the saga once compensation succeeds |
| `ThenContinue(ignoreCompensationErrors: true)` | Continue even when compensation reported errors |
| `ThenTerminate()` | Raise `SagaTerminatedException`, which an enclosing catch may handle |
| `ThenRetry(count)` or `ThenRetry(policy)` | Restart at the first undone step, or the failed step if nothing was undone |
| No continuation | Rethrow the original error after the selected undo |

A compensation failure normally faults after remaining compensation work has been attempted. When retry can no longer proceed because its budget is exhausted, cleanup failed or cancellation intervened, retained steps are undone too. Build rules reject continuation/termination policies that would keep a request whose answer the saga has not read.

## Retry scopes and attempts

An activity-level saga `OnError` can retry, ignore or terminate that step. A saga-level handler controls compensation and restarts part or all of the saga. They have separate budgets.

`RetryPolicy` supplies `MaximumAttempts`, `InitialInterval`, `MaximumInterval` and `BackoffCoefficient`. The current retry limit counts retries after the first execution: a budget of one permits a second execution. The count-only policy constructor uses immediate retry; property defaults use one-second initial delay, ten-second maximum and coefficient two. Long backoffs park durable instances and free host slots.

[RetryQuote.cs](../samples/IxIFlow.Samples/RetryQuote.cs) demonstrates a single-step retry, including `context.Attempt.Number`, earlier errors/notes and distinct attempt IDs. [Payments.cs](../samples/IxIFlow.Recipes/Payments.cs) demonstrates a saga retry with compensation.

## Faults, outcomes and saved progress

Typed `OnError<TException,TFault>` handlers project readable exception properties into a durable DTO with matching names/types. Handler mappings use `ctx.Fault`, plus workflow and previous-step data. The exception object itself is not the persisted contract.

A saga `Outcome` selects a typed value and declares matching branches plus an optional default. Outcome selectors follow the same purity rules as other control flow.

Handler position, accepted events, fault data, completed forward results and compensation progress survive waits and recovery. A resumed handler continues after its saved wait. Retries preserve kept work and replay eligible saved wait outcomes; newly executed requests get the appropriate new activation.

## External effects and uncertainty

Make forward effects and compensation idempotent or recoverable. The engine saves the activity start before invoking it and its outcome afterward. A crash between effect and outcome can require `IRecoverableActivity<TState>` or an operator decision. Earlier work is not compensated until the uncertain outcome is resolved.

Saga activities and compensations currently run in the host process. Durable behavior is available on SQLite, SQL Server and PostgreSQL; memory retains it only during the process lifetime. Read the [execution model](execution-model.md) and [store guide](stores.md).

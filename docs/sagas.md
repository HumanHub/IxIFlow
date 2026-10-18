---
title: Sagas and compensation
description: Reverse compensation, saved handlers and retry scopes.
---

A saga records completed forward steps and their compensations. If later work fails, it can undo completed effects in reverse order. Compensation is application code: it does not roll back a remote service automatically.

## Associate compensation with the effect

[Payments.cs](../samples/IxIFlow.Recipes/Payments.cs) contains the complete payment recipe. A payment request records its provider reference and associates a refund with it. If stock reservation fails, the saga refunds and retries.

The same runnable definition is [saga-payment.workflow.yaml](../samples/IxIFlow.Samples/Definitions/saga-payment.workflow.yaml). Its C# and YAML forms are executed together by [YamlSampleTests.cs](../tests/IxIFlow.Samples.Tests/YamlSampleTests.cs), including the old-token refusal and the fresh request on retry.

Compensation mappings can read `CurrentStep`, the completed forward activity's saved result, and workflow data. Request compensation can also use a typed reply and whether the request was answered. Chained compensations can map incoming `PreviousStep` and `PreviousCompensation`. Declare outputs explicitly when undo work changes workflow data. Forward steps compensate newest first; multiple compensations attached to one step run in declaration order. A failed compensation is recorded and remaining compensations are still attempted.

## Choose how much to undo

The first matching saga `OnError` handler selects the policy. The runtime performs the selected compensation first, then runs the handler's activities and waits, then applies its continuation. A handler containing a wait must declare `ThenContinue`, `ThenRetry` or `ThenTerminate`.

| Policy | Behavior |
| --- | --- |
| `Compensate()` | Undo every completed step, newest first |
| `CompensateNone()` | Keep completed steps; a retry restarts the failed step, except that an activity which returned before its output mapping failed must be undone and rerun |
| `CompensateUpTo<TActivity>()` | Undo through that activity inclusively, preserving earlier steps; the activity must occur exactly once in the saga |

If the selected activity never completed, the up-to policy undoes all completed steps. Kept requests retain their wait and any answer. Undone requests close their waits; retrying those steps creates new invocation IDs and reply tokens.

## Choose what follows

| Continuation | Behavior |
| --- | --- |
| `ThenContinue()` | Continue after the saga once compensation succeeds |
| `ThenContinue(ignoreCompensationErrors: true)` | Continue even when compensation reported errors |
| `ThenTerminate()` | Raise `SagaTerminatedException`, which an enclosing catch may handle |
| `ThenRetry(count)` or `ThenRetry(policy)` | `Compensate()` restarts the saga from its first step, re-evaluating `OutcomeOn`; `CompensateUpTo<TActivity>()` restarts at that activity if it completed, otherwise at the saga's first step; `CompensateNone()` restarts at the failed step |
| No continuation | Rethrow the original error after the selected undo |

A compensation failure ends as `SagaTerminatedException` after remaining compensation work has been attempted. Exhausted saga retries and a failing retry-handler body also terminate the saga. When retry can no longer proceed because its budget is exhausted, cleanup failed or cancellation intervened, retained steps are undone too. Build rules reject continuation/termination policies that would keep a request whose answer the saga has not read.

## Retry scopes and attempts

An activity-level saga `OnError` uses the first matching handler. `ThenRetry` retries the step; `ThenIgnore` skips it and clears its previous-step result. Step `ThenTerminate`, or an exhausted step retry, passes the original failure to the saga's error policy. A saga-level handler controls compensation and restarts part or all of the saga. They have separate budgets.

`RetryPolicy` supplies `MaximumAttempts`, `InitialInterval`, `MaximumInterval` and `BackoffCoefficient`. The current retry limit counts retries after the first execution: a budget of one permits a second execution. `ThenRetry(count)`, on a step and on a saga handler alike, waits one second, then doubles up to ten seconds. A `RetryPolicy` built with only a count retries immediately; property defaults use one-second initial delay, ten-second maximum and coefficient two. Long backoffs park durable instances and free host slots.

[RetryQuote.cs](../samples/IxIFlow.Samples/RetryQuote.cs) demonstrates a single-step retry, including `context.Attempt.Number`, earlier errors/notes and distinct attempt IDs. [Payments.cs](../samples/IxIFlow.Recipes/Payments.cs) demonstrates a saga retry with compensation.

## Faults, outcomes and saved progress

Typed `OnError<TException,TFault>` handlers project readable exception properties into a durable DTO with matching names/types. Handler mappings use `ctx.Fault`, plus workflow and previous-step data. The exception object itself is not the persisted contract.

A saga uses `OutcomeOn(selector, configure)` to select a typed value, with `Outcome(value, body)` branches and an optional `DefaultOutcome(body)`. The first equal label wins; no match without a default raises `InvalidOperationException` through saga failure handling. Outcome selectors follow the same purity rules as other control flow.

Handler position, accepted events, fault data, completed forward results and compensation progress survive waits and recovery. A resumed handler continues after its saved wait. Retries preserve kept work and replay eligible saved wait outcomes; newly executed requests get the appropriate new activation.

## External effects and uncertainty

Make forward effects and compensation idempotent or recoverable. The engine saves the activity start before invoking it and its outcome afterward. A crash between effect and outcome can require `IRecoverableActivity<TState>` or an operator decision. Earlier work is not compensated until the uncertain outcome is resolved.

Saga activities and compensations currently run in the host process. Durable behavior is available on SQLite, SQL Server and PostgreSQL; memory retains it only during the process lifetime. Read the [execution model](execution-model.md) and [store guide](stores.md).

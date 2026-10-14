---
title: Sagas and compensation
description: Compensation, error handlers, and saved saga progress.
---

A saga groups steps that need compensating actions when later work fails. The checkpointed runner records completed steps and runs their compensations in reverse order. The error handler is a saved sequence: its activities and waits resume at the saved position.

An activity may call a remote service. Saga activities execute in the workflow host process; the coordinator routes whole workflow executions to hosts.

## Define compensation

The fluent API lets a saga step name a compensation activity:

```csharp
.Saga(saga =>
{
    saga.Step<ReserveProductActivity>(step => step
            .Input(x => x.ProductId).From(ctx => ctx.WorkflowData.ProductId)
            .CompensateWith<ReleaseProductActivity>())
        .Step<ChargeCustomerActivity>(step => step
            .Input(x => x.Amount).From(ctx => ctx.WorkflowData.TotalAmount)
            .CompensateWith<RefundCustomerActivity>());
})
.OnError<Exception>(error => error.Compensate().ThenTerminate())
```

Compensation is an application action, not an automatic rollback of another service or database. Design it to be safe when retried, and test its behavior when a later activity fails.

`ThenContinue()` runs the remaining workflow after successful compensation. A compensation failure normally faults the workflow after the other compensations have been attempted. Use `ThenContinue(ignoreCompensationErrors: true)` when the workflow should continue despite those failures.

## Read a saved fault in an error handler

Declare a fault type with the exception properties the handler needs. Public property names and types must match the caught exception. IxIFlow checks the fault shape when building the workflow and captures its values when the exception occurs.

```csharp
public sealed record PaymentFault(string Message);

.OnError<PaymentException, PaymentFault>(error => error
    .Step<LogPaymentFailure>(step => step
        .Input(x => x.Message).From(ctx => ctx.Fault.Message))
    .WaitFor<ReviewResponse>("payment-review")
    .Compensate().ThenContinue())
```

The handler can also read `ctx.WorkflowData` and `ctx.PreviousStep`. It does not receive the live exception object. `WaitFor` saves the fault and the handler position, so a new provider can resume the same handler when the state repository survives a restart.

## Resume and persistence

Direct waits inside a saga resume the remaining saga activities. Completed activity results are saved at suspension and restored for compensation if later work fails.

The default state repository is process memory. It can retain an instance across request scopes in the same process, but not across a restart or another host. The optional SQL Server host stores instance state in SQL and uses renewable execution leases for cross-host resume and recovery. Definitions and activity code must still be registered on the resuming host.

## External effects

The engine saves versioned checkpoints, activity attempts, and compensation progress. The SQL host scans for interrupted running instances. Activities that call external systems need idempotency or an explicit recovery policy. An uncertain external outcome is held for an explicit resolution decision before execution continues.

Read [execution model](/docs/execution-model/) and [coordinator and hosts](/docs/coordinator-and-hosts/) for the wider runtime boundary.

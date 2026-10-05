---
title: Sagas and compensation
description: Current saga behavior, the process boundary, and resume limits.
---

A saga groups steps that need compensating actions when later work fails. IxIFlow's `SagaExecutor` runs those activities through the local `ActivityExecutor`. Compensation is also local to that workflow execution.

An activity may call a remote service, but IxIFlow does not dispatch each saga step to a durable remote worker. The current saga is an in-process orchestration pattern.

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
.OnError<Exception>(error => error.Compensate())
```

Compensation is an application action, not an automatic rollback of another service or database. Design it to be safe when retried, and test its behavior when a later activity fails.

## Current resume limits

Direct waits inside a saga now resume the remaining saga activities. Completed activity results are saved at suspension and restored for compensation if later work fails. Regression tests cover repeated waits, an outcome-branch wait, transient failure followed by retry success, and permanent failure followed by retry exhaustion. More complex error-handler and nested resume paths need coverage. A process crash is not recoverable with the default memory state repository.

The default state repository is process memory. It can retain an instance across request scopes in the same process, but not across a restart or another host. The optional SQL Server host stores instance state in SQL and atomically claims one matching suspended instance for resume. Definitions must still be registered on the resuming host, and a claim has no worker lease for crash recovery.

## Distributed saga goal

Reliable distributed saga recovery still needs versioned checkpoints across transitions, recoverable worker leases, durable published definitions, persisted step attempts, and durable compensation progress. SQL state and acknowledged message delivery alone do not provide those guarantees.

Read [execution model](/docs/execution-model/) and [coordinator and hosts](/docs/coordinator-and-hosts/) for the wider runtime boundary.

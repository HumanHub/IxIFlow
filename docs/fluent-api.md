---
title: Fluent C# API
description: Define typed IxIFlow workflows in C#.
---

The fluent API builds a `WorkflowDefinition` from activities, data mappings, and control flow. Use `Workflow.Create<TData>(name, version)` for a builder or `Workflow.Build<TData>(configure, name, version)` for a configuration callback.

## Create a definition

```csharp
var definition = Workflow.Create<OrderData>("OrderProcessing", version: 1)
    .Step<ValidateOrderActivity>(step => step
        .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
        .Output(activity => activity.IsValid).To(ctx => ctx.WorkflowData.IsValid))
    .Build();
```

`OrderData` is a reference type containing the workflow's data. An activity implements `IAsyncActivity` and has public properties for mapped values. `Input(...).From(...)` sets an activity property before it executes. `Output(...).To(...)` copies a property back to workflow data after it executes. The first activity establishes the type of `PreviousStep` for the continuation builder.

The equivalent callback form is:

```csharp
var definition = Workflow.Build<OrderData>(builder => builder
    .Step<ValidateOrderActivity>(step => step
        .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
        .Output(activity => activity.IsValid).To(ctx => ctx.WorkflowData.IsValid)),
    name: "OrderProcessing", version: 1);
```

## Use a previous step and branch

After a step, `ctx.PreviousStep` has the activity's type. Its properties can feed another step or a condition.

```csharp
var definition = Workflow.Create<OrderData>("RouteOrder")
    .Step<ValidateOrderActivity>(step => step
        .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
        .Output(activity => activity.IsValid).To(ctx => ctx.WorkflowData.IsValid))
    .If(ctx => ctx.PreviousStep.IsValid,
        then => then.Step<AcceptOrderActivity>(),
        @else => @else.Step<RejectOrderActivity>())
    .Build();
```

The branch activities are application types implementing `IAsyncActivity`. The `else` branch is optional.

## Compose control flow

After an activity, the continuation builder provides:

| Builder method | Meaning |
| --- | --- |
| `Step<TActivity>` | Execute an activity and expose it as `PreviousStep`. |
| `Sequence` | Group ordered child steps. |
| `If` | Choose a `then` or optional `else` branch. |
| `WhileDo`, `DoWhile` | Repeat a child sequence while a condition holds. |
| `Parallel` | Start branches with `Do`; join when all complete by default, or select `WaitAny` or `WaitConditionally`. |
| `Try`, `Catch`, `Finally` | Handle exceptions with nested steps. |
| `Saga`, `OnError` | Associate compensation activities and error handling with saga steps. |
| `WaitFor<TEvent>`, `Suspend<TEvent>` | Save an event wait and continue with the event as `PreviousStep`. |
| `Delay` | Wait for a positive `TimeSpan`. |
| `Invoke` | Run a child workflow by class or by name and version. |

For example, parallel branches can use the same workflow data and distinct output properties:

```csharp
.Parallel(parallel => parallel
    .Do(email => email.Step<SendEmailActivity>(step => step
        .Input(activity => activity.Address).From(ctx => ctx.WorkflowData.Email)
        .Output(activity => activity.Sent).To(ctx => ctx.WorkflowData.EmailSent)))
    .Do(sms => sms.Step<SendSmsActivity>(step => step
        .Input(activity => activity.Number).From(ctx => ctx.WorkflowData.Phone)
        .Output(activity => activity.Sent).To(ctx => ctx.WorkflowData.SmsSent))))
```

Branches share workflow data, so avoid conflicting writes. `WaitAny()` continues after the first branch completes and cancels the others. `WaitConditionally(predicate)` continues when the predicate becomes true after a branch completes, or after all branches complete.

## Handle errors

`Catch<TException, TFault>` selects exception properties to save in a fault DTO. The DTO's public property names and types must match public properties on the caught exception. Handlers can map `ctx.Fault` into their activities.

```csharp
public sealed record PaymentFault(string Message);
```

```csharp
var definition = Workflow.Create<OrderData>("ChargeOrder")
    .Step<PrepareOrderActivity>()
    .Try(tryBlock => tryBlock.Step<ChargeCardActivity>())
    .Catch<PaymentException, PaymentFault>(catchBlock => catchBlock
        .Step<LogPaymentFailureActivity>(step => step
            .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)))
    .Finally(finallyBlock => finallyBlock.Step<AuditOrderActivity>())
    .Build();
```

`PaymentException` and the activity classes in this example are application types. A `Try` block catches failures from its own child steps. See [sagas](/docs/sagas/) for compensation after a later step fails.

## Wait and resume

Use a correlation key to identify a wait. On resume, the event becomes the previous step value for the next activity.

```csharp
var definition = Workflow.Create<OrderData>("Approval")
    .Step<PrepareOrderActivity>()
    .WaitFor<ApprovalEvent>("manager-approval",
        (approval, ctx) => approval.OrderId == ctx.WorkflowData.OrderId)
    .Step<FinalizeOrderActivity>(step => step
        .Input(activity => activity.Approved).From(ctx => ctx.PreviousStep.Approved))
    .Build();

var started = await engine.ExecuteWorkflowAsync(definition, orderData);
// After started.Status is Suspended:
var resumed = await engine.ResumeWorkflowAsync(
    started.InstanceId, "manager-approval", approvalEvent);
```

`Suspend<TEvent>(reason, condition)` also creates an event wait. The execution result supplies the instance ID and status. The state repository retains the suspended instance; the default repository retains it for the life of the process.

## Invoke another workflow

`Invoke<TWorkflow, TChildData>` resolves a workflow class. `Invoke<TChildData>(name, version, ...)` resolves a registered definition. Both forms map child inputs and outputs like activity properties.

```csharp
.Invoke<ValidationWorkflow, ValidationData>(child => child
    .Input(data => data.OrderId).From(ctx => ctx.WorkflowData.OrderId)
    .Output(data => data.Approved).To(ctx => ctx.WorkflowData.IsValid))
```

Register child definitions and activity code on any host that may execute or resume the workflow. See [sagas](/docs/sagas/) for compensation and [execution model](/docs/execution-model/) for saved state.

## Execute

`IWorkflowEngine.ExecuteWorkflowAsync` accepts a definition and a data object. It returns `WorkflowExecutionResult` with `Status`, `InstanceId`, `WorkflowData`, `ErrorMessage`, and `TraceEntries`. `IsSuccess` is true when `Status` is `Success`.

```csharp
var result = await engine.ExecuteWorkflowAsync(definition, orderData);
if (!result.IsSuccess)
    Console.WriteLine($"{result.Status}: {result.ErrorMessage}");
```

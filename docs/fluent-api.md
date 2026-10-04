---
title: Fluent C# API
description: The current IxIFlow workflow authoring surface.
---

The fluent builder is the current workflow authoring API. It creates a `WorkflowDefinition` containing steps, data mappings, and nested control flow.

## Create a definition

```csharp
var definition = Workflow.Create<OrderData>("OrderProcessing", version: 1)
    .Step<ValidateOrderActivity>(step => step
        .Input(x => x.OrderId).From(ctx => ctx.WorkflowData.OrderId)
        .Output(x => x.IsValid).To(ctx => ctx.WorkflowData.IsValid))
    .Build();
```

`OrderData` is the workflow data type. Each activity implements `IAsyncActivity`. The builder uses activity properties for input and output mapping. After an activity runs, the next step can read `PreviousStep` as well as `WorkflowData`.

## Add control flow

After an activity, the builder supports `If`, `Parallel`, `Sequence`, `WhileDo`, `DoWhile`, `Try` with catches, `Saga`, and `Suspend`. These constructs are part of the C# API; they are not the same as the developing JSON document compiler.

For example, a condition can select one branch from workflow data:

```csharp
.If(ctx => ctx.WorkflowData.IsValid,
    then => then.Step<ProcessOrderActivity>(step => step
        .Input(x => x.OrderId).From(ctx => ctx.WorkflowData.OrderId)),
    @else => @else.Step<RejectOrderActivity>(step => step
        .Input(x => x.OrderId).From(ctx => ctx.WorkflowData.OrderId)))
```

The exact activity property types and mappings belong to your application. Keep each activity focused and test the branches you use.

## Execute

`IWorkflowEngine.ExecuteWorkflowAsync` accepts a definition and a data object. It returns `WorkflowExecutionResult`, including `Status`, `InstanceId`, `WorkflowData`, `ErrorMessage`, and `TraceEntries`.

```csharp
var result = await engine.ExecuteWorkflowAsync(definition, orderData);
if (!result.IsSuccess)
{
    Console.WriteLine(result.ErrorMessage);
}
```

## Authoring beyond C#

The repository has a preliminary JSON workflow document model and compiler. Some constructs are not fully compiled into executable behavior yet. YAML examples are not a supported runtime authoring path, and there is no workflow designer UI. These are planned surfaces; see [status and roadmap](/docs/status-and-roadmap/).

---
title: Getting started
description: Run a first IxIFlow workflow on .NET 10.
---

Use a .NET 10 application and reference `IxIFlow`. Activity libraries can depend on `IxIFlow.Abstractions` alone. Add `IxIFlow.Sqlite`, `IxIFlow.SqlServer` or `IxIFlow.PostgreSql` for durable storage; add `IxIFlow.Authoring` for document serialization and the installed activity catalog.

## Run a quote

The sample's [Quote.cs](../samples/IxIFlow.Samples/Quote.cs) supplies `QuoteData` and `AddFee`. The activity has a required customer, an in-out amount and a typed default fee of 2.50. With the sample types available, this complete async-method body runs a quote:

<!-- snippet: tests/IxIFlow.Samples.Tests/ReadmeExamples.cs -->
```csharp
var services = new ServiceCollection();
services.AddLogging();
services.AddIxIFlow();
await using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IWorkflowClient>();

var definition = Workflow.Create<QuoteData>("QuickStart")
    .Step<AddFee>(step => step
        .Input(activity => activity.Customer).From(ctx => ctx.WorkflowData.Customer)
        .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
        .Output(activity => activity.Amount).To(ctx => ctx.WorkflowData.Amount))
    .Build();
WorkflowDefinitionValidator.Validate(definition);

var result = await client.RunAsync(definition, new QuoteData());
if (!result.IsSuccess)
    throw new InvalidOperationException(result.ErrorMessage);
Console.WriteLine(((QuoteData)result.WorkflowData!).Amount); // 12.50
```

Imports are `IxIFlow.Builders`, `IxIFlow.Core`, `IxIFlow.Extensions`, `IxIFlow.Samples` and `Microsoft.Extensions.DependencyInjection`, plus .NET implicit usings. This exact example compiles and runs in [ReadmeExamples.cs](../tests/IxIFlow.Samples.Tests/ReadmeExamples.cs).

The input mappings set the activity's saved inputs. The output mapping writes its resulting amount to workflow data. Read data from the result: recovery or a parked retry can reconstruct it as another object. See the [activity SDK](activity-sdk.md) for direction markers and metadata.

## Run the repository samples

From the repository root, with packages restored:

```sh
dotnet run --project samples/IxIFlow.Samples --no-restore
dotnet run --project samples/IxIFlow.Samples --no-restore -- --yaml
dotnet test tests/IxIFlow.Samples.Tests --no-restore
```

Both console modes run the quote. The [sample map](../samples/README.md) covers loops, parallel work, waits, children, recovery, sagas and templates.

## Understand the result

`RunAsync` returns when the instance is idle, including `Suspended` at a wait or `NeedsResolution` for an operator. `IsSuccess` means `Status == Success`; a suspended result still has useful saved data and an instance ID.

`StartAsync` saves a durable start and returns its ID without waiting for execution. `AwaitAsync(id)` waits for idle; `AwaitAsync(id, WorkflowAwaitCondition.Finished)` waits for a terminal result. Terminal results remain readable for the configured [retention period](retention-and-tombstones.md).

A cancellation token interrupts the current call. Use `CancelAsync` with a `CancellationReason` to request durable workflow cancellation, including compensation and `Finally` cleanup. The token on a queued start only cancels waiting for acceptance; the host owns that execution.

## Add a long-running host

The example above uses the default memory store. Use a generic host to start background recovery, outbox delivery and retention, and to drain execution at shutdown. [Hosting](coordinator-and-hosts.md) has the compiled setup; [stores](stores.md) explains persistence and deployment constraints.

Parameterless activities can be constructed by the executor. Register activities with constructor dependencies in DI. Every process that might resume saved work must load its matching workflow definitions and activity code.

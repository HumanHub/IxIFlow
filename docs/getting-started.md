---
title: Getting started
description: Run a first IxIFlow workflow on .NET 10.
---

Use a .NET 10 application and reference `IxIFlow`. Activity libraries can depend on `IxIFlow.Abstractions` alone. Add `IxIFlow.Sqlite`, `IxIFlow.SqlServer` or `IxIFlow.PostgreSql` for durable storage; add `IxIFlow.Authoring` for document serialization and publication. Installed activity registration is part of the core engine.

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

if (result.Status != WorkflowExecutionStatus.Success)
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

Both console modes run the quote. The [sample map](../samples/README.md) also covers loops, parallel work, waits, children, recovery, sagas, published code activities, bundled templates and inline workflow calls. Publication samples are exercised by the sample tests.

## Understand the result

`RunAsync` returns when the instance is idle, including `Suspended` at a wait or `NeedsResolution` for an operator. A successful result has `Status == WorkflowExecutionStatus.Success`; a suspended result still has useful saved data and an instance ID.

`StartAsync` saves a durable start and normally returns its ID without waiting for execution. Inside a running host slot it executes inline and returns only when that nested run ends. `AwaitAsync(id)` waits for idle; `AwaitAsync(id, WorkflowAwaitCondition.Finished)` waits for a terminal result. Terminal results remain readable for the configured [retention period](retention-and-tombstones.md).

`AwaitAsync` considers a parked `Scheduled` result idle; `RunAsync` waits through that park. `RecoverAsync` may return `Running` for a busy owner or `Scheduled` for a parked retry. A `Faulted` recovery/release response can mean the operation was refused, rather than that the saved instance faulted; read its error and query the instance status.

`WorkflowOptions.ExecutionTimeout` must be positive and persists an execution deadline that unwinds cleanup before `TimedOut`. Configure retries on saga steps or sagas.

A cancellation token interrupts the current call. Use `CancelAsync` with a `CancellationReason` to request durable workflow cancellation, including compensation and `Finally` cleanup. The token on a queued start only cancels waiting for acceptance; the host owns that execution.

## Add a long-running host

The example above uses the default memory store. Use a generic host to start background recovery, outbox delivery and retention, and to drain execution at shutdown. [Hosting](coordinator-and-hosts.md) has the compiled setup; [stores](stores.md) explains persistence and deployment constraints.

Parameterless activities can be constructed by the executor. Register activities with constructor dependencies in DI. For code-defined workflows, every host that might resume saved work must load the matching definitions and activity code. Published workflows use the verified bundle-loading path below.

## Publish and consume artifacts

Use `WorkflowPublisher` with the selected catalog and artifact store. Save a kind-keyed draft, validate it with `ValidateAsync`, then publish its complete `Id` with `PublishAsync`. The result has one matching `Workflow`, `Package` or `Template` arm. Validation performs a full no-write build; only publication consumes the saved draft.

[PublishedActivities.cs](../samples/IxIFlow.Samples/PublishedActivities.cs) publishes the existing double activity as `acme.double` package `1.0.0`, then publishes a YAML consumer. Its fluent consumer uses the package's generated `IxIFlow.Activities.Acme.Double` type after a package reference. [PublishedArtifacts.cs](../samples/IxIFlow.Samples/PublishedArtifacts.cs) publishes a Liquid template, embeds it with `renderTemplate`, and publishes `use`/`useType` calls. The sample tests run the published workflows in a fresh bundle host and compile the fluent consumer against the nupkg DLL.

Configure `UseWorkflowBundles(cacheDirectory)` on each execution host. The host fetches verified digest-pinned artifacts and does not resolve NuGet dependencies at runtime. A published workflow's data types belong to its bundle context; use serialized data or declared shared contracts when crossing that boundary.

For a publishing web host, add `IxIFlow.AspNetCore`, register `AddWorkflowPublishing` and `ICatalogQueryScope`, and map `MapWorkflowPublishing`. The [HTTP guide](publishing-http.md) covers the seven required policies, draft ETags, upload and the authenticated NuGet read subset.

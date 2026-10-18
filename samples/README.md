# Samples

| Feature | Sample | YAML file | C# file |
| --- | --- | --- | --- |
| Activities, inputs, outputs, in-out mapping, typed defaults, sequences, conditions | Quote | [quote.workflow.yaml](IxIFlow.Samples/Definitions/quote.workflow.yaml) | [Quote.cs](IxIFlow.Samples/Quote.cs) |
| Sequential ForEach, item handles and ordered results | Batch; running totals and nested lists in recipes | [batch.workflow.yaml](IxIFlow.Samples/Definitions/batch.workflow.yaml) | [Batch.cs](IxIFlow.Samples/Batch.cs), [Lists.cs](IxIFlow.Recipes/Lists.cs) |
| Parallel ForEach, concurrency cap, all/any/conditional joins and results | Batch | [batch-parallel.workflow.yaml](IxIFlow.Samples/Definitions/batch-parallel.workflow.yaml), [batch-any.workflow.yaml](IxIFlow.Samples/Definitions/batch-any.workflow.yaml), [batch-conditional.workflow.yaml](IxIFlow.Samples/Definitions/batch-conditional.workflow.yaml) | [Batch.cs](IxIFlow.Samples/Batch.cs), [ApproverLists.cs](IxIFlow.Recipes/ApproverLists.cs) |
| Correlated waits, event mapping and pinned child workflows | Review | [review.workflow.yaml](IxIFlow.Samples/Definitions/review.workflow.yaml), [review-child.workflow.yaml](IxIFlow.Samples/Definitions/review-child.workflow.yaml) | [Review.cs](IxIFlow.Samples/Review.cs) |
| Reusable activity/step references and expression assets | Reusable quote | [reusable.workflow.yaml](IxIFlow.Samples/Definitions/reusable.workflow.yaml) | [Reusable.cs](IxIFlow.Samples/Reusable.cs) |
| WhileDo and DoWhile | Restock | [restock.workflow.yaml](IxIFlow.Samples/Definitions/restock.workflow.yaml) | [Restock.cs](IxIFlow.Samples/Restock.cs), [Approvals.cs](IxIFlow.Recipes/Approvals.cs) |
| Parallel branches, joins, isolated data and combiners | Approval recipes; combined quotes | [parallel-quotes.workflow.yaml](IxIFlow.Samples/Definitions/parallel-quotes.workflow.yaml), [parallel-quotes-any.workflow.yaml](IxIFlow.Samples/Definitions/parallel-quotes-any.workflow.yaml), [parallel-quotes-conditional.workflow.yaml](IxIFlow.Samples/Definitions/parallel-quotes-conditional.workflow.yaml) | [ParallelQuotes.cs](IxIFlow.Samples/ParallelQuotes.cs), [Approvals.cs](IxIFlow.Recipes/Approvals.cs) |
| Try/Catch/Finally with typed faults | Failed quote | [failed-quote.workflow.yaml](IxIFlow.Samples/Definitions/failed-quote.workflow.yaml) | [FailedQuote.cs](IxIFlow.Samples/FailedQuote.cs) |
| Sagas, reverse compensation and retry | Payment recipe; activity attempt history | [retry-quote.workflow.yaml](IxIFlow.Samples/Definitions/retry-quote.workflow.yaml), [saga-payment.workflow.yaml](IxIFlow.Samples/Definitions/saga-payment.workflow.yaml) | [Payments.cs](IxIFlow.Recipes/Payments.cs), [RetryQuote.cs](IxIFlow.Samples/RetryQuote.cs) |
| Delays | Reminder | [reminder.workflow.yaml](IxIFlow.Samples/Definitions/reminder.workflow.yaml) | [Reminder.cs](IxIFlow.Samples/Reminder.cs) |
| Workflow clock | Reusable quote | [reusable.workflow.yaml](IxIFlow.Samples/Definitions/reusable.workflow.yaml), using a C# expression asset | [Reusable.cs](IxIFlow.Samples/Reusable.cs) |
| Requests, reply tokens, subjects, deadlines and timeout paths | Wait and reply recipes | [approval.workflow.yaml](IxIFlow.Samples/Definitions/approval.workflow.yaml), [saga-payment.workflow.yaml](IxIFlow.Samples/Definitions/saga-payment.workflow.yaml) | [Approvals.cs](IxIFlow.Recipes/Approvals.cs), [Payments.cs](IxIFlow.Recipes/Payments.cs), [Shipping.cs](IxIFlow.Recipes/Shipping.cs) |
| Cancellation and child request routing | Fraud and child approval recipes | C# only: cancellation and runtime routing use application client APIs | [FraudAndChildren.cs](IxIFlow.Recipes/FraudAndChildren.cs) |
| Memory, SQLite, SQL Server and PostgreSQL store selection; hosting | Store setup | C# only: application DI configuration | [Hosting.cs](IxIFlow.Samples/Hosting.cs) |
| Definition validation and activity catalog metadata | Quote validation | C# only: application validation/catalog API | [Contracts.cs](IxIFlow.Samples/Contracts.cs) |
| Telemetry, committed trace and snapshot inspection | Quote observations | C# only: application diagnostics API | [Observability.cs](IxIFlow.Samples/Observability.cs) |
| Discoverable wait defaults, retention/tombstones, custom completion publishers, webhook registration and host lifecycle | Documentation examples | C# only: application/client configuration | [DocumentationExamples.cs](IxIFlow.Samples/DocumentationExamples.cs), [DocumentationTests.cs](../tests/IxIFlow.Samples.Tests/DocumentationTests.cs) |
| Versioned Liquid templates and message sending | Quote message | C# sample: template publication and sender registration use application APIs | [QuoteMessage.cs](IxIFlow.Samples/QuoteMessage.cs) |
| Recoverable activity and operator resolution | Reservation | [reservation.workflow.yaml](IxIFlow.Samples/Definitions/reservation.workflow.yaml); operator client calls are C# only | [Recovery.cs](IxIFlow.Samples/Recovery.cs) |

## Run the samples

With the .NET 10 SDK, run from the repository root:

```sh
dotnet run --project samples/IxIFlow.Samples
dotnet run --project samples/IxIFlow.Samples -- --yaml
dotnet test tests/IxIFlow.Samples.Tests
```

The console entry point runs Quote in fluent C# or generated YAML form and prints Ada's amount and outcome: 12.50 and ready (number formatting follows the console culture). Other examples expose a `Definition()` method; [SampleTests.cs](../tests/IxIFlow.Samples.Tests/SampleTests.cs) and [YamlSampleTests.cs](../tests/IxIFlow.Samples.Tests/YamlSampleTests.cs) show their setup and execution. Review also registers its pinned child in `IWorkflowVersionRegistry` before starting the parent.

`IxIFlow.Samples` references the generator as an analyzer and imports the engine's package props because it uses project references. Its eighteen `*.workflow.yaml` files are compiler additional files. A NuGet consumer of `IxIFlow` gets that glob, generator, analyzers and code fixes from the package automatically. The same package ships the workflow JSON Schema for editors; see [editor support](../src/IxIFlow.Generators/README.md#editor-support).

## Activity contracts

Use explicit contracts on activities intended for YAML or the activity catalog. [AddFee in Quote.cs](IxIFlow.Samples/Quote.cs) shows all three markers: `[ActivityInput]`, `[ActivityOutput]` and `[ActivityInputOutput]`. Once any property uses a direction marker, only marked properties belong to the activity contract. Inputs need public setters, outputs public getters, and in-out properties both.

The same example uses `[Required]` on Customer, `[DefaultValue(typeof(decimal), "2.50")]` on Fee, `[Display]` on input and output properties, and `[UIHint("text")]` on the customer input. The engine applies the typed fee default before saving the activity's inputs; an explicit mapping takes precedence. Required inputs need a mapping or declared default when built and a non-null, nonempty value before execution. Amount maps both into and out of AddFee in both YAML and C#.

Pure fluent activities can use the public-setter/input and public-getter/output convention. The existing recipes intentionally keep that convention. Their application dependencies, such as mail, payment and shipping services, are registered by the host. `Contracts.Catalog()` shows installed-assembly catalog registration and `Contracts.ValidateQuote()` calls the public definition validator; `Build()` already applies the shared build rules.

## Execution details

- Batch uses expression-preserving `itemsExpression`, `valueExpression` and `completeWhenExpression` arguments to match generated fingerprints. Its parallel versions keep one result per item in input order, recording completed, cancelled and not-started items. The existing approval-list recipes demonstrate a cap with genuinely pending requests; their tests assert the next request is sent only when a slot opens.
- ParallelQuotes covers all, any and conditional joins. It starts each branch from an isolated copy and uses `Sum<decimal>` to merge fee changes. Both general branches and parallel `forEach` have YAML twins.
- RetryQuote retries a single saga step immediately and reads `context.Attempt.Number`, previous errors/notes and attempt IDs. SagaPaymentRecipe and its YAML twin refund a payment when stock reservation fails, then retry with a new reply token. These are different retry scopes.
- Reusable reads the workflow clock in both versions; its YAML expression asset uses the generated selector context, `ctx.UtcNow`. Reminder parks for one hour and maps `ctx.UtcNow` after resumption. `FakeTimeProvider` drives the delay and approval deadline tests. RetryQuote uses zero backoff; the payment saga retry uses the one-second default, driven by the fake clock.
- QuoteMessage publishes immutable Liquid variants, renders the parent French variant for fr-CA and sends through `ITemplateMessageSender`. The application sender must deduplicate by message ID. Template storage defaults to memory; workflow bundle/artifact persistence is future work.
- Recovery checks an external reservation by its saved invocation ID. If the provider cannot establish the outcome, an operator supplies an observed result through the client. [RecoveryTests.cs](../tests/IxIFlow.Samples.Tests/RecoveryTests.cs) reuses the existing memory-store fault injector to interrupt the commit after the effect and verifies both automatic recovery and operator resolution.
- Hosting compiles all store registrations. Tests execute each workflow on memory and smoke-test SQLite. SQL Server/PostgreSQL registration tests construct the providers; database execution needs their integration environments.
- Observability attaches standard .NET activity and meter listeners, then reads committed trace history and a snapshot through the client. Configure an OpenTelemetry SDK/exporter in the application if it needs external telemetry delivery.

[SampleTests.cs](../tests/IxIFlow.Samples.Tests/SampleTests.cs) and [YamlSampleTests.cs](../tests/IxIFlow.Samples.Tests/YamlSampleTests.cs) compare saved definition fingerprints and serialized results for YAML/fluent pairs, including empty lists, loop boundaries, each parallel join, typed faults, retries, delays, replies and timeouts. Retry checks verify runtime IDs before comparing their roles across instances. [RecipeTests.cs](../tests/IxIFlow.Samples.Tests/RecipeTests.cs) runs every existing recipe on memory with fake time. [ReadmeExamples.cs](../tests/IxIFlow.Samples.Tests/ReadmeExamples.cs) compiles and executes the root README quick start. The existing conformance suite supplies additional store and failure scenarios.

The [root roadmap](../README.md#status-and-roadmap) marks the remaining 1.0 work. Workflow bundle publishing, the coordinator, process executors, debugger controls, management endpoints and incoming HTTP triggers remain planned. Outgoing signed webhooks, public outbox publishers, instance retention and tombstones are implemented; see the [documentation index](../docs/index.md).

## Demo activities

`IxIFlow.DemoActivities` contains sample activities. The classes define inputs, outputs, display metadata, and designer IDs in C#. A host can register the installed assembly with `ActivityPackageRegistry.AddAssembly` using its package ID and version.

The file activities execute without additional services. `QueryCustomerActivity` requires the host to register `IDemoConnectionFactory`; the workflow contains a connection reference, not credentials.

The activities expose designer IDs as catalog metadata. A consuming editor provides the matching controls; the engine loads and runs the .NET activity assembly.

## Wait and reply recipes

`IxIFlow.Recipes` contains workflows for the common ways a workflow waits for someone or something. Each recipe is a definition with its activities and data, written as an application would write it; the conformance tests run every recipe against each store. Register the activities with `services.AddWaitRecipes()` and provide the services they use.

| Recipe | Shows |
| --- | --- |
| `OneApproverRecipe` | A request with a reply token, a subject the answer must match, a timeout path, and a second click refused as already answered |
| `PaymentRecipe` | Requesting now and waiting later: a provider callback that arrives during the fraud check is kept, "pending" is refused while the wait stays open, and a callback received before the deadline beats it |
| `SagaPaymentRecipe` | A request inside a saga: a later failure refunds the payment and closes its wait, so a later callback is refused as cancelled (one that arrived before the failure is not taken by the wait, and its token is then refused as already answered), and the saga's retry pays again with a new token |
| `AnyApproverRecipe` | Any one of several approvers: a rejection keeps waiting, the first approval wins, and the other approver's late click is refused as cancelled |
| `EveryApproverRecipe` | Every approver must answer, in any order |
| `ApprovalPerLineRecipe` | An approval per loop iteration: an earlier line's token can never approve a later line |
| `ConfirmShipmentRecipe` | A human task with no request, found through `GetOpenWaitsAsync` and answered by its listed token |
| `DeliveryRecipe` | A notification by key: taken by a wait that is open when it arrives, refused if sent earlier |
| `FraudRecipe` | One workflow cancelling another, whose late answers are refused as cancelled |
| `ChildApprovalRecipe` | A child workflow sends the request; the answer goes to the child and the parent continues |
| `PriceLinesRecipe` | A step per order line with `ForEach`: each line sees the running total of the lines before it, and each line's total is kept in line order with `.Result(...)` and `Into` |
| `InvoiceCustomersRecipe` | Nested lists: a `ForEach` over customers holds a `ForEach` over their lines, and the inner body reads both items |
| `AnyApproverFromListRecipe` | Any one of a list of approvers with a parallel `ForEach`: a rejection keeps waiting, the first approval wins, the others are cancelled, and the decisions show who approved, rejected or was cancelled; an empty list or all rejections take the rejection path |
| `EveryApproverFromListRecipe` | Every approver from a list, decisions kept in approver order; with a cap only that many requests are out at once; if asking one fails, the others are cancelled and nothing is stored |
| `ChargeCustomersRecipe` | A child workflow per customer, no more than the cap at once, results in customer order |

An answer that must not reach the wrong wait goes through `IWorkflowClient.ReplyAsync(token, answer)`. The token comes from `IActivityContext.Reply` in a request activity, or from `GetOpenWaitsAsync` for a wait without a request.

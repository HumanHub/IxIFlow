# Workflow operations over HTTP

`IxIFlow.AspNetCore` maps an application's management API onto `IWorkflowOperations` and `IWorkflowClient`. Register the engine, ASP.NET Core authentication, named authorization policies and an application implementation of `IInstanceQueryScope`, then map the routes:

<!-- snippet: samples/IxIFlow.Samples/OperationsHttpExamples.cs#operations-http-map -->
```csharp
app.MapWorkflowOperations("/ixiflow/ops", options =>
{
    options.ReadPolicy = "workflow-read";
    options.ReplyPolicy = "workflow-reply";
    options.InspectPolicy = "workflow-inspect";
    options.WritePolicy = "workflow-write";
    options.OutboxPolicy = "workflow-outbox";
    options.ActForPolicy = "workflow-act-for"; // Optional delegation.
    options.MutationWait = TimeSpan.FromSeconds(10);
});
```

All five route policies are mandatory. Missing or unknown policies, a missing query scope, and conflicting routes fail startup. The hosting, authorization and problem-details infrastructure is shared with workflow triggers. Applications configure their own authentication middleware; these endpoints require an authenticated principal and return problem details for authentication or authorization failures.

Each single-instance route first authorizes its policy against the HTTP context and then calls `IAuthorizationService.AuthorizeAsync(user, summary, policy)` with the persisted `InstanceSummary`. Policy handlers should support both resources: the context check establishes general access, and the summary check establishes access to this workflow, correlation or parent. An instance denial returns `403`. An unknown instance returns `404`; a retained removal tombstone returns `410`.

Search authorization runs before the store query. Register an `IInstanceQueryScope` with both overloads: one narrows `InstanceQuery` and one narrows `WaitQuery` for the principal. Preserve the caller's filters and paging while intersecting them with the application's permitted scope. Never broaden a supplied filter. The wait overload can constrain `InstanceIds`; an empty collection permits no instances. Even an unrestricted administrative application must register an explicit implementation. Results are never post-filtered, so authorized pages and continuation cursors remain intact. Global outbox access is governed by the outbox policy.

**Inspection also requires reply authorization.** Both trace and snapshot routes enforce the inspect policy and the reply policy, including both checks against the instance summary. This remains necessary until signed reply tokens are implemented. Applications must treat trace and snapshot readers as capable of replying. This package does not sign reply tokens.

All paths below are relative to the mapped prefix.

| Method and path | Policy | Response |
| --- | --- | --- |
| `GET /instances` | read | `InstancePage`: `items`, `nextCursor` |
| `GET /instances/{id}` | read | `InstanceSummary` |
| `GET /instances/{id}/waits` | read | Array of `PendingWait`, without wait IDs or tokens |
| `GET /instances/{id}/waits?tokens=true` | reply | `WorkflowWaitPage`: `items`, `nextAfter`; items include reply tokens |
| `GET /waits` | reply | Scoped `WorkflowWaitPage` |
| `GET /instances/{id}/pending-activities` | read | Array of `PendingActivityInfo` |
| `GET /instances/{id}/trace` | inspect and reply | `WorkflowTracePage`: `items`, `nextAfter` |
| `GET /instances/{id}/snapshot` | inspect and reply | `WorkflowSnapshotResponse` |
| `POST /instances/{id}/resolutions` | write | Public operation status, or accepted status location |
| `POST /instances/{id}/release` | write | Public operation status, or accepted status location |
| `POST /instances/{id}/cancel` | write | Public operation status, or accepted status location |
| `POST /instances/{id}/terminate` | write | Public operation status, or accepted status location |
| `GET /outbox/publishers` | outbox | Array of `OutboxPublisherState` |
| `POST /outbox/publishers/{name}/enable` | outbox | Updated `OutboxPublisherState` |
| `DELETE /outbox/publishers/{name}` | outbox | Durable unregistered `OutboxPublisherState` |
| `GET /outbox/deliveries` | outbox | `WorkflowDeliveryPage`: `items`, `nextCursor` |
| `POST /outbox/deliveries/{eventId}/{publisher}/retry` | outbox | Event ID and publisher acknowledgement |

Instance searches combine `status` (comma-separated names), `stop`, `workflow`, `version`, `correlationId`, `parentInstanceId`, `createdFrom`, `createdTo`, `endedFrom`, `endedTo`, `after` and `limit`. Times are UTC; From is inclusive and To is exclusive. Creation order is descending, with instance ID ascending for ties. Reuse an opaque cursor with the same filters and principal scope. Page sizes are 1–1,000, default 100.

Token wait listings accept `after` and `limit`; global listings additionally accept `key` and `eventType`. `nextAfter` is the last returned wait ID when another row exists. Only plain event waits are discoverable. The read-policy wait listing returns all eligible waits for that instance without truncation or capabilities. Trace uses a numeric sequence `after`, default zero, and `limit`; continue with `nextAfter` until it is null. An exact final trace page can require one empty follow-up request.

Delivery searches combine `publisher`, `status`, `instanceId`, `neverAttempted`, `after` and `limit`. Delivery order is event ID then publisher name. Publisher and delivery listings contain no lease credentials. Delivery event metadata excludes the event body. Retention can remove given-up deliveries; retry is available only while they remain retained. Retry requires a given-up delivery and an enabled, registered publisher. Enable retries only deliveries given up because the publisher was disabled. Unregister is durable across host restarts and enable cannot reverse it.

Resolution bodies contain `invocationId`, `kind` (`Completed` or `Failed`), `outputs`, `note` and optional `errorMessage`. Completed outputs are JSON values converted to the declared activity property types and validated before acceptance. Failed resolutions use `errorMessage`, falling back to `note`. Release has no required body. Cancel and terminate require `{ "reasonCode": "operator-request", "description": "Optional explanation" }`.

Mutations take their actor from `User.Identity.Name`. An `X-IxIFlow-Actor` header is refused with `403` unless `ActForPolicy` is configured and succeeds. Multiple or blank actor headers are invalid requests. Mutations and outbox access require a nonblank actor name; supplied actor fields in JSON do not override the principal.

Workflow decisions are committed before acceptance. The HTTP completion wait defaults to ten seconds and returns `200` with `{ instanceId, status, errorMessage }` when it finishes in that time. Otherwise it returns `202` with `Location` pointing to `GET /instances/{id}`. Time spent accepting the decision counts toward the wait; a decision still queued is never acknowledged as accepted. Acceptance itself can exceed the configured wait if the store or slot is busy. The request-aborted token can stop HTTP waiting but never reaches the host run. A disconnect does not withdraw a committed decision. Cancellation can acknowledge its durable request while the running owner is still unwinding; poll the instance status to observe its final state. Outbox mutations finish their atomic store operation before returning `200`; they have no subsequent workflow run to wait for.

All responses use public projections. Workflow execution results, saved workflow data, snapshot values/frame state, trace metadata, stack traces and delivery lease tokens are excluded. Reply-capable wait listings deliberately include reply tokens under the reply policy. Enum values in responses use the application's ASP.NET Core JSON configuration; request enum names are case-insensitive.

Errors use `application/problem+json`: malformed or invalid requests return `400`, invalid state or busy operations `409`, removed instances `410`, unknown resources `404`, and unavailable workflow artifacts `503`. Exception messages and stacks are not copied into problem details.

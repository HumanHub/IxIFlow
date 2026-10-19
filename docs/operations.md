# Workflow operations

`AddIxIFlow` registers `IWorkflowOperations` alongside `IWorkflowClient`. Operations run through the host's scoped read pipeline. Applications must authorize access to this interface.

The same interface provides [outbox administration](outbox-and-publishers.md#administer-retained-deliveries): delivery listing with filters and cursors, retry, publisher listing, enable and durable unregister. Each call takes an actor. Mutations record that actor and the store time, and delivery listings contain no lease credentials.

<!-- snippet: samples/IxIFlow.Samples/OperationsExamples.cs#search -->
```csharp
var page = await operations.SearchAsync(new InstanceQuery(
    Statuses: [WorkflowStatus.Suspended, WorkflowStatus.NeedsResolution],
    WorkflowName: "Order", WorkflowVersion: 2, Limit: 100));
var waits = await operations.GetWaitsAsync(page.Items.Select(row => row.InstanceId).ToArray());
var next = await operations.SearchAsync(new InstanceQuery(
    Statuses: [WorkflowStatus.Suspended, WorkflowStatus.NeedsResolution],
    WorkflowName: "Order", WorkflowVersion: 2, Limit: 100, After: page.NextCursor));
```

Operations search pages contain at most 1,000 instances, so a whole page fits one waits query. Search combines status, stop kind, workflow name/version, correlation, parent and creation/end ranges. Ranges include `From` and exclude `To`. Results sort by creation time descending and instance ID ascending using ordinal ordering. A cursor belongs to its filters; the page size can change. Rows inserted ahead of the cursor do not shift subsequent pages. Search is a live view: changes to filter membership and deletion can change later results. It does not reserve a historical snapshot.

The search reads materialized row columns only. It reads neither execution state nor workflow data or checkpoint parts. Creation order has a global index, with additional ordered indexes led by status, workflow, version, stop kind, correlation and parent. End-time ranges have a separate index. Arbitrary combinations can require residual filtering or sorting; they do not each have a dedicated covering index.

`WorkflowStopKind` distinguishes activity resolution, resource limits, quarantine and verification. An explicit stop takes precedence over activity resolution. Quarantine exposes the crash count captured when it stopped, even after the live lease count resets. Release and termination clear the stop projection in the same checkpoint commit.

`GetAsync` uses the same row projection and returns null for an absent row. Summaries contain no workflow data, execution state or stack trace. `GetWaitsAsync` accepts up to 1,000 instance IDs and returns every discoverable plain event wait for those instances in one store query, including pages with more than 100 waits. Reply waits, child waits, delays and execution timers are excluded. The result contains no wait IDs or reply tokens. Trace paging takes an exclusive sequence and a limit; the last returned sequence is the next page's boundary. Trace wait references are diagnostic references.

## Termination

<!-- snippet: samples/IxIFlow.Samples/OperationsExamples.cs#terminate -->
```csharp
var stopped = await operations.TerminateAsync(instanceId, "operator-stop",
    "Stopped after investigation", actor: "alice");
```

Termination runs no workflow handlers, compensation or finally blocks. It durably upgrades graceful cancellation, including cancellation already acknowledged by a running owner. Each accepted termination has a store-assigned command version; repeating a pending command keeps the first termination's version and actor. The terminal commit acknowledges that version. The task returns after the parent is durably `Terminated`. Cancelling the caller's wait does not withdraw an accepted command.

For an idle instance, a single fenced checkpoint commit preserves saved workflow data, clears its stop projection, closes open waits and pending inbox events, appends `WorkflowTerminated` with the actor, publishes completion, notifies its waiting parent and records termination commands for every nonterminal child. This path needs neither a registered definition nor deserialization of user data. Child commands are durable in that same commit; recovery applies them recursively.

Detached-child policy: hard termination stops detached descendants too. Detachment releases the parent's wait, but does not exempt the descendant from administrative subtree termination. Completed children retain their results.

For a running instance, accepting termination blocks further ordinary commits, including new child starts. The owner acknowledges the hard stop at its next commit boundary. If completion commits first, termination is refused and reports that it lost. If termination is accepted first, the ordinary completion commit returns `TerminationRequested`, and the host commits termination instead. The interrupted run reports `Terminated`.

A pending terminate prevents lease renewal. If an activity or cancellation cleanup hangs, another host can claim the instance at lease expiry and commit the hard stop without loading its workflow definition. The accepted command remains runnable for recovery. External work already started by an activity can continue; its expired epoch prevents later workflow commits.

Unhandled saga `ThenTerminate` also produces `WorkflowExecutionStatus.Terminated`, and finished awaits recognize that status. A surrounding catch can still handle the saga exception. Failed compensation and exhausted retries retain failure semantics.

Databases created by an earlier schema build must be recreated. The new row columns and search indexes are declared when each database is created.

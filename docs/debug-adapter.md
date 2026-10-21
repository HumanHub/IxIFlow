# Attach the workflow debugger from VS Code

The application hosts the adapter in its own process. Enable engine debugging and register a local endpoint and an application authorization policy:

<!-- snippet: samples/IxIFlow.Samples/DebugAdapterExamples.cs#register -->
```csharp
services.AddIxIFlow(o =>
{
    o.Debugging = true;
}).AddDebugging(o =>
{
    o.NamedPipe("ixiflow-debug");
    o.Authorize = (request, cancellationToken) =>
        policy.CanDebugAsync(request.Actor, request.InstanceId,
            request.Operation, cancellationToken);
});
```

`policy` and its `ValueTask<bool>` implementation belong to the application. Registration requires an authorizer. Authorize inspection, pause and step, edits, breakpoint changes, and launches separately. The authorizer runs again on each operation, including inspection refreshes. Pipe connections are restricted by the OS to the application's current user. The authenticated OS user supplies the audit actor.

For TCP, replace `NamedPipe` with `o.Tcp(IPAddress.Loopback, 4711, sessionToken, "developer-session")`. Supply a randomly generated token with at least 32 bytes, for example `Convert.ToHexString(RandomNumberGenerator.GetBytes(32))`. The application sets the actor associated with that token. A request's `actor` field has no effect. TCP accepts loopback addresses only and checks the token with a constant-time digest comparison.

Open this repository's `extensions/ixiflow-debug` directory in VS Code. Use **Run > Start Debugging** with VS Code's **Extension** development configuration, or run `code --extensionDevelopmentPath=<absolute path to extensions/ixiflow-debug>`. The extension uses its JavaScript source directly and needs no dependency installation or compilation. No marketplace package is required.

In the extension development window, open your workflow checkout and add this configuration to `.vscode/launch.json`:

```json
{
  "version": "0.2.0",
  "configurations": [{
    "type": "ixiflow",
    "request": "attach",
    "name": "Attach to workflow",
    "instanceId": "the-saved-instance-id",
    "endpoint": "pipe:ixiflow-debug",
    "sourceRoots": { "C:/build/workflows": "${workspaceFolder}/workflows" },
    "continueOnDisconnect": false
  }]
}
```

For TCP, use `"endpoint": "tcp://127.0.0.1:4711"` and `"sessionToken": "${input:ixiflowToken}"`. Add an input alongside `configurations`:

```json
"inputs": [{ "id": "ixiflowToken", "type": "promptString", "description": "Application debugger token", "password": true }]
```

The descriptor factory connects to the application pipe or port. VS Code's `debugServer` setting can also point directly at the TCP port; attach still requires `sessionToken`. An application using multiple debugger identities should give each identity its own endpoint and token.

Breakpoints can address C# or generated YAML lines. Verified breakpoints return the actual executable line. `sourceRoots` maps absolute build directories to your local checkout; the adapter uses the inverse mapping for source breakpoints. Frames use the step's source file and line. Frames without a location are shown subtly. Source requests are restricted to files in the instance's step table. Register `IWorkflowDebugSourceProvider` to read sources from your published workflow bundles when their build paths are absent locally. An unavailable source returns an explicit error.

Graph source lines resolve to real picks: a node or state declaration maps to `Enter`, a flowchart edge to `Edge`, and a state trigger declaration to `Trigger` after exit. A separately authored case line maps to its case `Edge` before the action. Generated `Decision` and `Arm` share the node or state location; activity and listener `Fire` share their trigger or state location. Same-line ties use catalog order, so the declaration breakpoint selects `Enter` or `Trigger`. Use function breakpoints with catalog paths or generated author IDs to select another pick on that line. A finished graph frame retains its last pick's location. Both one-based and zero-based DAP clients use the same mapping.

Branches appear as threads, with scope, loop and item information in their names. Select a thread before stepping. Stepping into child workflows is unavailable; children continue independently. The variables view exposes the branch's workflow data, item index, current attempt metadata, and waits identified by safe references. Previous outputs and actual item values remain unavailable until classified inspection is integrated.

Graph frames retain their inspection names, for example `approval / pending (visit 2)`, `approval / pending / exit`, `approval / pending / on 1 / case 0`, and `approval / pending / on 2 / run`. Flowchart edge frames end in `/ to 0`; listener frames end in `/ listen`. Visits count entries across that graph activation. Run and listener threads report their parent state's visit. Stack order groups exit, transition and action frames above their state, and edge frames above their flowchart node.

Selecting a graph frame, or an ordinary frame inside it, adds a read-only **Graph** scope. It exposes `id` (the graph's catalog ID), `node` (the current node or state key), `phase`, `visit`, `armOccurrence`, `via` (the incoming edge), and `pending`. A selected choice contains `Via`, `Trigger`, `Target` and `Cause`; a null `Target` ends the graph, while null `pending` means no choice has been selected. Event and activity payloads are omitted. For nested graphs the selected frame determines the scope. Recorded graph metadata is also available in replay without a retained data checkpoint; older traces may lack these added fields.

Watch expressions accept paths such as `data.Value`, `WorkflowData.Customer.Name`, `attempt[0].Host`, `waits[0].WaitRef`, `graph.phase`, `graph.visit`, and `graph.pending.Target`. They never execute code. Edits accept JSON values and apply only to paused workflow data. Refresh variables after editing or moving execution: handles from an older revision or stop are refused.

Use **Step Back** on a paused or finished workflow to enter read-only replay, or attach with `"replay": true`. Reverse continue moves the replay cursor to a configured catalog breakpoint or the beginning of retained history. Forward stepping and continue then move only that cursor. Replay matches breakpoint addresses without re-executing conditions. Historical values are available only at an explicitly retained matching checkpoint. Truncated history is identified as incomplete. Disconnect and attach again to return to live execution.

Timers and deadlines always keep running while paused. Due deadlines are handled when execution continues, and events arriving after a wait deadline are rejected. Disconnect leaves the instance paused by default. `continueOnDisconnect` requires authorization and resumes it; termination requests never terminate the application or workflow.

A launch configuration uses `request: "launch"`, `workflow`, `version`, JSON `input`, and optional `stopOnEntry`. It starts a definition already registered in the application. The initial hold is saved before dispatching workflow code. The application authorizes the generated instance ID before launch. This extension does not launch an application executable.

---
title: YAML authoring
description: Every workflow document construct, strict validation and editor schema.
---

YAML has a form for every fluent workflow construct. The source generator translates documents into the fluent API; generated and handwritten definitions use the same build rules and executor. Workflow names are required, and identity is the declared `(name, version)`. The parsed document model lowers to the same executable structure as fluent C#, so equivalent definitions have equal `StructureHash` values. Whitespace, comments, mapping key order, descriptions, display names and step labels do not affect the hash. Changed executable structure or expression shape does. Delegate bodies and captured values are excluded; bump the version when behavior changes. Checkpoint contract 1 (`ixiflow-1`) refuses instances saved by earlier builds without migration.

The `IxIFlow` package automatically includes `*.workflow.yaml` and `*.workflow.yml` as compiler additional files. A file such as `reminder.workflow.yaml` generates `<RootNamespace>.Generated.Workflow_reminder_workflow.Build()`. Punctuation in filenames becomes underscores; use distinct filenames. Project-reference applications need analyzer references and package props explicitly, as in [the samples project](../samples/IxIFlow.Samples/IxIFlow.Samples.csproj).

## A complete document

This is [reminder.workflow.yaml](../samples/IxIFlow.Samples/Definitions/reminder.workflow.yaml). It is compiled by the sample project and compared with [Reminder.cs](../samples/IxIFlow.Samples/Reminder.cs) using fake time in [YamlSampleTests.cs](../tests/IxIFlow.Samples.Tests/YamlSampleTests.cs).

<!-- snippet: samples/IxIFlow.Samples/Definitions/reminder.workflow.yaml -->
```yaml
# Park for an hour, then stamp the reminder with the workflow clock.
workflow:
  name: Reminder
  dataType: IxIFlow.Samples.ReminderData
code:
  now:
    language: csharp-expression
    source: ctx.UtcNow
definitions:
  - kind: delay
    duration: '01:00:00'
  - kind: activity
    activity: samples.stamp-time
    input:
      - target: At
        from: { kind: function, function: now }
    output:
      - source: At
        to: { path: workflow.SentAt }
```

## Document structure and reuse

| Root member | Meaning |
| --- | --- |
| `schemaVersion` | Document contract version; defaults to `"1.0"` |
| `workflow` | Required `name` and `dataType`; positive `version` defaults to 1; optional `displayName`, `description`, `tags`, `requiresScopes` |
| `definitions` | Ordered executable steps |
| `activities` | Named local activity templates with `activity`, optional `activityVersion`, `defaults`, `input`, `output` |
| `steps` | Named local step templates: `steps` and optional `parameters` |
| `code` | Named expression assets with `language: csharp-expression` and `source` |
| `imports` | Catalog/workflow dependencies; any nonempty catalog or workflow import list fails the source-generator build with `IXIYAML001` at `imports`, because the generator does not resolve imports |
| `$schema` | Optional editor association, ignored by readers |

Activity keys come from `[WorkflowActivity]` metadata or fully qualified CLR types. An explicit activity version must match the resolved contract. Step-template parameters declare optional `type`, `required` and scalar `default`; `stepRef.arguments` overrides defaults. Local parameter types are string, bool, int, long, double and decimal. Missing required arguments fail validation.

[reusable.workflow.yaml](../samples/IxIFlow.Samples/Definitions/reusable.workflow.yaml) demonstrates both reference forms, defaults, parameters and expression assets. Reference expansion prefixes template IDs with the reference ID.

## Every step kind

All steps accept `id`; `name` is accepted only in workflow-scope bodies. A name in a catch, saga or saga-handler body fails validation with `SCOPE002`. IDs are unique; omitted IDs derive from structural positions. A step name changes presentation only; it does not change the structure hash. IDs and source coordinates are excluded too, except when an identifier supplies an executable reference or wait key.

| `kind` | Members and behavior |
| --- | --- |
| `activity` | `activity`, optional `activityVersion`, `defaults`, `input`, `output`; within a saga, `compensation` and step `onError` |
| `activityRef` | `ref` selects a local activity template |
| `stepRef` | `ref` selects a local step template; `arguments` supplies named expressions |
| `scope` | Scope `activity`, optional `activityVersion`, `defaults`, `input`, `output`, `retry`, `threading`, and body `steps` |
| `sequence` | Ordered nested `steps` |
| `if` | Boolean `condition`, `then`, optional `else` |
| `whileDo` | `condition` tested before each pass through `steps` |
| `doWhile` | `steps` run before testing `condition` |
| `parallel` | `branches`, each containing `steps`; `join: all\|any\|conditionally`, optional `combine`; `condition` is required for `conditionally` and rejected for other joins (`PAR001`) |
| `forEach` | `items`, optional `as` alias (default `item`), body `steps`, optional trailing `result` expression, `into` and `parallel` configuration |
| `result` | `value` ends a result-producing ForEach body path, including timeout paths leaving it |
| `try` | Body `steps`, ordered `catches`, optional `finally`; catches may specify `exception`, `faultType`, `id`, `steps` |
| `saga` | Saga `steps` and ordered `onError` handlers |
| `switch` | Typed `value` selector, optional `valueType`, ordered `cases` with label `value` and `steps`, optional `default` steps |
| `delay` | Positive invariant TimeSpan `duration`; preserves previous-step value |
| `request` | Activity members plus required `key` and optional `subject`; opens a request reply slot |
| `waitFor` | Required `key` and `event`; plain event wait or the paired request's reply wait |
| `suspend` | Required `event`, optional `reason` key (step ID fallback) |
| `use`, `useType` | Inline a published catalog workflow or a registered workflow type with validated `input` and `output` mappings |
| `renderTemplate` | Constant `template` reference, typed `model`, optional `culture` and output mappings |
| `invokeWorkflow` | `workflow: { name, version? }`, `input`, `output`, ordered `onError`, optional `cancelWithParent` (true by default); publishing resolves catalog versions and contracts; local source generation requires an explicit version of another document in the compilation |
| `invokeWorkflowType` | `workflowType`, optional `dataType` fallback, mappings and `cancelWithParent`; the generator reads the class's `[Workflow("Name", Version = n)]` identity; register the class with `services.RegisterWorkflow<TWorkflow>()` before building the service provider |

The schema describes possible nodes; shared builder rules also enforce where constructs are legal. Saga bodies accept activities/references, requests, waits and switches. Saga handlers accept ordinary activities/references, `waitFor` and switches; catch bodies retain a lexical fault binding through their nested callbacks. Timeout paths and result-producing lists retain the fluent API's scope restrictions. Use `name` for a step label; descriptions belong to the workflow.

Plain and request waits accept `resumeCondition`, `input`, `output`, `defaultPayload`, positive `timeout` and `onTimeout` steps. A default payload is suggested discovery data, not an automatic reply. `timeout` and a nonempty `onTimeout` must be supplied together; the timeout path exits the containing body. `defaultPayload` accepts scalar, list or object literals shaped for the event type. When a wait opens, its pure `input` mappings read the workflow data and previous step, overlay `defaultPayload` (or a new event instance), and save the result as the suggested payload. Recovery and replay use the saved suggestion. Only the submitted event supplies the answer; suggestions never fill in an answer. For request replies, the token is reserved before the request runs; the suggestion is computed when execution reaches `waitFor`, using the completed previous step. Pair requests and reply waits by key in the same body; a plain `suspend` is not a request pairing.

Wait inputs require public setters and compatible property types. When constructing a suggestion, `[Required]` event members must be mapped or supplied by the default payload, and cannot be null or empty at runtime. Without a default payload, the event type needs a public parameterless constructor. A failed mapping fails the wait step.

General parallel joins use provisional merged workflow data for `condition`. A `combine` entry declares `property: { path: workflow.Member }`, `propertyType` and `combiner`. The [parallel twins](../samples/IxIFlow.Samples/Definitions/parallel-quotes.workflow.yaml) show isolated data and sum merging.

ForEach `parallel` accepts `maxConcurrency`, `join`, `combine` and, for a conditional join, `resultType` and `condition`. That condition sees `workflow` and `results`. The body alias names the typed current item; nested bodies retain enclosing aliases. `into` targets a workflow `List<ItemResult<T>>`. See [batch.workflow.yaml](../samples/IxIFlow.Samples/Definitions/batch.workflow.yaml) and [batch-parallel.workflow.yaml](../samples/IxIFlow.Samples/Definitions/batch-parallel.workflow.yaml).

## Saga settings

An activity/request's `compensation` is an ordered list. Entries specify `activity`, `input`, `output`, and optionally `replyType` or `previousActivity` for the corresponding fluent overload. Reply compensation can read `reply` and `answered`; ordinary compensation reads `currentStep`. The previous-compensation overload supports `previousStepInput` and `previousCompensationInput`.

Activity-level `onError` entries accept optional `exception`, required `then: retry|terminate|ignore`, and optional `retry`. Saga-level handlers accept `exception`, `faultType`, `id`, handler `steps`, `compensate: all|none` or `compensateUpTo`, followed by `then: continue|terminate|retry`. Compensation runs before the handler steps. `ignoreCompensationErrors` applies to continuing. Omitted continuation rethrows after any selected undo.

Retry objects mirror `RetryPolicy`: `maximumAttempts`, `initialInterval`, `maximumInterval`, `backoffCoefficient`. Omission uses the fluent count-based default; supplied objects use the policy's property defaults. See [retry-quote.workflow.yaml](../samples/IxIFlow.Samples/Definitions/retry-quote.workflow.yaml), [saga-payment.workflow.yaml](../samples/IxIFlow.Samples/Definitions/saga-payment.workflow.yaml) and [sagas](sagas.md).

## Flowcharts and state machines

`kind: flowchart` requires `start` and keyed `nodes`. A node has optional `name`, `steps`, `switch` and ordered `transitions`. Each edge has `to` and either a `when` expression or a `case` under `switch`. A case accepts a scalar literal or a build-time expression, such as `{kind: function, function: readyLabel}` referencing a code asset whose source is `MyMode.Ready`, `1.25m` or `1L`. Typed expressions preserve enum, decimal and boxed numeric labels; labels cannot read execution context. A single unguarded edge is unconditional; an unguarded last edge after guards or cases is the fallback. To preserve an explicitly chosen fluent edge flag, `default: true` selects `Otherwise` even for a single edge; `default: false` selects `Always` after guards. This flag cannot accompany `when` or `case`, and switch fallbacks require the default form. `to: end` or `to: null` completes the graph. With no matching edge and no fallback, the flowchart completes. Keys are ordinal, unique, nonempty, contain no `/`, and cannot be `end`. Changing keys or edge order changes structure identity; changing display names, source locations or node/state declaration order does not.

<!-- snippet: samples/IxIFlow.Samples/Definitions/stock-flowchart.yaml -->
```yaml
workflow: {name: FulfilOrder, dataType: IxIFlow.Samples.Flowcharts+Order}
definitions:
  - kind: flowchart
    id: fulfilment
    start: check
    nodes:
      - key: check
        name: Check stock
        steps: [{kind: activity, activity: IxIFlow.Samples.Flowcharts+CheckStock}]
        transitions:
          - when: {path: previousStep.InStock}
            to: ship
          - to: backorder
      - key: ship
        steps: [{kind: activity, activity: IxIFlow.Samples.Flowcharts+Ship}]
      - key: backorder
        steps: [{kind: delay, duration: '1.00:00:00'}]
        transitions: [{to: check}]
```

`kind: stateMachine` requires `start` and keyed `states`. A state has optional `name`, `entry`, `exit`, `transitions` and `final`. An explicit `exit: []` creates an executable exit scope and affects structure identity; omitting `exit` creates none. Final states have entry only and cannot declare even an empty exit. Each transition has one trigger form:

| Shape | Trigger |
| --- | --- |
| `on: key`, `event: Type` | Keyed event |
| `reply: requestName`, `event: Type` | Reply to one request directly in this state's entry |
| `after: '00:05:00'` or `after: {path: previousStep.Timeout}` | Positive duration, literal or pure TimeSpan expression |
| `activity: Type` | One code activity, with optional `activityVersion` |
| No trigger member | Immediate guard arbitration |

Use direct `to` with optional `when` and action `steps`, or `cases` with ordered `when`/`to`/`steps` alternatives. An unguarded case is last; reply triggers require one. An unconditional immediate transition is last and cannot accompany waiting triggers. A non-final state with only guarded immediate transitions cannot wait and is rejected. A timer whose cases all fail starts a fresh duration when the state re-arms. Absolute timer dates are not supported.

Event and reply triggers accept `matches`, `defaultPayload`, `input` and `output`. Inputs read workflow data and the graph's incoming previous value; matches reads `event` and workflow data; outputs read the accepted value. Activity triggers accept input/output mappings, and guards and actions read the completed activity. Each activity trigger creates a `run` branch ending in `Fire`; a mixed state also creates one `listen` branch ending in its own `Fire`. These are generated catalog scopes, not authored YAML members. Composed durable-step and workflow-call triggers remain unspecified.

See the compiled [approval YAML](../samples/IxIFlow.Samples/Definitions/approval-state-machine.yaml) and [activity race YAML](../samples/IxIFlow.Samples/Definitions/activity-trigger-state-machine.yaml), with their [fluent twins](fluent-api.md#native-state-machines). These explicitly named `.yaml` examples compile in the generator parity project. Default automatic discovery uses `.workflow.yaml`/`.workflow.yml`; other names use `IxIFlowWorkflow` items as described in the generator guide. Nested CLR types use `+` in YAML catalog lookup names.

A direct timeout path leaving a flowchart node or a non-final state entry must end with `{kind: transition, to: destination}`. Explicit transitions cannot appear in exit/action bodies or arbitrary nested scopes. There is no trigger `timeout`; use an `after` transition. Invalid graph forms produce located `GRAPH0xx` validator diagnostics; the build reports them through `IXIYAML001`.

## Invocation error policies

Both `invokeWorkflow` and `invokeWorkflowType` accept the same ordered step `onError` list as saga activities and requests. Omitted `exception` matches `Exception`. Each entry selects `then: retry`, `terminate` or `ignore`; only retry accepts a `retry` object.

<!-- snippet: samples/IxIFlow.Samples/Definitions/invocation-retries.workflow.yaml -->
```yaml
workflow: {name: RetryLookup, dataType: IxIFlow.Samples.LookupRequest}
definitions:
  - kind: invokeWorkflow
    id: lookup
    workflow: {name: RetryLookupChild, version: 1}
    input: [{target: Query, from: {path: workflow.Query}}]
    output: [{source: Response, to: {path: workflow.Response}}]
    onError:
      - exception: System.InvalidOperationException
        then: retry
        retry: {maximumAttempts: 2, initialInterval: '00:00:05', maximumInterval: '00:00:30', backoffCoefficient: 2}
```

`maximumAttempts` counts additional retries. The first matching policy wins, including when its budget is exhausted. Each retry evaluates fresh mappings and required values against the pinned contract and starts a different child; a pre-start input failure has no child. Ignore leaves outputs untouched and makes `previousStep` null. Step terminate propagates the failure to enclosing handlers. Successful-child output mapping failures never start replacements. Reachable retry delays over 36,500 days fail positioned validation; the exact limit is accepted. See [invocation retries](fluent-api.md) for failure boundaries and cancellation, and the [sample twin](../samples/IxIFlow.Samples/Definitions/invocation-retries.workflow.yaml).

## Mappings and expressions

An input has `target` and `from`. An output has `source` and `to: { path: workflow.Member }`. The step kind determines the source: activity outputs, the event on a wait, or the child workflow data on an invoke. Explicit activity input mappings override YAML defaults and attribute defaults. Wait input mappings override the initial suggested payload.

| Expression `kind` | Shape |
| --- | --- |
| `constant` | `value`: locally translated literals are scalar |
| `path` | `path`: a C# expression rewritten against the current context; `kind` may be omitted when `path` is present |
| `binary` | `operator`, `left`, `right`; operators are `eq`, `ne`, `and`, `or`, `gt`, `ge`, `lt`, `le`, `add`, `subtract`, `multiply`, `divide` |
| `function` | `function` names a `code` asset with language `csharp-expression`, taking no arguments. Put a pure C# method call inside the asset source |

Use CLR member names such as `workflow.Amount`, `previousStep.Receipt`, `event.Approved` and handler `fault.Message`. The generator also resolves camel-case workflow aliases. Expression assets may use `ctx.UtcNow`; other context names depend on scope. These are trusted C# expressions and must obey [purity rules](execution-model.md#checkpoints-and-commits).

YAML fractional numbers become doubles. Use an expression asset such as a C# decimal literal when decimal typing is needed. Activity attribute defaults retain their declared CLR types. Quote version-like strings and numeric-looking text to keep editor and reader types aligned.

## Strict reader and schema

Member names and kind values are exact and case-sensitive. Unknown/miscased members and invalid shapes fail the build with located `IXIYAML001` diagnostics. Duplicate keys, including case-insensitive duplicates, are rejected. Missing/blank required members are diagnosed by shared document validation; generated C# type errors also point back to YAML through source mappings. Free-form keys under named templates, parameters, defaults and arguments stay open.

The package includes [workflow.schema.json](../src/IxIFlow/schemas/workflow.schema.json). Package props expose its installed path through `IxIFlowWorkflowSchema`; associate that path with workflow files in your YAML editor. Use the schema shipped with your package version. The schema supports completion and structural checks; C# resolution and builder validation remain authoritative.

`WorkflowYamlSerializer` and the Authoring `WorkflowJsonSerializer` share the document model and strict member rules. JSON document reading/writing and translation from a `WorkflowDocument` exist; automatic build discovery is for YAML/YML files.

## Workflow contracts and catalog calls

The workflow header accepts `input: [Member, ...]` and `output: [Member, ...]`. These lists merge with `[WorkflowInput]`, `[WorkflowOutput]` and `[WorkflowInputOutput]` on the data type. Without markers or lists, public setters are inputs and public getters are outputs. With declarations, only declared members are exposed. An explicit empty list exposes nothing on that side, including marked members. Omit the lists to keep convention. Unknown members report WF005; declarations that contradict markers or public accessors report WF006.

Publishing resolves every `invokeWorkflow` and `imports.workflows` entry through the catalog. An omitted version selects the latest Published version. An explicit version may name a Published or Deprecated version. Missing, Draft and Retired versions are refused. Canonical bundle YAML records explicit step versions and `imports.workflows` entries such as `Orders@3`.

<!-- snippet: samples/IxIFlow.Samples/PublishedCheckout.cs#checkout-yaml -->
```yaml
workflow:
  name: Checkout
  version: 1
  dataType: CheckoutData
  input: [OrderId]
  output: [Receipt]
imports:
  workflows: [Orders]
definitions:
  - kind: invokeWorkflow
    workflow: {name: Orders}
    input:
      - target: OrderId
        from: {path: workflow.OrderId}
    output:
      - source: Receipt
        to: {path: workflow.Receipt}
```

Generated calls embed the catalog's immutable workflow contract. Mapping a member outside its inputs is MAP004; reading a member outside its outputs is MAP005; omitting a required input without a default is MAP006. These diagnostics point to the mapping in YAML. STEP011 requires a resolved workflow contract and pinned version. Public accessors and compatible types are also checked by the fluent builder and compiler.

The caller bundle includes the union of its packages and its callees' digest-pinned package closures, preserving reference assemblies and runtime assets per RID. This makes package-owned callee data types available to compile and load the caller. Callee workflow assemblies are resolved independently when the child runs. An incompatible callee engine version, checkpoint contract version, target framework or shared assembly profile reports PUB009 and requires republishing the callee. Conflicting package versions, digests or asset selections report PUB010, as do incompatible private DTO assemblies or their private dependencies (including different bytes with the same assembly identity).

Call input precedence is explicit mapping, then `[DefaultValue]`, then constructor or initializer. A required null or empty input fails the invocation step as `InvalidInput`, catchable with `try`/`catches`. Invocation steps support ordered typed and general `onError` policies, including retry and ignore; see [invocation retry policies](fluent-api.md#faults-and-recovery). Client root starts validate required inputs without applying attribute defaults. Invoked workflows run as independent child instances; their contracts are checked again at child start. Published and Deprecated callers, and unfinished parent instances of retired callers, continue to prevent retirement of their child dependencies.

## Publishing and boundaries

`WorkflowPublisher` in `IxIFlow.Authoring` publishes YAML drafts with pinned package and workflow imports as verified bundles. It validates, translates and compiles the draft, then calls the generated `Build()` in the publishing process, inside a collectible scratch load context on a dedicated background thread. `WorkflowPublisherOptions.BuildTimeout` defaults to 30 seconds. If the build exceeds that deadline, publication fails with a build-timeout diagnostic and uploads nothing. The thread cannot be stopped: a warning reports that it and its scratch load context are abandoned until the process restarts. Supply an `ILogger<WorkflowPublisher>` to capture these warnings. A completed build is validated before its scratch context is unloaded and its bundle is uploaded and published against the draft revision.

See the [generator guide](../src/IxIFlow.Generators/README.md) for custom filenames, opt-out, MSBuild metadata and explicit project references. Host bundle loading and activity-package dependency resolution and pinning are implemented (see the README's publishing section). YAML inline calls, YAML code activities and template documents use the same publisher. The [HTTP surface](publishing-http.md) adds policies, scope, preconditions, preview, upload and downloads. Application hosting, store choice, publishers and client calls use C# configuration.

<!-- snippet: samples/IxIFlow.Samples/Definitions/switch-routing.workflow.yaml -->
```yaml
workflow: {name: SwitchRouting, dataType: IxIFlow.Samples.RoutingData}
definitions:
  - kind: switch
    id: route
    valueType: string
    value: {path: workflow.Route}
    cases:
      - value: {kind: constant, value: ship}
        steps:
          - kind: activity
            activity: samples.label
            input: [{target: Value, from: {kind: constant, value: shipping}}]
            output: [{source: Value, to: {path: workflow.Result}}]
      - value: {kind: constant, value: hold}
        steps:
          - kind: activity
            activity: samples.label
            input: [{target: Value, from: {kind: constant, value: on hold}}]
            output: [{source: Value, to: {path: workflow.Result}}]
    default:
      - kind: activity
        activity: samples.label
        input: [{target: Value, from: {kind: constant, value: manual review}}]
        output: [{source: Value, to: {path: workflow.Result}}]
```

Case labels are constant expressions or named `csharp-expression` assets evaluated at definition build, with no workflow, previous, item or fault context. Use `valueType` for ambiguous null/base-type selectors; typed enum, decimal and supported struct labels can use code assets. An explicitly empty `default: []` is retained. An absent default means no-match continues normally. Build reports `SWITCH001` for a missing selector, `SWITCH002` for an incompatible label/type, `SWITCH003` for duplicate cases/defaults and `SWITCH004` for unsupported or incomplete label state. Existing purity diagnostics apply to selectors and label expressions. See [Switch bodies](fluent-api.md#switch-bodies) for equality, label-state and recovery rules.

## Assigning data and iteration positions

<!-- snippet: samples/IxIFlow.Samples/Definitions/assign-totals.workflow.yaml -->
```yaml
workflow: {name: AssignTotals, dataType: IxIFlow.Samples.AssignmentTotalsData}
definitions:
  - kind: assign
    id: total
    to: {path: workflow.Total}
    value:
      kind: binary
      operator: multiply
      left: {path: workflow.Price}
      right: {path: workflow.Quantity}
```

`to` names one writable serialized workflow location. `value` uses the existing constant, path, binary or function expression forms. Nested properties, structs, array/list elements and dictionary entries use the same typed location compiler as fluent C#. Invalid roots and unwritable storage report `ASSIGN001`/`ASSIGN002`; incompatible values report `ASSIGN003` during definition validation, or a positioned C# diagnostic during compilation.

Within `forEach` with `as: line`, write `workflow.Lines[index(line)].Total` to select the current position. `index(line)` also works in C# expression assets. It accepts exactly one lexical item alias; `index` is reserved for this intrinsic. `line.Index` continues to read the item's own property. Outer aliases remain available inside nested iterations; an alias cannot be reused in the same lexical scope.

## Raising and propagating business errors

Use `Throw<PaymentException>(ctx => new PaymentException("Declined") { Code = ctx.WorkflowData.Code })`
wherever a body accepts steps. The type must be a concrete business exception; engine, cancellation and saga
control exceptions are rejected. Constructor arguments and initializers may read workflow data, the incoming
previous value, items and a lexically enclosing Catch fault. The factory retains the previous value and creates
no activity attempt. A null or different concrete result, or a factory failure, follows ordinary Catch and saga routing.

`Rethrow()` takes no arguments and binds to the nearest lexically enclosing Try Catch, including nested bodies
and parallel branches. Build rejects it at the root, in a sibling Finally or in a saga error handler without such
a Catch. Invoked and inline workflow definitions start their own lexical scope. Rethrow preserves the original
error type, message, declared properties and workflow location across recovery; changing the Catch DTO does not
change the error. Diagnostic CLR stacks are not a durable contract.

In YAML use `kind: throw`, `exception: PaymentException`, and `value: {kind: function, function: declined}`.
The referenced C# expression asset constructs the exception and may include property initializers. Use
`{kind: rethrow}` inside a Catch, without exception or value arguments.

Factories obey expression purity rules: no external clocks, environment, random values, I/O, waits or mutations
of existing state. Initializing fresh objects is allowed. Build checks direct expression operations and recognized
mutators; the analyzer also follows source-visible helpers with bounded alias analysis. Opaque helpers, virtual
implementations and user setters remain the author's responsibility. Debugger stepping stops at an eligible
handler or cleanup pick, completes propagation, or saves a Blocked pause at a held join. Continue releases that
restriction. Privileged inspection exposes the classified, read-only Catch DTO, never the CLR exception.

The [fluent example](../samples/IxIFlow.Samples/ThrowAndRethrow.cs) and its
[YAML definition](../samples/IxIFlow.Samples/Definitions/throw-and-rethrow.workflow.yaml) form the same executable workflow.

## Cancellation cleanup

A `cancellationScope` requires both `steps` and `onCancelled`; either may be an explicit empty list.
The body and handler are separate request/reply scopes. A handler may wait durably and resumes in
its saved phase after a host restart.

<!-- snippet: samples/IxIFlow.Samples/Definitions/cancellation-approval.workflow.yaml -->
```yaml
workflow: {name: CancellationApproval, dataType: IxIFlow.Samples.CancellationApprovalData}
definitions:
  - kind: cancellationScope
    steps:
      - {kind: waitFor, key: approval, event: IxIFlow.Samples.CancellationApprovalSignal}
      - {kind: assign, to: {path: workflow.State}, value: {kind: constant, value: approved}}
    onCancelled:
      - kind: activity
        activity: samples.label
        input: [{target: Value, from: {kind: constant, value: withdrawn}}]
        output: [{source: Value, to: {path: workflow.State}}]
```

The handler runs for unfinished cancelled work, including a business fault travelling to an outer
Catch or saga error handler. That handler receives the original fault after intervening cleanup.
Completed work, internally handled faults and fully unhandled faults do not activate the handler.
Finally, compensation and cancellation handlers run in nesting order. Administrative termination
runs no workflow cleanup. See the [matching runnable examples](../samples/IxIFlow.Samples/CancellationApproval.cs).

## Scope activities

`kind: scope` selects an `IScopeActivity<TValue>` implementation, using its catalog key or CLR type. Its `steps` run with that value in force. Activity versions, copied inputs, defaults and output mappings use the ordinary activity rules. `retry` accepts `maximumAttempts`, `initialInterval`, `maximumInterval` and `backoffCoefficient` and applies to initial opens and re-opens. `threading` accepts `anyThread` or `threadAffine`; omission uses the implementation's minimum, and an override cannot weaken it. Concurrency comes from the implementation.

<!-- snippet: samples/IxIFlow.Samples/Definitions/scoped-workbook.yaml -->
```yaml
workflow: {name: ScopedWorkbook, dataType: IxIFlow.Samples.ScopeData}
definitions:
  - kind: scope
    activity: IxIFlow.Samples.ExcelScope
    threading: threadAffine
    input: [{target: WorkbookRef, from: {path: workflow.Reference}}]
    steps:
      - {kind: activity, activity: IxIFlow.Samples.WriteCells}
      - {kind: activity, activity: IxIFlow.Samples.SaveWorkbook}
```

`SaveWorkbook` is an explicit body activity. Close releases the session; it is not an implicit save. [Connection](../samples/IxIFlow.Samples/Definitions/scoped-connection.yaml) and [mailbox](../samples/IxIFlow.Samples/Definitions/scoped-mailbox.yaml) twins demonstrate named references resolved by host services. All three documents are compiled and run by [ScopeSampleTests](../tests/IxIFlow.Samples.Tests/ScopeSampleTests.cs).

Scopes are accepted in workflow and catch bodies, including nested graphs and branches. Saga bodies and handlers refuse scope declarations. Naming an ordinary activity with `scope` is `SCOPE003`; naming a scope activity with `activity` is `SCOPE004`; weakening threading is `SCOPE005`. Unknown members and mode values are rejected. `[RequiresScope<TValue>]` errors map to the authored YAML step.

`workflow.requiresScopes` lists the C# value types a reusable workflow needs. `use` and `useType` inherit the caller's scope and threading. `invokeWorkflow` and `invokeWorkflowType` create independent children that inherit neither; root starts also refuse required scopes. Registration and publishing validate resolved requirements; loading a forged bundle refuses it as an artifact mismatch. See [scope lifetimes](execution-model.md#scopes).

## Code activity documents

An activity document declares one activity. Its key is also the NuGet package ID. Integer activity version `1` becomes package version `1.0.0`; a workflow's `activityVersion` still selects the activity declaration version `"1"`.

<!-- snippet: samples/IxIFlow.Samples/Definitions/double.activity.yaml -->
```yaml
activity:
  key: acme.double
  version: 1
  name: Double
input:
  Value: { type: int, default: 3 }
output:
  Result: { type: int }
body: |
  Result = Value * 2;
```

The `input`, `output` and `inOut` dictionaries declare typed properties. Members support `required`, scalar `default`, `label`, `help` and `control` metadata. The generated class uses activity direction markers and .NET Required, DefaultValue, Display and UIHint attributes. A body contains C# statements and may await, use `context` and `cancellationToken`, or resolve services from `context.Services`. Optional `declarations` contains class members; `usings` supplies namespaces and `packages` pins dependencies. Code is trusted application code.

`WorkflowPublisher.ValidateAsync` compiles and checks without storing packages or artifacts. `PublishAsync` constructs the deterministic nupkg, verifies it, records the matching activity descriptor and closure, and consumes the exact saved draft. Its `Package` result contains the stored audit and receipt. Workflows import published packages; they do not define code activities within a workflow document.

<!-- snippet: samples/IxIFlow.Samples/PublishedActivities.cs#package-consumer-yaml -->
```yaml
workflow: {name: PublishedDouble, dataType: IxIFlow.Samples.PublishedNumber}
imports: {catalogs: [acme.double@1.0.0]}
definitions:
  - kind: activity
    activity: acme.double
    activityVersion: "1"
    input: [{target: Value, from: {path: workflow.Value}}]
    output: [{source: Result, to: {path: workflow.Value}}]
```

A fluent application adds a PackageReference to `acme.double` version `1.0.0` and uses `IxIFlow.Activities.Acme.Double` with ordinary typed mappings. [PublishedActivities](../samples/IxIFlow.Samples/PublishedActivities.cs) and its [sample test](../tests/IxIFlow.Samples.Tests/PublishedArtifactSampleTests.cs) compile that consumer against the published DLL and execute both forms. The publishing HTTP host exposes a protected NuGet read subset for external consumers.

## Template documents and bundled rendering

Template documents have a `template` header, required base `content`, and optional `cultures`. `template.model` names the declared C# object surface. Imported model packages and their complete closures are pinned. Liquid and `text`/`html` are supported; scalar, collection and dictionary root models are refused. Template versions have no lifecycle transition or collection operation.

<!-- snippet: samples/IxIFlow.Samples/PublishedArtifacts.cs#template-yaml -->
```yaml
template:
  key: samples.greeting
  version: 1
  engine: liquid
  format: text
  model: IxIFlow.Samples.GreetingModel
content: "Hello {{ Name }}"
cultures:
  fr: "Bonjour {{ Name }}"
```

<!-- snippet: samples/IxIFlow.Samples/PublishedArtifacts.cs#bundled-render-yaml -->
```yaml
workflow: {name: BundledGreeting, dataType: IxIFlow.Samples.GreetingData}
definitions:
  - kind: renderTemplate
    id: greeting
    template: samples.greeting
    model: {path: workflow.Model}
    culture: {kind: constant, value: fr-CA}
    output: [{source: Rendered, to: {path: workflow.Body}}]
```

Publishing resolves an omitted template version and model type, then writes their exact values into canonical workflow YAML. The bundle embeds the template content and model closure with a binding for each executable site. The template reference must be constant; workflow data may supply model and culture. A build-time generator has no publishing catalog, so it needs an explicit version and `modelType`.

A bundled render reads only its verified embedded content, including after suspension or publication of a newer live template. Each dispatch can render only its own bound references. Missing or changed pins fail as engine definition errors outside authored Catch and saga handling. `RenderTemplate<TModel>` remains one recoverable step; sending is a separate idempotent activity. See [durable templates](../src/IxIFlow.Templates/README.md).

Template preview performs the full Scratch pipeline and renders the JSON model without writes. HTTP preview has a separate PreviewPolicy; full validation uses ExecutePolicy. Both may execute constructors, getters, converters or authored code. Model/output bounds and BuildTimeout limit input, output and waiting; timeout does not forcibly stop managed code.

## Published inline calls

<!-- snippet: samples/IxIFlow.Samples/PublishedArtifacts.cs#inline-yaml -->
```yaml
workflow: {name: InlineDouble, dataType: IxIFlow.Samples.PublishedNumber}
imports: {workflows: [NumberBody@1]}
definitions:
  - kind: use
    id: published
    workflow: {name: NumberBody, version: 1}
    input: [{target: Value, from: {path: workflow.Value}}]
    output: [{source: Value, to: {path: workflow.Value}}]
  - kind: useType
    id: typed
    workflowType: IxIFlow.Samples.DoubleNumber
    input: [{target: Value, from: {path: workflow.Value}}]
    output: [{source: Value, to: {path: workflow.Value}}]
```

`use` selects an artifact-backed Published or explicitly Deprecated catalog version. A code-defined callee uses `useType` and must already be registered in the catalog; its public parameterless constructor builds the body during publication. The publisher verifies and copies the executable closure and canonical source into the caller. Input and output mappings follow the callee's immutable contract. Static demands merge into the caller and contradictory demands fail publication.

Inline bodies run in the caller's executor with their own checkpointed data frames and inherited scopes. They create no child instance. Independent `invokeWorkflow` calls retain child lifecycle and cancellation semantics. Inline pins continue from the caller's copied bytes after callee retirement; unfinished child callers still protect their callee. Private assembly or package conflicts fail publication. The [inline sample](../samples/IxIFlow.Samples/PublishedArtifacts.cs) publishes both forms and executes them from a fresh bundle host.

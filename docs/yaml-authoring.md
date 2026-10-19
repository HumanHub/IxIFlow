---
title: YAML authoring
description: Every workflow document construct, strict validation and editor schema.
---

YAML has a form for every fluent workflow construct. The source generator translates documents into the fluent API; generated and handwritten definitions use the same build rules and executor.

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
| `workflow` | Required `name` and `dataType`; positive `version` defaults to 1; optional `displayName`, `description`, `tags` |
| `definitions` | Ordered executable steps |
| `activities` | Named local activity templates with `activity`, optional `activityVersion`, `defaults`, `input`, `output` |
| `steps` | Named local step templates: `steps` and optional `parameters` |
| `code` | Named expression assets with `language: csharp-expression` and `source` |
| `imports` | Catalog/workflow dependencies; any nonempty catalog or workflow import list fails the source-generator build with `IXIYAML001` at `imports`, because the generator does not resolve imports |
| `$schema` | Optional editor association, ignored by readers |

Activity keys come from `[WorkflowActivity]` metadata or fully qualified CLR types. An explicit activity version must match the resolved contract. Step-template parameters declare optional `type`, `required` and scalar `default`; `stepRef.arguments` overrides defaults. Local parameter types are string, bool, int, long, double and decimal. Missing required arguments fail validation.

[reusable.workflow.yaml](../samples/IxIFlow.Samples/Definitions/reusable.workflow.yaml) demonstrates both reference forms, defaults, parameters and expression assets. Reference expansion prefixes template IDs with the reference ID.

## Every step kind

All steps accept `id`; `name` is accepted only in workflow-scope bodies. A name in a catch, saga or saga-handler body fails validation with `SCOPE002`. IDs are unique; omitted IDs derive from structural positions. A name changes the fluent step name and runtime fingerprint; IDs and source coordinates do not.

| `kind` | Members and behavior |
| --- | --- |
| `activity` | `activity`, optional `activityVersion`, `defaults`, `input`, `output`; within a saga, `compensation` and step `onError` |
| `activityRef` | `ref` selects a local activity template |
| `stepRef` | `ref` selects a local step template; `arguments` supplies named expressions |
| `sequence` | Ordered nested `steps` |
| `if` | Boolean `condition`, `then`, optional `else` |
| `whileDo` | `condition` tested before each pass through `steps` |
| `doWhile` | `steps` run before testing `condition` |
| `parallel` | `branches`, each containing `steps`; `join: all\|any\|conditionally`, optional `combine`; `condition` is required for `conditionally` and rejected for other joins (`PAR001`) |
| `forEach` | `items`, optional `as` alias (default `item`), body `steps`, optional trailing `result` expression, `into` and `parallel` configuration |
| `result` | `value` ends a result-producing ForEach body path, including timeout paths leaving it |
| `try` | Body `steps`, ordered `catches`, optional `finally`; catches may specify `exception`, `faultType`, `id`, `steps` |
| `saga` | Saga `steps` and ordered `onError` handlers |
| `outcome` | Saga `value` selector, ordered `branches` with label `value` and `steps`, optional `default` steps |
| `delay` | Positive invariant TimeSpan `duration`; preserves previous-step value |
| `request` | Activity members plus required `key` and optional `subject`; opens a request reply slot |
| `waitFor` | Required `key` and `event`; plain event wait or the paired request's reply wait |
| `suspend` | Required `event`, optional `reason` key (step ID fallback) |
| `invokeWorkflow` | `workflow: { name, version }`, `input`, `output`, optional `cancelWithParent` (true by default); the source generator requires an explicit `version` because it registers only `name@version` references |
| `invokeWorkflowType` | `workflowType`, optional `dataType` fallback, mappings and `cancelWithParent`; resolves an `IWorkflow<T>` class |

The schema describes possible nodes; shared builder rules also enforce where constructs are legal. Saga bodies accept activities/references, requests, waits and outcomes. Saga handlers accept ordinary activities/references and `waitFor`; catch bodies exclude requests, delays, results and outcomes. Timeout paths and result-producing lists retain the fluent API's scope restrictions. Use `name` for a step label; descriptions belong to the workflow.

Plain and request waits accept `resumeCondition`, `input`, `output`, `defaultPayload`, positive `timeout` and `onTimeout` steps. A default payload is suggested discovery data, not an automatic reply. `timeout` and a nonempty `onTimeout` must be supplied together; the timeout path exits the containing body. `defaultPayload` accepts scalar, list or object literals shaped for the event type. Wait `input` mappings are retained by the builder but are not applied to accepted events or used to construct default payloads; use `defaultPayload` and `output` for those purposes. Pair requests and reply waits by key in the same body; a plain `suspend` is not a request pairing.

General parallel joins use provisional merged workflow data for `condition`. A `combine` entry declares `property: { path: workflow.Member }`, `propertyType` and `combiner`. The [parallel twins](../samples/IxIFlow.Samples/Definitions/parallel-quotes.workflow.yaml) show isolated data and sum merging.

ForEach `parallel` accepts `maxConcurrency`, `join`, `combine` and, for a conditional join, `resultType` and `condition`. That condition sees `workflow` and `results`. The body alias names the typed current item; nested bodies retain enclosing aliases. `into` targets a workflow `List<ItemResult<T>>`. See [batch.workflow.yaml](../samples/IxIFlow.Samples/Definitions/batch.workflow.yaml) and [batch-parallel.workflow.yaml](../samples/IxIFlow.Samples/Definitions/batch-parallel.workflow.yaml).

## Saga settings

An activity/request's `compensation` is an ordered list. Entries specify `activity`, `input`, `output`, and optionally `replyType` or `previousActivity` for the corresponding fluent overload. Reply compensation can read `reply` and `answered`; ordinary compensation reads `currentStep`. The previous-compensation overload supports `previousStepInput` and `previousCompensationInput`.

Activity-level `onError` entries accept optional `exception`, required `then: retry|terminate|ignore`, and optional `retry`. Saga-level handlers accept `exception`, `faultType`, `id`, handler `steps`, `compensate: all|none` or `compensateUpTo`, followed by `then: continue|terminate|retry`. Compensation runs before the handler steps. `ignoreCompensationErrors` applies to continuing. Omitted continuation rethrows after any selected undo.

Retry objects mirror `RetryPolicy`: `maximumAttempts`, `initialInterval`, `maximumInterval`, `backoffCoefficient`. Omission uses the fluent count-based default; supplied objects use the policy's property defaults. See [retry-quote.workflow.yaml](../samples/IxIFlow.Samples/Definitions/retry-quote.workflow.yaml), [saga-payment.workflow.yaml](../samples/IxIFlow.Samples/Definitions/saga-payment.workflow.yaml) and [sagas](sagas.md).

## Mappings and expressions

An input has `target` and `from`. An output has `source`, optional `sourceContext: activity|event|workflow`, and `to: { path: workflow.Member }`. Explicit input mappings override YAML defaults and attribute defaults. `sourceContext` is accepted metadata; the step kind determines the object read (activity, event or child workflow data), and the translator does not switch that object based on `sourceContext`.

| Expression `kind` | Shape |
| --- | --- |
| `constant` | `value`: locally translated literals are scalar |
| `path` | `path`: a C# expression rewritten against the current context; `kind` may be omitted when `path` is present |
| `binary` | `operator`, `left`, `right`; operators are `eq`, `ne`, `and`, `or`, `gt`, `ge`, `lt`, `le`, `add`, `subtract`, `multiply`, `divide` |
| `function` | `function` names a local `csharp-expression` code asset; `arguments` must be empty. Put a pure C# method call inside the asset source |

Use CLR member names such as `workflow.Amount`, `previousStep.Receipt`, `event.Approved` and handler `fault.Message`. The generator also resolves camel-case workflow aliases. Expression assets may use `ctx.UtcNow`; other context names depend on scope. These are trusted C# expressions and must obey [purity rules](execution-model.md#checkpoints-and-commits).

YAML fractional numbers become doubles. Use an expression asset such as a C# decimal literal when decimal typing is needed. Activity attribute defaults retain their declared CLR types. Quote version-like strings and numeric-looking text to keep editor and reader types aligned.

## Strict reader and schema

Member names and kind values are exact and case-sensitive. Unknown/miscased members and invalid shapes fail the build with located `IXIYAML001` diagnostics. Duplicate keys, including case-insensitive duplicates, are rejected. Missing/blank required members are diagnosed by shared document validation; generated C# type errors also point back to YAML through source mappings. Free-form keys under named templates, parameters, defaults and arguments stay open.

The package includes [workflow.schema.json](../src/IxIFlow/schemas/workflow.schema.json). Package props expose its installed path through `IxIFlowWorkflowSchema`; associate that path with workflow files in your YAML editor. Use the schema shipped with your package version. The schema supports completion and structural checks; C# resolution and builder validation remain authoritative.

`WorkflowYamlSerializer` and the Authoring `WorkflowJsonSerializer` share the document model and strict member rules. JSON document reading/writing and translation from a `WorkflowDocument` exist; automatic build discovery is for YAML/YML files.

See the [generator guide](../src/IxIFlow.Generators/README.md) for custom filenames, opt-out, MSBuild metadata and explicit project references. Dependency resolution/pinning, published bundles, inline workflow calls and YAML-defined code activities remain planned. Application hosting, store choice, publishers and client calls use C# configuration.

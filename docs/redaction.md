---
title: Marking and redaction
description: Classify workflow values and control their inspection projection.
---

IxIFlow erases classified members when it projects workflow values for inspection. Mark properties on workflow data, events, activity outputs used as `Previous`, and typed activity notes. `[Sensitive]` ships in `IxIFlow.Abstractions`, under `IxIFlow.Core`:

<!-- snippet: samples/IxIFlow.Samples/DocumentationExamples.cs#sensitive-member -->
```csharp
public sealed class PaymentNote
{
    [Sensitive] public string CardNumber { get; set; } = "";
    public string Outcome { get; set; } = "";
}
```

The equivalent contract and its projections are exercised in [RedactionTests.cs](../tests/IxIFlow.Tests/ExecutionTests/RedactionTests.cs). Applications with a taxonomy can use their own subclass of `Microsoft.Extensions.Compliance.Classification.DataClassificationAttribute` instead. Multiple classifications on a member are combined into one `DataClassificationSet` for the provider.

Classification belongs to the member. Copying a classified input to an unmarked output, computed property or note does not carry its classification along. Mark each destination that inspection could expose. A typed note means an object assigned to `context.Attempt.Note`; its properties carry the annotations.

The workflow build collects classified paths using the data contract walk, including nested objects, list elements, events and `Previous` types. Notes have an `object` contract, so their actual type is collected when first projected. Runtime types are inspected too.

## Inspection behavior

`InspectionRedactionPolicy.Default` defines inspection behavior. Its typed-value methods use the fail-closed `RedactingJson` projection for typed attempt notes before they enter committed traces, snapshot `Data`, and `GetOpenWaitsAsync` default payloads. Trace copies returned in run results and through `GetTraceAsync` contain the same projected notes. Snapshots expose execution positions and projected data.

A classified string, number or object becomes a JSON string. A classified object is redacted as one complete serialized value. Classified lists retain their list shape and redact each element. An unclassified object retains its structure and projects its nested members. Erasure produces an empty string, including for classified nulls. Public members remain visible.

No redactor configuration is required for erasure. If a provider is registered, IxIFlow resolves `Microsoft.Extensions.Compliance.Redaction.IRedactorProvider` from the current operation scope. A missing provider or redactor, a provider/redactor exception, or a projection failure erases the affected value. Opaque custom-converter shapes, raw JSON, extension-data members and dictionaries are erased because this projection cannot reliably inspect their member classifications. They are erased even when a pseudonymizing redactor is configured.

Engine mapping logs carry property names and execution identity, without the mapped values. This also applies to unclassified values. Activities control their own logs and should configure their logging pipeline accordingly.

Redaction does not change checkpoints, saved notes, wait defaults, inbox payloads, or the data returned to the application by run and await calls. Execution and retries continue to use the original values. Store access must have its own access control.

## Opt into HMAC pseudonyms

Add `Microsoft.Extensions.Compliance.Redaction` to the application and call `services.AddRedaction`. Inside its builder callback, call `SetHmacRedactor`, supplying `HmacRedactorOptions.Key`, `KeyId`, and the classifications to pseudonymize. Use `SensitiveAttribute.Classification` for `[Sensitive]`; register the corresponding sets for application taxonomy attributes. Unconfigured classifications use the package's erasing fallback. The HMAC configuration in [RedactionTests.cs](../tests/IxIFlow.Tests/ExecutionTests/RedactionTests.cs) exercises the actual package provider.

Use a secret, randomly generated key scoped to the deployment or tenant whose values may be correlated. Keep unrelated scopes on different keys/providers, obtain key material from the application's secret store, and rotate both the key and its identifier. The key must be Base64 encoded and meet the redactor's length requirements. IxIFlow supplies no global key and stores no key in the workflow definition. A scoped `IRedactorProvider` is supported when applications need scope-specific resolution.

Microsoft currently marks the HMAC API experimental (`EXTEXP0002`). See [Microsoft's redaction guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/data-redaction) for package configuration and key requirements.

## Errors and free-text notes

Activity trace errors and pending-activity reasons contain a safe exception type or engine code and a diagnostic ID. Snapshot `Errors` lists those identities, including workflow failures and operator stops. Raw messages, stack traces and stop descriptions stay in checkpoint diagnostic records. Trace `StackTrace` is empty. Diagnostic IDs are random correlation identifiers, independent of error contents; the same live exception keeps its ID across a span, trace and logger. Delivery inspection uses `InspectionRedactionPolicy.Delivery` to replace `LastError` with a delivery failure identity tied to the event, publisher and attempt. Store primitives continue to return original records for execution.

A plain string attempt note or operator decision note is wholly sensitive. The policy erases it by default, or passes the whole text to the registered redactor under `SensitiveAttribute.Classification`. Typed notes keep their member-by-member projection. This affects inspection copies, including traces carried by application results; execution and retry notes stay original.

Error spans use `error.type` and `ixiflow.diagnostic.id`, with error status and no status description, exception event, message or stack. Ordinary engine exception logs contain `ErrorType` and `DiagnosticId`, with no exception object. This covers mapping, recovery, outbox, retention, cancellation-watch, heartbeat and runnable-time failures. Application-authored logs and caller-supplied identifiers, actors and schema annotations remain the application's responsibility.

## Privileged raw diagnostics

`IWorkflowDiagnostics` is a separate scoped service. Calling `ReadAsync(instanceId)` requires an application registration of `IWorkflowDiagnosticsAuthorizer`. The authorizer checks the current caller and the requested instance on **every** read, before the store is read. Missing or rejecting authorization denies access. An authorizer failure propagates without returning diagnostics. Resolve this service in the caller's scope so the authorizer can use the application's current principal or tenant.

After authorization, `ReadAsync` returns checkpoint `WorkflowDiagnostic` records: diagnostic ID, error type, original message and original stack. An unknown or removed instance returns an empty list. This read neither runs the workflow nor changes state. Normal inspection never invokes it. A management or debugger integration must supply a separate application-authorized action if it offers this read; an ordinary inspect permission must not grant it.

Raw checkpoint diagnostics follow checkpoint retention and can contain secrets. Protect store access and any application route returning these records. Run and await results continue to return the original error and workflow data to the application.

## Opt into sensitive exception logging

`AddIxIFlow` disables the exact logger category `IxIFlow.Core.SensitiveDiagnostics`, even when ordinary logging is set to Trace. To explicitly enable full exceptions, add a category filter **after** registering IxIFlow:

Call `services.AddIxIFlow()` first, then call `services.AddLogging(logging => logging.AddFilter(InspectionRedactionPolicy.SensitiveDiagnosticsCategory, LogLevel.Error))`.

This category receives the full exception with its safe type and diagnostic ID. Ordinary engine categories keep the safe diagnostic summary. Configure the logging provider's destination and access controls for sensitive records before opting in. Enabling this category is independent of authorizing the privileged checkpoint read.

Redaction applies when producing new inspection output; it does not rewrite previously committed trace records.

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

One `RedactingJson` projection applies to typed attempt notes before they enter committed traces, snapshot `Data`, and `GetOpenWaitsAsync` default payloads. Trace copies returned in run results and through `GetTraceAsync` contain the same projected notes. Snapshots expose execution positions and projected data.

A classified string, number or object becomes a JSON string. A classified object is redacted as one complete serialized value. Classified lists retain their list shape and redact each element. An unclassified object retains its structure and projects its nested members. Erasure produces an empty string, including for classified nulls. Public members remain visible.

No redactor configuration is required for erasure. If a provider is registered, IxIFlow resolves `Microsoft.Extensions.Compliance.Redaction.IRedactorProvider` from the current operation scope. A missing provider or redactor, a provider/redactor exception, or a projection failure erases the affected value. Opaque custom-converter shapes, raw JSON, extension-data members and dictionaries are erased because this projection cannot reliably inspect their member classifications. They are erased even when a pseudonymizing redactor is configured.

Engine mapping logs carry property names and execution identity, without the mapped values. This also applies to unclassified values. Activities control their own logs and should configure their logging pipeline accordingly.

Redaction does not change checkpoints, saved notes, wait defaults, inbox payloads, or the data returned to the application by run and await calls. Execution and retries continue to use the original values. Store access must have its own access control.

## Opt into HMAC pseudonyms

Add `Microsoft.Extensions.Compliance.Redaction` to the application and call `services.AddRedaction`. Inside its builder callback, call `SetHmacRedactor`, supplying `HmacRedactorOptions.Key`, `KeyId`, and the classifications to pseudonymize. Use `SensitiveAttribute.Classification` for `[Sensitive]`; register the corresponding sets for application taxonomy attributes. Unconfigured classifications use the package's erasing fallback. The HMAC configuration in [RedactionTests.cs](../tests/IxIFlow.Tests/ExecutionTests/RedactionTests.cs) exercises the actual package provider.

Use a secret, randomly generated key scoped to the deployment or tenant whose values may be correlated. Keep unrelated scopes on different keys/providers, obtain key material from the application's secret store, and rotate both the key and its identifier. The key must be Base64 encoded and meet the redactor's length requirements. IxIFlow supplies no global key and stores no key in the workflow definition. A scoped `IRedactorProvider` is supported when applications need scope-specific resolution.

Microsoft currently marks the HMAC API experimental (`EXTEXP0002`). See [Microsoft's redaction guidance](https://learn.microsoft.com/en-us/dotnet/core/extensions/data-redaction) for package configuration and key requirements.

## Deferred free text and diagnostics

Error messages and stacks, string attempt notes, operator decision notes, recovery reasons, span error descriptions and recorded exceptions, and exception logging retain their existing behavior while their treatment awaits an owner decision. They can contain sensitive values. Member classification does not sanitize these free-text surfaces. Caller-supplied identifiers, actors, schema annotations and application logs also require application policy.

Redaction applies to newly produced inspection output. Previously committed trace text is not rewritten.

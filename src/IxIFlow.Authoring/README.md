# Standalone activity authoring

An `*.activity.yaml` file defines one asynchronous activity with its own key and integer version:

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

Read and validate an activity with `WorkflowYamlSerializer.ReadActivity(source)` and
`ActivityDocumentValidator.Validate(document, source)`. JSON editor representations use
`WorkflowJsonSerializer.ReadActivity` and `Write`. `DocumentModel.GetLocation(document, path)`
locates diagnostics such as `input.Value.default`. A supplied `default: null` is distinct
from an omitted default. Body and declarations use YAML literal blocks.

`ActivityCompilation.Compile(source)` returns assembly bytes, portable PDB bytes and
positioned diagnostics. It compiles deterministic Release C#, preserves the authored
source verbatim in the `ixiflow/activity.yaml` assembly resource, and embeds that source
and `activity.g.cs` in the PDB. The generated class implements `IAsyncActivity`; its body
can use `context`, `cancellationToken` and `await`. Optional `declarations` add fields,
constructors, helper methods or nested types.

Compilation references the runtime and SDK profile. Supply prepared package reference
assembly paths and host-declared shared contract assemblies through
`ActivityCompilationOptions`; the compiler does not resolve package imports. Compiling
does not instantiate the activity or execute its body or default converters.

`ActivityPackageIdentity` derives the package and assembly identity and C# type name:
`acme.send-email` version `2` becomes package `acme.send-email` version `2.0.0`, assembly
version `2.0.0.0`, and class `IxIFlow.Activities.Acme.SendEmail`.

The IxIFlow package ships `schemas/activity.schema.json` and exposes its installed path
as the `IxIFlowActivitySchema` MSBuild property. Activity files are excluded from automatic
workflow generation. Explicitly selecting one as an `IxIFlowWorkflow` reports IXIYAML002.

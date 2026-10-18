# Workflow source generation

The IxIFlow package ships the generator, analyzers, code fixes and YamlDotNet in
`analyzers/dotnet/cs`. Its `buildTransitive/IxIFlow.props` (also imported by
`build/IxIFlow.props` for direct references) automatically includes
`**/*.workflow.yaml` and `**/*.workflow.yml` as compiler `AdditionalFiles`.
The glob excludes `bin/`, `obj/`, `DefaultItemExcludes` and
`DefaultExcludesInProjectFolder`, and follows `EnableDefaultItems`.

Use `IxIFlowWorkflow` to include files with other names:

```xml
<ItemGroup>
  <IxIFlowWorkflow Include="Definitions/custom.yaml" />
</ItemGroup>
```

To disable the automatic glob, set this in the consuming project:

```xml
<PropertyGroup>
  <IxIFlowIncludeWorkflowFiles>false</IxIFlowIncludeWorkflowFiles>
</PropertyGroup>
```

The property defaults to `true`. Explicit `IxIFlowWorkflow` items still apply when
it or `EnableDefaultItems` is false. Existing `AdditionalFiles` entries are reused.
The props map workflow items after project evaluation and before Roslyn generates
its MSBuild editorconfig, including design-time builds. They publish
`IxIFlowWorkflow=true` with `CompilerVisibleItemMetadata`; the generator selects
only files with that metadata through `AnalyzerConfigOptionsProvider`.
Unrelated additional files, including `docker-compose.yaml`, are ignored.
These standard NuGet/MSBuild assets work in Visual Studio, Rider and `dotnet build`
without IDE-specific configuration, including through transitive package references.

For a generator project reference, use `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`
and import the props, or supply `AdditionalFiles` with `IxIFlowWorkflow="true"`
and register its `CompilerVisibleItemMetadata` yourself. Each file produces
`<RootNamespace>.Generated.Workflow_<filename>.Build()`; punctuation in the filename stem
becomes underscores. Filenames must be distinct within an assembly. The props expose
`RootNamespace` through `CompilerVisibleProperty`; the assembly name is the fallback.

The generator targets `netstandard2.0`, like the analyzers, so it can also load in
.NET Framework compiler hosts. Its only private runtime dependency is YamlDotNet;
Roslyn and framework assemblies come from the compiler host. The engine and
Authoring target .NET 10.

`WorkflowSourceTranslator.Translate(yaml, sourcePath, options)` returns the same fluent C#.
Its document overload accepts a `WorkflowDocument` directly. Both use the single model
and direct YamlDotNet reader in `IxIFlow.Generators/Documents`, compiled once into
the generator library and referenced by Authoring. Document values use ordinary
CLR scalars, lists and string-keyed mappings. The model declares its contract with
serializer-neutral attributes: `DocumentKind`, `DocumentRequired`, `DocumentValues` and
`Description`. `DocumentModel` reads them for the YAML reader and writer, for
`WorkflowJsonSerializer.Read/Write` in Authoring, and for the editor schema.
A publishing host supplies Roslyn, engine and activity references to compile the
returned source. The generator reads symbols and metadata, without loading activity code.

## Editor support

The IxIFlow package includes `schemas/workflow.schema.json`, the schema for the generator
in that package version. The props expose its path as `$(IxIFlowWorkflowSchema)`; print it
from the consuming project's folder:

```bash
dotnet msbuild -getProperty:IxIFlowWorkflowSchema
```

The schema declares a versioned GitHub URL as its `$id`. Use the installed package file
through `$(IxIFlowWorkflowSchema)`; the repository code does not establish that a matching
release tag or hosted schema is available.

With a yaml-language-server client, associate a file using the path printed above:

```yaml
# yaml-language-server: $schema=<installed schema path>
```

For example, after copying that schema to the workspace root, a workspace association in `settings.json` is:

```json
{
  "yaml.schemas": {
    "./workflow.schema.json": ["*.workflow.yaml", "*.workflow.yml"]
  }
}
```

In JetBrains IDEs, use Settings → Languages & Frameworks → Schemas and DTDs →
JSON Schema Mappings with the same filename patterns.

The schema provides completion, hover descriptions and validation for the document form
that the YAML and JSON readers accept. Member names are camelCase and exact. An unknown
member, or a `kind` that differs from the type a position requires, is an error at that
member, including in the generator. Keys you name yourself stay open: activity, step and
code names, `defaults`, `arguments` and `parameters`. The document root may carry
`$schema` to bind an editor; readers ignore it. Quote string values that look like numbers
or Booleans, such as `activityVersion: "1.2"`: the reader takes them as text, but editors
type them first. The schema is generated from the document model by
`WorkflowDocumentSchema.Write()` in Authoring.

The YAML and JSON readers reject unknown or miscased members; generator errors use
IXIYAML001 at the member. The JSON writer writes `kind` on every typed node, including
the fixed path in `to` and `into`. The YAML reader also rejects duplicate keys,
including case-insensitive duplicates; the JSON reader accepts duplicate keys with the last value winning.

The supported catalog includes activities and references, sequences, conditions, `WhileDo`/`DoWhile`,
general parallel branches and combiners, sequential or parallel `forEach` with results, typed
catch/finally blocks, sagas with outcome branches, compensation and retry, delays, requests with
replies and timeouts, event waits, suspend/resume and both named and typed child invocations. Explicit inputs
override YAML and attribute defaults. Step parameters use supplied arguments, then scalar defaults,
and reject missing required arguments. Declared parameter types support string, bool, int, long, double
and decimal; other parameter types are unsupported by local translation. Code assets use
`language: csharp-expression`; their source can call pure C# methods such as `System.Math.Abs`.
A `function` expression names a local asset and takes no arguments. YAML floats emit double literals; decimal
values can use an expression asset. `event` references work anywhere in an expression.

Paths use CLR property names: `workflow.Value`, `previousStep.Result`, `event.Value`.
Compilation symbols also resolve camel-case workflow property aliases. Library hosts supply
catalog type mappings through `WorkflowSourceOptions.Types` and, when needed, member mappings
through `Members` (keys `dataTypeKey.member`). Child contracts go in `Workflows`, keyed by
`name@version`, or by name when the host has resolved an unversioned request to a pinned version.
At build time, child contracts come from the other YAML files. Referenced activity attribute
keys are read as metadata names; source keys use `ForAttributeWithMetadataName`. Only
`GetTypesByMetadataName` lookups depend on the compilation. Value snapshots keep type resolution
and generation cached across unrelated edits.

The generator runs the shared `WorkflowDocumentValidator` before translation and reports errors
at YAML members. Explicit `activityVersion` values must match the resolved declared version.
Library hosts supply those versions through `ActivityVersions`. Imports require publishing to
resolve and pin dependencies; publishing hosts attest completed pins through `ResolvedImports`.
A workflow display name is retained in `definition.Metadata["DisplayName"]`.

A `forEach` infers its item type from `items`; its `as` name becomes a typed item handle.
`as` defaults to `item`. `steps` defines the body; use a trailing `result` expression or a
`kind: result` step to finish a result-producing body. `into` targets a `List<ItemResult<T>>`.
Optional `parallel` accepts `maxConcurrency` and `join: all|any|conditionally`. A conditional
join also declares `resultType` and `condition`, which can read `workflow` and `results`.
Document validation checks the selector, alias, body, result target and parallel options.

Explicit IDs are preserved; omitted IDs use structural paths. Reused templates prefix their
IDs with the reference ID. Fluent definitions reject duplicate IDs at build time.
`WithId(id, sourcePath, sourceLine)` records identity and diagnostics, with caller file and line
defaults for fluent authors. Use `Named(name)` to change a step's name explicitly; names affect
the fingerprint, while IDs and locations do not.

Compiler and analyzer diagnostics map to verbatim YAML paths with `#line`; quotes and newlines
in a source path are rejected. `#line default` closes the mapping after the last step.
ForEach result contracts are checked after setup, so their errors name the loop.
Build rules attach their offending step, and `WorkflowDefinitionValidator.Apply` decorates
the error once, including request, saga, ForEach, parallel and catalog errors. Rule results are
cached per definition instance by `Apply`, including the runtime-delegate requirement. Catalog
construction only computes scopes and fingerprints, so hand-built, non-executable definitions
can be fingerprinted without validation. Public validation, registration and starts prepare the
execution catalog once; dispatch, child resolution and recovery require the cached `Apply`
result and never run build rules as a fallback. Activity checkpoint-property reflection is
cached per type.

Generic builder methods capture activity dispatch, child resolution and context constructors.
Mappings retain their own typed factories, and catch builders capture typed default fault contexts
for nested steps, including mappings added after a build. Registration performs validation,
without a reflection binding pass. Clones retain their delegates. Compensation saved-value type
errors retain their `InvalidState` message. DI child definitions must match the registry fingerprint.

Generated selectors use the `itemsExpression`, `valueExpression` and `completeWhenExpression`
named arguments on `ForEach`, `Result` and `WaitConditionally` to retain expression trees.
Fluent authors can use the same overloads for equivalence. Positional callbacks keep the existing
delegate overloads, including statement bodies. Item captures use positional handles in fingerprints.
Equivalence tests compare structure, persisted fingerprints and results for all supported node kinds,
references, defaults and assets. A baseline fluent workflow pins the existing fingerprint.

Fingerprints sign the author-supplied delegates and expression trees. Runtime/context factories,
compiled expression adapters and the adapters for conditions, request subjects, joins and ForEach
selectors are excluded. Into contributes its expression and Combine its property/combiner type.
Runtime fingerprints describe the current definition. Declared artifact fingerprints and a
compatibility/migration policy are planned; do not infer compatibility with older builds.

Publishing orchestration, dependency resolution, package pinning, inline `use`, code activities,
bundles and artifact storage remain planned. Declared canonical fingerprints and
Native AOT validation remain planned too.

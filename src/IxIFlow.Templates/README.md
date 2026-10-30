# Durable templates

Configure templates on the workflow builder so publishing and rendering use the selected durable store:

<!-- snippet: samples/IxIFlow.Samples/QuoteMessage.cs#template-setup -->
```csharp
services.AddIxIFlow().AddIxIFlowTemplates().AddLiquid();
```

Publish an immutable version with its culture variants; the renderer and the `RenderTemplate<TModel>` step read it from the same store:

<!-- snippet: samples/IxIFlow.Samples/QuoteMessage.cs#template-publish -->
```csharp
await services.GetRequiredService<TemplatePublisher>().PublishAsync<QuoteData>(
    TemplateVersion.Create<QuoteData>("quote", 1, "liquid", TemplateFormat.Text,
        new TemplateVariant("", "Quote for {{ Customer }}"),
        new TemplateVariant("fr", "Devis pour {{ Customer }}")));
```

The model is a public object type with typed readable members. Closed generic objects, structs, record structs, public nested types, abstract classes and interfaces are supported declared read surfaces. Root scalars, `object`, arrays, collections, dictionaries, open generics and inaccessible types are refused. Typed collections and dictionaries inside a model remain readable. A derived runtime value exposes the declared surface used to publish and render the template.

`TemplateReference`, `TemplateVersion`, `TemplateVariant`, `TemplateFormat`, `TemplateVersionEntry`, model rules and deterministic `TemplateJson` content live in the `IxIFlow` assembly under `IxIFlow.Templates`. Engines, `TemplatePublisher`, `TemplateRenderer` and activities live in `IxIFlow.Templates`. Engines implement the explicit `object model, Type modelType` render entry; the renderer supplies `typeof(TModel)`.

Template keys use lowercase package-id grammar, at most 100 characters; versions are positive. Each version requires a base culture (`""`). Cultures are canonicalized, ordered base-first, and fall back through parents to the base. Only Liquid and Text/Html formats are supported. Publication validates every variant. Identical registration returns the first stored audit without advancing the catalog revision; different content at that key/version raises `TemplateVersionConflictException`. Publication timestamps come from the store.

Code registration binds the model and generic argument assemblies to registered packages by their recorded DLL entry digests, then records the complete package closure. Host models require no package row. When equal DLL bytes belong to several catalog package identities, supply the exact `ModelPackages` pins on the version to select the owner. Model package identities and artifacts must exist, and withdrawn dependencies cannot be newly authored. Existing templates and renders survive withdrawal; their model package bytes are permanent retention roots. Templates have no lifecycle states or collection operation.

`IWorkflowCatalogStore` exposes `RegisterTemplateVersionAsync`, `GetTemplateVersionAsync`, `GetTemplateVersionsAsync` and `PublishTemplateDefinitionAsync` on every store. Draft publication checks the complete generation/revision, consumes that exact draft atomically, and returns its first winning receipt on retries. Content bytes exclude receipt and audit. `GetPackageHoldersAsync` includes templates and scope-masks their names.

`AddIxIFlowTemplates` explicitly registers `SendTemplateMessage` through the activity registry as `ixiflow.templates.send-message`, version `1.0`, category `Templates`. Applications supply `ITemplateMessageSender`; its message ID is the idempotency key. Workflow YAML publishing recognizes the built-in key from the shared template assembly without a package import.

Published workflow documents use the built-in `renderTemplate` step:

<!-- snippet: samples/IxIFlow.Samples/CatalogExamples.cs#render-yaml -->
```yaml
workflow: {name: CatalogRenderSample, dataType: IxIFlow.Samples.MessageData}
definitions:
  - kind: renderTemplate
    id: render
    template: quote@1
    modelType: IxIFlow.Samples.QuoteData
    model: {path: workflow.Quote}
    culture: {kind: constant, value: fr-CA}
    output: [{source: Rendered, to: {path: workflow.Body}}]
```

Publishing resolves an omitted version to the highest published template version and infers its declared model type. The canonical YAML records both the exact version and `modelType`. A build-time generator without a publishing catalog requires an explicit version and `modelType`. The generated activity is `RenderTemplate<TModel>`, with the same mappings and structure as fluent authoring. Rendering can use saga error policies; it has no compensation.

The bundle manifest embeds canonical template JSON at `templates/{key}@{version}.json`, pins the complete model package closure, and records permitted template references separately for each runtime catalog site. Inline callers copy verified callee payloads and remap their bindings to grafted sites. Cold loading verifies content digests, model identities and reconstructed constants before making the template set available.

During a bundled run, every renderer call uses that dispatch's embedded template set and permitted references. This includes custom activities, parallel branches, compensation and recovery. A site's permission does not authorize another site's template. A missing binding, changed saved input or mismatched model fails with an engine `InvalidDefinition`; workflow Catch and saga error/retry handlers do not handle these guard failures, even if an activity catches the renderer's exception. Ordinary authored exceptions retain their normal handling. Bundled rendering makes no template catalog queries. Code-defined execution continues to use the durable catalog.

`TemplateStepDescriptors.RenderTemplate()` supplies the registered catalog descriptor; `Bind(version)` resolves its dependent Model input to the selected template's saved and C# model names. Culture is a nullable string. Rendering remains one recoverable activity for breakpoints, stepping and restart; template text is content rather than a separate executable source.

Editors use `IWorkflowCatalogStore.ListStepDescriptorsAsync(scope)` alongside
activity reads, and `ListTemplatesAsync(CatalogTemplateQuery)`,
`ListTemplateVersionsAsync(key, CatalogVersionQuery)` and
`GetTemplateDetailAsync(key, version, scope)` for template choices. These reads
apply template scope before paging and mask related model packages by package
scope. Pages use projections without loading variant content. The descriptor
factory lives with the core catalog value contracts and describes the rendering
assembly's output by portable identity, without loading an engine into a store.
See the [unified catalog API](../../docs/workflow-catalog.md) for shapes,
ordering, cursor and caching rules.

[PublishedArtifacts](../../samples/IxIFlow.Samples/PublishedArtifacts.cs) provides a complete template document, read-only preview and bundled render example. The [YAML guide](../../docs/yaml-authoring.md#template-documents-and-bundled-rendering) documents the syntax and [HTTP guide](../../docs/publishing-http.md) describes PreviewPolicy, bounds and source download authorization.

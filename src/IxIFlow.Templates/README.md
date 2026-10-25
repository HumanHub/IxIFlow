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

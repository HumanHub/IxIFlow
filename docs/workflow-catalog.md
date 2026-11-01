---
title: Unified catalog and descriptor reads
description: Scoped workflow, activity, package, template and step metadata reads.
---

`IWorkflowCatalogStore` supplies scoped workflow, activity, package, template
and built-in step metadata on memory, SQLite, SQL Server and PostgreSQL.
`IWorkflowCatalog` forwards the same reads and adds live host queries. These are
library APIs also back the [publishing and catalog HTTP routes](publishing-http.md), with explicit policies and a per-request application scope.

## Scope and paging

Every client-facing reader takes `CatalogScope`, directly or through its query.
Its required `Workflows`, `Activities`, `Packages` and `Templates` members are
`CatalogNames(Names, Prefixes)`. Both lists null means unrestricted; otherwise
exact names and literal prefixes are unioned. Two empty lists deny everything.
Each list allows at most 100 entries, normalized, deduplicated and sorted for
the scope hash. Package and template scope values are lowercase.

Scope applies before filtering, ordering, counts and paging. Activity readers
intersect activity-key scope with the provider's package-id scope. Package
authorization grants no additional activity keys. Related workflow dependencies,
package closures and template model packages are masked by their own scopes.
An out-of-scope detail returns null.

Pages return `CatalogPage<T>(Items, NextCursor, CatalogRevision)`. The default
limit is 100; accepted limits are 1–1,000. Stores read at most limit + 1 eligible
rows to decide whether to return a cursor. Cursors bind the endpoint, normalized
filters, complete keyset and scope hash. Reusing one with another scope, filter
or version-page identity throws `ArgumentException`. Limits may change between
pages. Continuations tolerate later writes; the revision describes that page's
atomic read rather than a frozen traversal.

Text searches trim and lowercase invariantly and match literal substrings of
the bounded search projection. SQL wildcard characters are literal. Catalog
text rejects NUL and malformed Unicode through `WorkflowCatalogRules.ValidateText`.

## Workflows

`WorkflowDefinition` exposes `DisplayName`, `Description`, `Tags`, `Icon` and
`Category`. The same members are available on `[Workflow]` and on root and
continuation builders. Attribute values initialize the builder; fluent calls
can override them. YAML uses `displayName`, `description`, `tags`, `icon` and
`category` under `workflow`. Display names default to the workflow name.

`WorkflowDescriptor.Of(definition)` produces portable value metadata, including
the saved data type identity and its canonical C# name. Icons are lowercase
name tokens (WF007); a supplied category cannot be blank (WF008). Display names
are limited to 200 characters, descriptions to 4,000, categories and icons to
100. Tags contain 1–50 word, dot or hyphen characters and their sorted projection
must fit within 400 characters. Workflow presentation does not change the
structure hash. Input/output labels remain part of the workflow contract hash.

Every `WorkflowVersion` requires a descriptor. Registering an existing code
version refreshes its presentation atomically and advances the catalog revision
when it changes. Data type, identity, structure and member contracts remain
immutable. A published bundle's presentation is immutable. Executable definitions
and debugger source stay pinned to the definition or bundle acquired by the host.

Use these explicitly scoped readers on `IWorkflowCatalogStore`:

- `ListWorkflowsAsync(CatalogWorkflowQuery)` filters by literal text, tag,
  category and state. One version must satisfy every filter. Summaries choose
  the highest Published version, otherwise the highest version, within that
  filtered set. Counts include registered Draft versions; editable drafts are
  separate resources.
- `ListWorkflowVersionsAsync(name, CatalogVersionQuery)` returns ascending
  integer versions with descriptors, hashes and lifecycle revisions.
- `GetWorkflowVersionDetailAsync(name, version, scope)` returns the contract,
  scoped workflow dependencies and scoped package pins, publication audit and
  current artifact availability. Pages never load manifest or contract payloads.

Pages use limits of 1–1,000 and cursors bound to the full query and scope. Scope
restrictions apply before counts and pagination. A page's revision describes its
own atomic read; it does not freeze subsequent continuation pages.

`IWorkflowCatalog.GetWorkflowVersionHostsAsync(name, version, scope)` returns
live hosts advertising `workflow:name@version` with the version's exact structure
hash. A general bundle-host capability does not imply availability. Host and
artifact-availability responses must use no-store caching. Retirement blocker
reads mask hidden callers and preserve `OutsideScope`; retirement itself still
checks the complete dependency graph.

## Activities and packages

| Reader | Result and selection |
| --- | --- |
| `ListActivitiesAsync(CatalogActivityQuery)` | `CatalogActivity` rows; optional Text, Category, PackageId, Origin, Withdrawn and ArtifactPresent filters. |
| `ListActivityVersionsAsync(key, CatalogVersionQuery)` | All scoped provider/version rows for one activity key. |
| `GetActivityAsync(key, version, ActivityProviderSelector, scope)` | Activity detail and current live hosts for the exact provider. The selector requires PackageId, PackageVersion and Build. |
| `ListPackagesAsync(CatalogPackageQuery)` | `CatalogPackage` rows; optional Text, Origin, Withdrawn and ArtifactPresent filters. |
| `ListPackageVersionsAsync(id, CatalogVersionQuery)` | Scoped package versions. |
| `GetPackageDetailAsync(id, version, scope)` | Summary, scoped activities/closure and registration/withdrawal audit. |
| `GetPackageActivitiesAsync(id, version, CatalogVersionQuery)` | Paged activities intersecting both activity and package scopes. |

Activity order is `(key, version, packageId, packageVersion, build)`, using
ordinal text order. Activity versions are labels with no global latest version.
Package order is `(id, version)` using ordinal text order, rather than NuGet
release precedence. Package activity counts include only permitted activity keys.

The activity catalog unions artifact providers with installed Local descriptors.
Artifact Build is the package digest; Local Build is the installed fingerprint.
Different Local fingerprints coexist. Local rows have Withdrawn and
ArtifactPresent false. Other rows retain their package's withdrawal state and
current artifact availability; defaults include withdrawn and unavailable rows.
ArtifactPresent filters probe the configured artifact store in bounded batches
before page limiting, including when artifacts reside in files.

`IWorkflowCatalog.GetPackageHostsAsync(id, version, build, scope)` reports live
Local marks matching the permitted activities and requested build. Downloadable
package bytes alone make no host claim. Availability and host responses require
no-store caching because those facts are not covered by the catalog revision.

## Templates and built-in steps

<!-- snippet: samples/IxIFlow.Samples/CatalogExamples.cs#template-reads -->
```csharp
public static async Task<(CatalogPage<CatalogTemplate> Templates, IReadOnlyList<CatalogStepDescriptor> Steps)>
    ReadAsync(IWorkflowCatalogStore catalog, CatalogScope scope)
{
    var templates = await catalog.ListTemplatesAsync(new CatalogTemplateQuery(scope, Engine: "liquid"));
    var steps = await catalog.ListStepDescriptorsAsync(scope);

    return (templates, steps);
}
```

The public [template records](../src/IxIFlow/Core/CatalogTemplates.cs) and [catalog store API](../src/IxIFlow/Core/WorkflowCatalog.cs) define query and receipt shapes. `ListTemplateVersionsAsync` pages one key; `GetTemplateDetailAsync` reads its publication receipt.

Global template pages contain every matching version, ordered by `(key, version)`;
version pages use ascending integer versions. Text matches key/display name,
Engine is trimmed and lowercase, and all supplied filters must match the same
row. These shapes have no separate total-count field. Traverse NextCursor for
the complete filtered set. There is no template lifecycle or latest-only list.
Format is the enum name `Text` or `Html`. Cultures are structured names, base
culture `""` first. ModelType is the canonical C# name and ModelTypeId the saved
identity. ModelPackages contains only package identities allowed by Packages.

Pages read projection columns and the small cultures/model-packages JSON,
never template content JSON. Detail verifies the immutable content against its
projections and returns the publication receipt and store-stamped UTC audit;
it does not expose variant text. Trusted publisher/execution APIs
`GetTemplateVersionAsync` and `GetTemplateVersionsAsync` retain complete content
and pins and must not be used as scoped client adapters. Source download needs
its own authorization.

Step descriptors appear alongside activities as a separate kind of editor
choice. They disclose no catalog identities, so any non-null scope receives the
built-in `renderTemplate` descriptor: Render template, Templates, file-text.
It has required Template and Model inputs, optional nullable string Culture,
and nullable RenderedTemplate output. Model has null Type/TypeName and
TypeFrom `template.ModelType`; select a template through the scoped readers,
then use `TemplateStepDescriptors.Bind(version)` during authoring to bind the
model's saved and C# identities. This does not fabricate a provider for the
open generic activity `RenderTemplate<T>`.

The same read also returns `scope` (Scope, Scopes, box), including Activity,
ActivityVersion, Defaults, Input, Output, Retry, Threading and Steps. Select an
activity whose descriptor has a non-null `Scope`; its saved/C# value type,
Concurrency and minimum Threading come from the verified implementation. Inputs
and outputs bind to that activity's members. `RequiresScopes` describes enclosing
value types required by ordinary or scope activities. These members survive activity
pages, activity detail, package activity pages and package detail for published,
uploaded, resolved and Local providers on every store. Metadata reads do not open
the scope or load its implementation. Built-in step descriptors remain available
under a deny-all identity scope.

Pure metadata responses may use a private, revalidated ETag containing catalog
revision plus normalized query/scope hash. Source, artifact availability and
live-host responses need their own download policy or no-store handling as
applicable; a revision alone does not certify those facts.

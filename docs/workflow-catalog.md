# Workflow descriptors and catalog reads

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

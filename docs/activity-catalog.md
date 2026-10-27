# Activity and package catalog

`ActivityDescriptor.OfType(type, packageId, packageVersion)` derives a declaration from an activity's attributes and public properties. `Scan(assembly, packageId, packageVersion)` refuses invalid attributed types. Local registration additionally requires the default, noncollectible load context and a transient service registration.

Descriptors include display metadata, inputs and outputs, package identity, `MetadataTypeName` for reflection lookup, and `CSharpTypeName` for generated source. Members carry a saved type identity in `Type` and a fully qualified source spelling in `TypeName`. Nested and constructed generic names retain their structure. `ActivityType` is available for local declarations and is excluded from JSON.

`ToJson()` writes a validated compact descriptor with members ordered by ordinal name. `Parse(json)` reads the current complete shape without loading implementation types. `ContractHash` covers activity identity and executable member semantics; presentation and implementation names do not affect it. Installed package fingerprints cover the complete canonical descriptor array and identify metadata rather than executable bytes. Different fingerprints for one package version can coexist.

## Uploads and indexing

Use `PackageIngestion` from the authoring assembly to verify and store a package independently of workflow publishing:

<!-- snippet: samples/IxIFlow.Samples/DocumentationExamples.cs#package-ingestion -->
```csharp
var ingestion = new PackageIngestion(catalog, artifacts, publisherOptions);
var candidate = await ingestion.ReadPackageAsync(stream, cancellationToken);
var receipt = await ingestion.StorePackageAsync(candidate, actor, cancellationToken);
```

`UploadPackageAsync(stream, actor, cancellationToken)` combines these calls. Both paths enforce the configured archive, entry, expanded-size, entry-count and nuspec bounds. Storage revalidates caller-created candidates. A verified upload supplies its root bytes directly to resolution, so the root needs no feed or prior catalog registration.

`PackageRegistration.Stored` is the stored audited row, `Activities` is its stored extraction result, and `Created` is true for exactly the first accepted registration. Replays preserve the first registration's time and actor. Origins strengthen from `Resolved` to `Uploaded` to `Published`. An existing identity cannot acquire different archive bytes, entry hashes or dependency metadata.

Extraction selects this publisher's managed runtime assets and uses a bounded collectible context. It derives descriptors without constructing or executing activities. Attributes and default converters can execute trusted package code. A timeout abandons waiting; arbitrary managed code cannot be forcibly canceled.

- `Indexed` has validated descriptors and a complete closure, including a valid empty descriptor set or empty closure.
- `Pending` has an unresolved closure and durable missing dependency IDs, with no partial activity rows.
- `Unloadable` has no managed implementation for this publisher's runtime.

Uploads revisit pending dependents. Run `PackageIndexingService` on a publishing host for startup and periodic recovery after a crash:

<!-- snippet: samples/IxIFlow.Samples/DocumentationExamples.cs#package-indexing -->
```csharp
services.AddHostedService(provider => new PackageIndexingService(
    provider.GetRequiredService<IWorkflowStateRepository>(),
    provider.GetRequiredService<IArtifactStore>(), publisherOptions));
```

Its passes are bounded, continue through pending work, and retry completion against the recorded digest. Reference/runtime disagreement reports `PUB014`; a selected dependency closure changing an indexed activity's contract reports `PUB015`.

## Publication, withdrawal and retention

`PublishPackageDefinitionAsync(draftId, package, activities, artifacts, actor)` atomically records a Published package, exactly its matching activity descriptor, its resolved closure and store audit, and consumes the exact activity draft. The package identity is the draft key and integer version expressed as `n.0.0`. The package's `PublishedDraft` must match the complete kind/name/version/generation/revision. Retries return the first stored receipt; an uploaded identity without that receipt conflicts. Activity document compilation and package construction are supplied by the activity publisher handler.

Use `WorkflowPublisher.ValidateAsync(activityDraft)` for a full no-write Scratch run or `PublishAsync(activityDraft.Id, actor)` to produce the deterministic nupkg and atomically publish it. The typed result's `Package` is the audited stored row. Source retrieval uses the nupkg's verbatim `ixiflow/activity.yaml`; profile and digest-pinned closure metadata use `ixiflow/activity.json`. Portable symbols and embedded source accompany the DLL. Package readers verify archive/entry hashes before scratch or bundle loaders use a sibling PDB.

Shared application interfaces must be declared in `WorkflowPublisherOptions.SharedContractAssemblies` and in each host's `builder.ShareContracts(...)`. These default-context, closed profiles freeze before use and record exact required versions. Contracts stay application-supplied rather than private package dependencies, so host DI and package code use the same interface types. Missing or incompatible contracts prevent authoring/load and host capability advertisement. See the [Authoring API](../src/IxIFlow.Authoring/README.md) for Test, dependency and consumer behavior.

`SetPackageWithdrawnAsync(id, version, true, new CatalogMutationToken(package.LifecycleRevision), actor)` withdraws a package from new authoring. A stale token raises `CatalogPreconditionException`. Withdrawal is allowed with holders and preserves identity, descriptors and bytes. Known withdrawn identities are rejected before feed acquisition, including explicit imports and seeded candidates. Existing digest-pinned execution, verified callee copying and exact package downloads keep using their saved bytes.

Restore by uploading the identical verified archive through `PackageIngestion` or `WorkflowPublisher`. The store clears withdrawal atomically, advances its lifecycle token and preserves the original registration/publication audit and stronger origin. Different bytes are refused; `SetPackageWithdrawnAsync(..., false, ...)` cannot restore metadata alone. Resolver replay and extraction completion cannot restore a package.

`GetPackageHoldersAsync(id, version, scope)` reports scoped workflow, template and package references, instance pins, unreadable orphan pins and whether holders exist outside scope. Holders inform retention and never veto withdrawal. Uploaded and Published packages retain their complete closures, including while withdrawn. Template model packages, workflow manifests and retained instance pins also protect bytes. Unreadable orphan pins conservatively protect package content and recorded closures while unrelated abandoned bundles remain collectable. Unheld Resolved bytes may be collected after retention grace; their immutable identity and activity rows remain visible with `ArtifactPresent` false.

## Scoped readers

The [unified catalog API](workflow-catalog.md) also documents workflow,
template/version/detail and built-in step reads with the same scope and paging
contracts.

`ListActivitiesAsync`, `ListActivityVersionsAsync`, `GetActivityAsync`, `ListPackagesAsync`, `ListPackageVersionsAsync`, `GetPackageDetailAsync` and `GetPackageActivitiesAsync` take an explicit `CatalogScope`. Activity reads require both the activity key and provider package ID to be permitted. Package activity counts and related closure or missing-dependency summaries respect the same scope.

Pages have stable keysets, a continuation cursor and the revision observed with their rows. Cursors bind the normalized filters and scope; reuse with a different query is refused. Text search treats wildcard characters literally. Availability filters run before page limiting and continue across absent-artifact batches.

Local providers use installed fingerprints as `Build`; artifact providers use their package digest. Activity version labels have no global latest ordering. Activity detail requires the package ID, package version and build. `GetPackageHostsAsync` reports only matching live Local marks with permitted activity keys. Stored package bytes alone make no host claim.

`WorkflowCatalog` uses its configured artifact store for current availability. Direct concrete store users can set `CatalogArtifacts` when files replace database artifacts. External file changes and host liveness do not advance the metadata revision. Responses containing `ArtifactPresent` or hosts must use `no-store`; a revision alone cannot validate them.

Package acceptance, descriptors, closure, reverse dependency edges and revision commit together. Installed content insertion also advances the revision; equal registrations and mark-only heartbeats do not. Earlier database schemas and bundle manifests must be recreated with the current build; there are no compatibility readers or in-place migrations.

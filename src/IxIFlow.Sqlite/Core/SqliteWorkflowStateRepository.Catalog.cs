namespace IxIFlow.Core;

public sealed partial class SqliteWorkflowStateRepository
{
    private const string CatalogSchema = """
                                         CREATE TABLE ixiflow_publication_heads (
                                             kind text COLLATE BINARY NOT NULL CHECK (kind IN ('workflow','activity','template')),
                                             name text COLLATE BINARY NOT NULL,
                                             version INT NOT NULL,
                                             generation TEXT NOT NULL,
                                             revision BIGINT NOT NULL,
                                             active_id TEXT NULL,
                                             last_attempt INT NOT NULL,
                                             PRIMARY KEY (kind, name, version, generation, revision)
                                         );
                                         CREATE TABLE ixiflow_publication_attempts (
                                             id TEXT NOT NULL,
                                             kind text COLLATE BINARY NOT NULL CHECK (kind IN ('workflow','activity','template')),
                                             name text COLLATE BINARY NOT NULL,
                                             version INT NOT NULL,
                                             generation TEXT NOT NULL,
                                             revision BIGINT NOT NULL,
                                             attempt INT NOT NULL,
                                             state text NOT NULL CHECK (state IN ('Running','Published','Failed','Abandoned')),
                                             host_id TEXT NOT NULL,
                                             started_by text COLLATE BINARY NULL,
                                             started_at_utc TEXT NOT NULL,
                                             heartbeat_at_utc TEXT NOT NULL,
                                             finished_at_utc TEXT NULL,
                                             outcome_json text NULL,
                                             catalog_revision BIGINT NOT NULL,
                                             PRIMARY KEY (id)
                                         );
                                         CREATE UNIQUE INDEX ix_publication_attempt_identity ON ixiflow_publication_attempts (kind, name, version, generation, revision, attempt);
                                         CREATE INDEX ix_publication_history ON ixiflow_publication_attempts (kind, name, version, started_at_utc, id);
                                         CREATE INDEX ix_publication_finished ON ixiflow_publication_attempts (finished_at_utc);
                                         CREATE TABLE ixiflow_definitions (
                                             kind text COLLATE BINARY NOT NULL CHECK (kind IN ('workflow','activity','template')),
                                             name text COLLATE BINARY NOT NULL,
                                             version INT NOT NULL,
                                             generation TEXT NOT NULL,
                                             revision BIGINT NOT NULL,
                                             source text NOT NULL,
                                             source_length INT NOT NULL,
                                             updated_at_utc TEXT NOT NULL,
                                             updated_by text COLLATE BINARY NULL,
                                             PRIMARY KEY (kind, name, version)
                                         );
                                         CREATE TABLE ixiflow_draft_generations (
                                             generation TEXT NOT NULL,
                                             kind text COLLATE BINARY NOT NULL CHECK (kind IN ('workflow','activity','template')),
                                             name text COLLATE BINARY NOT NULL,
                                             version INT NOT NULL,
                                             PRIMARY KEY (generation)
                                         );
                                         CREATE TABLE ixiflow_catalog_revision (
                                             id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                                             revision BIGINT NOT NULL CHECK (revision >= 0)
                                         );
                                         INSERT INTO ixiflow_catalog_revision (id, revision) VALUES (1, 0);
                                         CREATE TABLE ixiflow_workflow_versions (
                                             name TEXT COLLATE BINARY NOT NULL,
                                             version integer NOT NULL,
                                             structure_hash TEXT NOT NULL,
                                             state TEXT NOT NULL CHECK (state IN ('Draft','Published','Deprecated','Retired')),
                                             catalog_revision BIGINT NOT NULL,
                                             published_draft_generation TEXT NULL,
                                             published_draft_revision BIGINT NULL,
                                             published_at_utc TEXT NULL,
                                             published_by text COLLATE BINARY NULL,
                                             state_changed_at_utc TEXT NULL,
                                             state_changed_by text COLLATE BINARY NULL,
                                             lifecycle_revision BIGINT NOT NULL,
                                             artifact_digest TEXT COLLATE BINARY NULL,
                                             display_name text COLLATE BINARY NOT NULL,
                                             category text COLLATE BINARY NULL,
                                             tags text COLLATE BINARY NOT NULL,
                                             search_text text COLLATE BINARY NOT NULL,
                                             descriptor_json text NOT NULL,
                                             contract_hash text COLLATE BINARY NOT NULL,
                                             metadata_json TEXT NOT NULL,
                                             PRIMARY KEY (name, version)
                                         );
                                         CREATE INDEX ix_workflow_versions_revision ON ixiflow_workflow_versions (catalog_revision);
                                                 CREATE INDEX ix_workflow_versions_category ON ixiflow_workflow_versions (category, name);
                                         CREATE TABLE ixiflow_template_versions (
                                             "key" text COLLATE BINARY NOT NULL,
                                             "version" int NOT NULL,
                                             "engine" text COLLATE BINARY NOT NULL,
                                             "format" text COLLATE BINARY NOT NULL,
                                             "model_type" text COLLATE BINARY NOT NULL,
                                             "model_type_id" text COLLATE BINARY NOT NULL,
                                             "model_packages_json" text COLLATE BINARY NOT NULL,
                                             "cultures_json" text COLLATE BINARY NOT NULL,
                                             "display_name" text COLLATE BINARY NULL,
                                             "description" text COLLATE BINARY NULL,
                                             "search_text" text COLLATE BINARY NOT NULL,
                                             "published_draft_generation" TEXT NULL,
                                             "published_draft_revision" bigint NULL,
                                             "published_at_utc" TEXT NOT NULL,
                                             "published_by" text COLLATE BINARY NULL,
                                             "metadata_json" text COLLATE BINARY NOT NULL,
                                             "catalog_revision" bigint NOT NULL,
                                             PRIMARY KEY ("key", "version")
                                         );
                                         CREATE TABLE ixiflow_packages (
                                             id TEXT COLLATE BINARY NOT NULL,
                                             version TEXT COLLATE BINARY NOT NULL,
                                             dependency_groups_json TEXT NOT NULL,
                                             digest TEXT COLLATE BINARY NOT NULL,
                                             metadata_json TEXT NOT NULL,
                                             origin TEXT COLLATE BINARY NOT NULL CHECK (origin IN ('Resolved','Uploaded','Published')),
                                             extraction TEXT COLLATE BINARY NOT NULL CHECK (extraction IN ('Indexed','Pending','Unloadable')),
                                             extraction_detail TEXT COLLATE BINARY NULL,
                                             closure_json TEXT COLLATE BINARY NULL,
                                             withdrawn INTEGER NOT NULL,
                                             withdrawn_at_utc TEXT COLLATE BINARY NULL,
                                             withdrawn_by TEXT COLLATE BINARY NULL,
                                             registered_at_utc TEXT COLLATE BINARY NOT NULL,
                                             registered_by TEXT COLLATE BINARY NULL,
                                             published_draft_generation TEXT COLLATE BINARY NULL,
                                             published_draft_revision INTEGER NULL,
                                             published_draft_name TEXT COLLATE BINARY NULL,
                                             published_draft_version INTEGER NULL,
                                             published_at_utc TEXT COLLATE BINARY NULL,
                                             published_by TEXT COLLATE BINARY NULL,
                                             lifecycle_revision INTEGER NOT NULL,
                                             catalog_revision INTEGER NOT NULL,
                                             PRIMARY KEY (id, version)
                                         );
                                         CREATE INDEX ix_packages_origin ON ixiflow_packages (origin, id, version);
                                         CREATE TABLE ixiflow_package_activities (
                                             package_id TEXT COLLATE BINARY NOT NULL,
                                             package_version TEXT COLLATE BINARY NOT NULL,
                                             "key" TEXT COLLATE BINARY NOT NULL,
                                             version TEXT COLLATE BINARY NOT NULL,
                                             metadata_type_name TEXT COLLATE BINARY NOT NULL,
                                             csharp_type_name TEXT COLLATE BINARY NOT NULL,
                                             display_name TEXT COLLATE BINARY NOT NULL,
                                             category TEXT COLLATE BINARY NOT NULL,
                                             icon TEXT COLLATE BINARY NULL,
                                             search_text TEXT COLLATE BINARY NOT NULL,
                                             contract_hash TEXT COLLATE BINARY NOT NULL,
                                             descriptor_json TEXT COLLATE BINARY NOT NULL,
                                             catalog_revision INTEGER NOT NULL,
                                             PRIMARY KEY (package_id, package_version, "key")
                                         );
                                         CREATE INDEX ix_package_activities_key ON ixiflow_package_activities ("key", version, package_id, package_version);
                                         CREATE INDEX ix_package_activities_category ON ixiflow_package_activities (category, "key");
                                         CREATE TABLE ixiflow_installed_activities (
                                             package_id TEXT COLLATE BINARY NOT NULL,
                                             package_version TEXT COLLATE BINARY NOT NULL,
                                             "key" TEXT COLLATE BINARY NOT NULL,
                                             version TEXT COLLATE BINARY NOT NULL,
                                             metadata_type_name TEXT COLLATE BINARY NOT NULL,
                                             csharp_type_name TEXT COLLATE BINARY NOT NULL,
                                             display_name TEXT COLLATE BINARY NOT NULL,
                                             category TEXT COLLATE BINARY NOT NULL,
                                             icon TEXT COLLATE BINARY NULL,
                                             search_text TEXT COLLATE BINARY NOT NULL,
                                             contract_hash TEXT COLLATE BINARY NOT NULL,
                                             descriptor_json TEXT COLLATE BINARY NOT NULL,
                                             catalog_revision INTEGER NOT NULL,
                                             fingerprint TEXT COLLATE BINARY NOT NULL,
                                             PRIMARY KEY (package_id, package_version, fingerprint, "key")
                                         );
                                         CREATE INDEX ix_installed_activities_key ON ixiflow_installed_activities ("key", version, package_id, package_version);
                                         CREATE INDEX ix_installed_activities_category ON ixiflow_installed_activities (category, "key");
                                         CREATE TABLE ixiflow_package_pending_dependencies (
                                             package_id TEXT COLLATE BINARY NOT NULL,
                                             package_version TEXT COLLATE BINARY NOT NULL,
                                             missing_package_id TEXT COLLATE BINARY NOT NULL,
                                             PRIMARY KEY (package_id, package_version, missing_package_id)
                                         );
                                         CREATE INDEX ix_package_pending_reverse ON ixiflow_package_pending_dependencies (missing_package_id, package_id, package_version);
                                         CREATE TABLE ixiflow_artifacts (
                                             digest TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                                             kind TEXT NOT NULL,
                                             content BLOB NOT NULL,
                                             created_at_utc TEXT NOT NULL
                                         );
                                         CREATE INDEX ix_artifacts_created ON ixiflow_artifacts (created_at_utc);
                                         """;

    private static string? CatalogSql(Statement statement) => statement switch
    {
        Statement.CatalogPackages =>
            "SELECT id AS Id, version AS Version, digest AS Digest, metadata_json AS MetadataJson, dependency_groups_json AS DependencyGroupsJson, origin AS Origin, extraction AS Extraction, extraction_detail AS ExtractionDetail, closure_json AS ClosureJson, withdrawn AS Withdrawn, withdrawn_at_utc AS WithdrawnAtUtc, withdrawn_by AS WithdrawnBy, registered_at_utc AS RegisteredAtUtc, registered_by AS RegisteredBy, published_draft_generation AS PublishedDraftGenerationValue, published_draft_revision AS PublishedDraftRevision, published_draft_name AS PublishedDraftName, published_draft_version AS PublishedDraftVersion, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, lifecycle_revision AS LifecycleRevision, catalog_revision AS CatalogRevision FROM ixiflow_packages WHERE id=COALESCE(@Id,id)",
        Statement.CatalogPackage =>
            "SELECT id AS Id, version AS Version, digest AS Digest, metadata_json AS MetadataJson, dependency_groups_json AS DependencyGroupsJson, origin AS Origin, extraction AS Extraction, extraction_detail AS ExtractionDetail, closure_json AS ClosureJson, withdrawn AS Withdrawn, withdrawn_at_utc AS WithdrawnAtUtc, withdrawn_by AS WithdrawnBy, registered_at_utc AS RegisteredAtUtc, registered_by AS RegisteredBy, published_draft_generation AS PublishedDraftGenerationValue, published_draft_revision AS PublishedDraftRevision, published_draft_name AS PublishedDraftName, published_draft_version AS PublishedDraftVersion, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, lifecycle_revision AS LifecycleRevision, catalog_revision AS CatalogRevision FROM ixiflow_packages WHERE id=@Id AND version=@Version",
        Statement.CatalogPackagePage =>
            "SELECT id AS Id, version AS Version, digest AS Digest, metadata_json AS MetadataJson, dependency_groups_json AS DependencyGroupsJson, origin AS Origin, extraction AS Extraction, extraction_detail AS ExtractionDetail, closure_json AS ClosureJson, withdrawn AS Withdrawn, withdrawn_at_utc AS WithdrawnAtUtc, withdrawn_by AS WithdrawnBy, registered_at_utc AS RegisteredAtUtc, registered_by AS RegisteredBy, published_draft_generation AS PublishedDraftGenerationValue, published_draft_revision AS PublishedDraftRevision, published_draft_name AS PublishedDraftName, published_draft_version AS PublishedDraftVersion, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, lifecycle_revision AS LifecycleRevision, catalog_revision AS CatalogRevision FROM ixiflow_packages WHERE id=@Id AND (@AfterVersion IS NULL OR version>@AfterVersion) ORDER BY version LIMIT @Limit",
        Statement.CatalogPackageInsert =>
            "INSERT INTO ixiflow_packages (id, version, digest, metadata_json, dependency_groups_json, origin, extraction, extraction_detail, closure_json, withdrawn, withdrawn_at_utc, withdrawn_by, registered_at_utc, registered_by, published_draft_generation, published_draft_revision, published_draft_name, published_draft_version, published_at_utc, published_by, lifecycle_revision, catalog_revision) VALUES (@Id, @Version, @Digest, @MetadataJson, @DependencyGroupsJson, @Origin, @Extraction, @ExtractionDetail, @ClosureJson, @Withdrawn, @WithdrawnAtUtc, @WithdrawnBy, @RegisteredAtUtc, @RegisteredBy, @PublishedDraftGeneration, @PublishedDraftRevision, @PublishedDraftName, @PublishedDraftVersion, @PublishedAtUtc, @PublishedBy, @LifecycleRevision, @CatalogRevision)",
        Statement.CatalogLock => null,
        Statement.CatalogSharedLock => null,
        Statement.CatalogInstanceDigests => "SELECT DISTINCT bundle_digest FROM ixiflow_workflow_instances WHERE bundle_digest IS NOT NULL",
        Statement.CatalogRevision => "SELECT revision FROM ixiflow_catalog_revision WHERE id=1",
        Statement.CatalogRevisionAdvance =>
            "UPDATE ixiflow_catalog_revision SET revision=CASE WHEN revision < 9223372036854775807 THEN revision+1 ELSE NULL END WHERE id=1",
        Statement.CatalogVersionChanges => """
                                           SELECT display_name AS DisplayName, category AS Category, tags AS Tags, search_text AS SearchText, descriptor_json AS DescriptorJson, contract_hash AS ContractHash, name AS Name, version AS Version, structure_hash AS StructureHash, artifact_digest AS ArtifactDigest, catalog_revision AS CatalogRevision, metadata_json AS MetadataJson, state AS State, published_draft_generation AS PublishedDraftGenerationValue, published_draft_revision AS PublishedDraftRevision, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, state_changed_at_utc AS StateChangedAtUtc, state_changed_by AS StateChangedBy, lifecycle_revision AS LifecycleRevision FROM ixiflow_workflow_versions WHERE catalog_revision>@AfterRevision
                                           """,
        Statement.CatalogVersions => """
                                     SELECT display_name AS DisplayName, category AS Category, tags AS Tags, search_text AS SearchText, descriptor_json AS DescriptorJson, contract_hash AS ContractHash, name AS Name, version AS Version, structure_hash AS StructureHash, artifact_digest AS ArtifactDigest, catalog_revision AS CatalogRevision, metadata_json AS MetadataJson, state AS State, published_draft_generation AS PublishedDraftGenerationValue, published_draft_revision AS PublishedDraftRevision, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, state_changed_at_utc AS StateChangedAtUtc, state_changed_by AS StateChangedBy, lifecycle_revision AS LifecycleRevision FROM ixiflow_workflow_versions
                                     """,
        Statement.CatalogNamedVersions => """
                                          SELECT display_name AS DisplayName, category AS Category, tags AS Tags, search_text AS SearchText, descriptor_json AS DescriptorJson, contract_hash AS ContractHash, name AS Name, version AS Version, structure_hash AS StructureHash, artifact_digest AS ArtifactDigest, catalog_revision AS CatalogRevision, metadata_json AS MetadataJson, state AS State, published_draft_generation AS PublishedDraftGenerationValue, published_draft_revision AS PublishedDraftRevision, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, state_changed_at_utc AS StateChangedAtUtc, state_changed_by AS StateChangedBy, lifecycle_revision AS LifecycleRevision FROM ixiflow_workflow_versions WHERE name=@Name
                                          """,
        Statement.CatalogVersion => """
                                    SELECT display_name AS DisplayName, category AS Category, tags AS Tags, search_text AS SearchText, descriptor_json AS DescriptorJson, contract_hash AS ContractHash, name AS Name, version AS Version, structure_hash AS StructureHash, artifact_digest AS ArtifactDigest, catalog_revision AS CatalogRevision, metadata_json AS MetadataJson, state AS State, published_draft_generation AS PublishedDraftGenerationValue, published_draft_revision AS PublishedDraftRevision, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, state_changed_at_utc AS StateChangedAtUtc, state_changed_by AS StateChangedBy, lifecycle_revision AS LifecycleRevision FROM ixiflow_workflow_versions WHERE name=@Name AND version=@Version
                                    """,
        Statement.CatalogUnfinishedCallers => """
                                              SELECT json_extract(caller.value, '$.Name') AS Name, json_extract(caller.value, '$.Version') AS Version
                                              FROM json_each(@Callers) AS caller
                                              WHERE EXISTS (
                                                  SELECT 1 FROM ixiflow_workflow_instances AS instance
                                                  WHERE instance.workflow_name = json_extract(caller.value, '$.Name')
                                                      AND instance.workflow_version = json_extract(caller.value, '$.Version')
                                                      AND instance.status NOT IN ('Completed','Failed','Cancelled','Terminated','TimedOut'))
                                              LIMIT 1
                                              """,
        Statement.CatalogVersionInsert => """
                                          INSERT INTO ixiflow_workflow_versions (name, version, structure_hash, state, artifact_digest, metadata_json, catalog_revision, published_draft_generation, published_draft_revision, published_at_utc, published_by, state_changed_at_utc, state_changed_by, lifecycle_revision, display_name, category, tags, search_text, descriptor_json, contract_hash) VALUES (@Name, @Version, @StructureHash, @State, @ArtifactDigest, @MetadataJson, @CatalogRevision, @PublishedDraftGeneration, @PublishedDraftRevision, @PublishedAtUtc, @PublishedBy, @StateChangedAtUtc, @StateChangedBy, @LifecycleRevision, @DisplayName, @Category, @Tags, @SearchText, @DescriptorJson, @ContractHash)
                                          """,
        Statement.CatalogVersionState => """
                                         UPDATE ixiflow_workflow_versions SET state=@State, metadata_json=@MetadataJson, catalog_revision=@CatalogRevision, lifecycle_revision=@LifecycleRevision, state_changed_at_utc=@StateChangedAtUtc, state_changed_by=@StateChangedBy WHERE name=@Name AND version=@Version AND lifecycle_revision=@ExpectedRevision
                                         """,
        Statement.CatalogDefinition => """
                                       SELECT name AS Name, version AS Version, kind AS Kind, source AS Source, revision AS Revision, generation AS GenerationValue, source_length AS SourceLength, updated_at_utc AS UpdatedAtUtc, updated_by AS UpdatedBy FROM ixiflow_definitions WHERE kind=@Kind AND name=@Name AND version=@Version
                                       """,
        Statement.CatalogDefinitionInsert => """
                                             INSERT INTO ixiflow_definitions (name, version, kind, source, revision, generation, source_length, updated_at_utc, updated_by) VALUES (@Name, @Version, @Kind, @Source, @Revision, @Generation, @SourceLength, @UpdatedAtUtc, @UpdatedBy)
                                             """,
        Statement.CatalogDefinitionUpdate => """
                                             UPDATE ixiflow_definitions SET source=@Source, source_length=@SourceLength, revision=@Revision, updated_at_utc=@UpdatedAtUtc, updated_by=@UpdatedBy WHERE kind=@Kind AND name=@Name AND version=@Version AND generation=@Generation AND revision=@ExpectedRevision
                                             """,
        Statement.CatalogDefinitionDelete => """
                                             DELETE FROM ixiflow_definitions WHERE kind=@Kind AND name=@Name AND version=@Version AND generation=@Generation AND revision=@Revision
                                             """,
        Statement.ArtifactPut => """
                                 INSERT INTO ixiflow_artifacts (digest, kind, content, created_at_utc) VALUES (@Digest,@Kind,@Content,@Now) ON CONFLICT (digest) DO UPDATE SET created_at_utc=excluded.created_at_utc
                                 """,
        Statement.ArtifactRead => """
                                  SELECT content FROM ixiflow_artifacts WHERE digest=@Digest
                                  """,
        Statement.ArtifactExists => """
                                    SELECT COUNT(*) FROM ixiflow_artifacts WHERE digest=@Digest
                                    """,
        Statement.ArtifactDelete => """
                                    DELETE FROM ixiflow_artifacts WHERE digest=@Digest
                                    """,
        Statement.ArtifactDeleteOld => """
                                       DELETE FROM ixiflow_artifacts WHERE digest=@Digest AND created_at_utc < @Cutoff
                                       """,
        Statement.ArtifactCandidates => """
                                        SELECT digest FROM ixiflow_artifacts WHERE created_at_utc < @Cutoff AND digest NOT IN (SELECT value FROM json_each(@References)) ORDER BY created_at_utc, digest LIMIT @Limit
                                        """,
        Statement.DraftGenerationInsert => """
                                           INSERT INTO ixiflow_draft_generations (generation, kind, name, version) VALUES (@Generation, @Kind, @Name, @Version) ON CONFLICT (generation) DO NOTHING
                                           """,
        Statement.CatalogDefinitionPage => """
                                           SELECT name AS Name, version AS Version, kind AS Kind, revision AS Revision, generation AS GenerationValue, source_length AS SourceLength, updated_at_utc AS UpdatedAtUtc, updated_by AS UpdatedBy FROM ixiflow_definitions WHERE ({scope}) AND (@Kind IS NULL OR kind=@Kind) AND (@NamePrefix IS NULL OR SUBSTRING(name,1,@PrefixLength)=@NamePrefix) AND (@AfterKind IS NULL OR kind>@AfterKind OR (kind=@AfterKind AND (name>@AfterName OR (name=@AfterName AND version>@AfterVersion)))) ORDER BY kind, name, version LIMIT @Limit
                                           """,
        Statement.PublicationHead => """
                                     SELECT active_id AS ActiveIdValue, last_attempt AS LastAttempt FROM ixiflow_publication_heads WHERE kind=@Kind AND name=@Name AND version=@Version AND generation=@Generation AND revision=@Revision
                                     """,
        Statement.PublicationHeadInsert => """
                                           INSERT INTO ixiflow_publication_heads (kind, name, version, generation, revision, active_id, last_attempt) VALUES (@Kind, @Name, @Version, @Generation, @Revision, @ActiveId, @LastAttempt)
                                           """,
        Statement.PublicationHeadUpdate => """
                                           UPDATE ixiflow_publication_heads SET active_id=@ActiveId, last_attempt=@LastAttempt WHERE kind=@Kind AND name=@Name AND version=@Version AND generation=@Generation AND revision=@Revision
                                           """,
        Statement.Publication => """
                                 SELECT id AS IdValue, kind AS Kind, name AS Name, version AS Version, generation AS GenerationValue, revision AS Revision, attempt AS Attempt, state AS State, host_id AS HostIdValue, started_by AS StartedBy, started_at_utc AS StartedAtUtc, heartbeat_at_utc AS HeartbeatAtUtc, finished_at_utc AS FinishedAtUtc, outcome_json AS OutcomeJson, catalog_revision AS CatalogRevision FROM ixiflow_publication_attempts WHERE id=@Id
                                 """,
        Statement.PublicationInsert => """
                                       INSERT INTO ixiflow_publication_attempts (id, kind, name, version, generation, revision, attempt, state, host_id, started_by, started_at_utc, heartbeat_at_utc, finished_at_utc, outcome_json, catalog_revision) VALUES (@Id, @Kind, @Name, @Version, @Generation, @Revision, @Attempt, @State, @HostId, @StartedBy, @StartedAtUtc, @HeartbeatAtUtc, @FinishedAtUtc, @OutcomeJson, @CatalogRevision)
                                       """,
        Statement.PublicationUpdate => """
                                       UPDATE ixiflow_publication_attempts SET state=@State, finished_at_utc=@FinishedAtUtc, outcome_json=@OutcomeJson, catalog_revision=@CatalogRevision WHERE id=@Id AND host_id=@HostId AND state='Running'
                                       """,
        Statement.PublicationHeartbeat => """
                                          UPDATE ixiflow_publication_attempts SET heartbeat_at_utc=@Now, catalog_revision=@CatalogRevision WHERE id=@Id AND host_id=@HostId AND state='Running'
                                          """,
        Statement.LatestPublication => """
                                       SELECT id AS IdValue, kind AS Kind, name AS Name, version AS Version, generation AS GenerationValue, revision AS Revision, attempt AS Attempt, state AS State, host_id AS HostIdValue, started_by AS StartedBy, started_at_utc AS StartedAtUtc, heartbeat_at_utc AS HeartbeatAtUtc, finished_at_utc AS FinishedAtUtc, outcome_json AS OutcomeJson, catalog_revision AS CatalogRevision FROM ixiflow_publication_attempts WHERE kind=@Kind AND name=@Name AND version=@Version AND generation=@Generation ORDER BY started_at_utc DESC, revision DESC, attempt DESC LIMIT 1
                                       """,
        Statement.PublicationPage => """
                                     SELECT id AS IdValue, kind AS Kind, name AS Name, version AS Version, generation AS GenerationValue, revision AS Revision, attempt AS Attempt, state AS State, host_id AS HostIdValue, started_by AS StartedBy, started_at_utc AS StartedAtUtc, heartbeat_at_utc AS HeartbeatAtUtc, finished_at_utc AS FinishedAtUtc, outcome_json AS OutcomeJson, catalog_revision AS CatalogRevision FROM ixiflow_publication_attempts WHERE kind=@Kind AND name=@Name AND version=@Version AND (@Generation IS NULL OR generation=@Generation) AND (@AfterTime IS NULL OR started_at_utc>@AfterTime OR (started_at_utc=@AfterTime AND id>@AfterId)) ORDER BY started_at_utc, id LIMIT @Limit
                                     """,
        Statement.PublicationPurgeCandidates => """
                                                SELECT id AS Id FROM ixiflow_publication_attempts WHERE state<>'Running' AND finished_at_utc<@Cutoff ORDER BY finished_at_utc, id LIMIT @Limit
                                                """,
        Statement.PublicationDelete => """
                                       DELETE FROM ixiflow_publication_attempts WHERE id=@Id AND state<>'Running'
                                       """,
        _ => null
    };
}
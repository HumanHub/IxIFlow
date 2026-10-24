namespace IxIFlow.Core;

public sealed partial class PostgreSqlWorkflowStateRepository
{
    private const string CatalogSchema = """
        CREATE TABLE ixiflow_publication_heads (
            kind text COLLATE "C" NOT NULL CHECK (kind IN ('workflow','activity','template')),
            name text COLLATE "C" NOT NULL,
            version INT NOT NULL,
            generation uuid NOT NULL,
            revision BIGINT NOT NULL,
            active_id uuid NULL,
            last_attempt INT NOT NULL,
            PRIMARY KEY (kind, name, version, generation, revision)
        );
        CREATE TABLE ixiflow_publication_attempts (
            id uuid NOT NULL,
            kind text COLLATE "C" NOT NULL CHECK (kind IN ('workflow','activity','template')),
            name text COLLATE "C" NOT NULL,
            version INT NOT NULL,
            generation uuid NOT NULL,
            revision BIGINT NOT NULL,
            attempt INT NOT NULL,
            state text NOT NULL CHECK (state IN ('Running','Published','Failed','Abandoned')),
            host_id uuid NOT NULL,
            started_by text COLLATE "C" NULL,
            started_at_utc timestamptz NOT NULL,
            heartbeat_at_utc timestamptz NOT NULL,
            finished_at_utc timestamptz NULL,
            outcome_json text NULL,
            catalog_revision BIGINT NOT NULL,
            PRIMARY KEY (id)
        );
        CREATE UNIQUE INDEX ix_publication_attempt_identity ON ixiflow_publication_attempts (kind, name, version, generation, revision, attempt);
        CREATE INDEX ix_publication_history ON ixiflow_publication_attempts (kind, name, version, started_at_utc, id);
        CREATE INDEX ix_publication_finished ON ixiflow_publication_attempts (finished_at_utc);
        CREATE TABLE ixiflow_definitions (
            kind text COLLATE "C" NOT NULL CHECK (kind IN ('workflow','activity','template')),
            name text COLLATE "C" NOT NULL,
            version INT NOT NULL,
            generation uuid NOT NULL,
            revision BIGINT NOT NULL,
            source text NOT NULL,
            source_length INT NOT NULL,
            updated_at_utc timestamptz NOT NULL,
            updated_by text COLLATE "C" NULL,
            PRIMARY KEY (kind, name, version)
        );
        CREATE TABLE ixiflow_draft_generations (
            generation uuid NOT NULL,
            kind text COLLATE "C" NOT NULL CHECK (kind IN ('workflow','activity','template')),
            name text COLLATE "C" NOT NULL,
            version INT NOT NULL,
            PRIMARY KEY (generation)
        );
        CREATE TABLE ixiflow_catalog_revision (
            id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
            revision BIGINT NOT NULL CHECK (revision >= 0)
        );
        INSERT INTO ixiflow_catalog_revision (id, revision) VALUES (1, 0);
        CREATE TABLE ixiflow_workflow_versions (
            name text COLLATE "C" NOT NULL,
            version integer NOT NULL,
            structure_hash text NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('Draft','Published','Deprecated','Retired')),
            catalog_revision BIGINT NOT NULL,
            published_draft_generation uuid NULL,
            published_draft_revision BIGINT NULL,
            published_at_utc timestamptz NULL,
            published_by text COLLATE "C" NULL,
            state_changed_at_utc timestamptz NULL,
            state_changed_by text COLLATE "C" NULL,
            lifecycle_revision BIGINT NOT NULL,
            artifact_digest text COLLATE "C" NULL,
            metadata_json text NOT NULL,
            PRIMARY KEY (name, version)
        );
        CREATE INDEX ix_workflow_versions_revision ON ixiflow_workflow_versions (catalog_revision);
        CREATE TABLE ixiflow_packages (
            id text COLLATE "C" NOT NULL,
            version text COLLATE "C" NOT NULL,
            dependency_groups_json text NOT NULL,
            digest text COLLATE "C" NOT NULL,
            metadata_json text NOT NULL,
            origin varchar(10) COLLATE "C" NOT NULL CHECK (origin IN ('Resolved','Uploaded','Published')),
            extraction varchar(10) COLLATE "C" NOT NULL CHECK (extraction IN ('Indexed','Pending','Unloadable')),
            extraction_detail varchar(400) COLLATE "C" NULL,
            closure_json text COLLATE "C" NULL,
            withdrawn boolean NOT NULL,
            withdrawn_at_utc timestamptz NULL,
            withdrawn_by varchar(200) COLLATE "C" NULL,
            registered_at_utc timestamptz NOT NULL,
            registered_by varchar(200) COLLATE "C" NULL,
            published_draft_generation uuid NULL,
            published_draft_revision bigint NULL,
            published_draft_name varchar(200) COLLATE "C" NULL,
            published_draft_version int NULL,
            published_at_utc timestamptz NULL,
            published_by varchar(200) COLLATE "C" NULL,
            lifecycle_revision bigint NOT NULL,
            catalog_revision bigint NOT NULL,
            PRIMARY KEY (id, version)
        );
        CREATE INDEX ix_packages_origin ON ixiflow_packages (origin, id, version);
        CREATE TABLE ixiflow_package_activities (
            package_id varchar(100) COLLATE "C" NOT NULL,
            package_version varchar(100) COLLATE "C" NOT NULL,
            "key" varchar(100) COLLATE "C" NOT NULL,
            version varchar(50) COLLATE "C" NOT NULL,
            metadata_type_name text COLLATE "C" NOT NULL,
            csharp_type_name text COLLATE "C" NOT NULL,
            display_name varchar(200) COLLATE "C" NOT NULL,
            category varchar(100) COLLATE "C" NOT NULL,
            icon varchar(100) COLLATE "C" NULL,
            search_text varchar(400) COLLATE "C" NOT NULL,
            contract_hash varchar(64) COLLATE "C" NOT NULL,
            descriptor_json text COLLATE "C" NOT NULL,
            catalog_revision bigint NOT NULL,
            PRIMARY KEY (package_id, package_version, "key")
        );
        CREATE INDEX ix_package_activities_key ON ixiflow_package_activities ("key", version, package_id, package_version);
        CREATE INDEX ix_package_activities_category ON ixiflow_package_activities (category, "key");
        CREATE TABLE ixiflow_installed_activities (
            package_id varchar(100) COLLATE "C" NOT NULL,
            package_version varchar(100) COLLATE "C" NOT NULL,
            "key" varchar(100) COLLATE "C" NOT NULL,
            version varchar(50) COLLATE "C" NOT NULL,
            metadata_type_name text COLLATE "C" NOT NULL,
            csharp_type_name text COLLATE "C" NOT NULL,
            display_name varchar(200) COLLATE "C" NOT NULL,
            category varchar(100) COLLATE "C" NOT NULL,
            icon varchar(100) COLLATE "C" NULL,
            search_text varchar(400) COLLATE "C" NOT NULL,
            contract_hash varchar(64) COLLATE "C" NOT NULL,
            descriptor_json text COLLATE "C" NOT NULL,
            catalog_revision bigint NOT NULL,
            fingerprint varchar(64) COLLATE "C" NOT NULL,
            PRIMARY KEY (package_id, package_version, fingerprint, "key")
        );
        CREATE INDEX ix_installed_activities_key ON ixiflow_installed_activities ("key", version, package_id, package_version);
        CREATE INDEX ix_installed_activities_category ON ixiflow_installed_activities (category, "key");
        CREATE TABLE ixiflow_package_pending_dependencies (
            package_id varchar(100) COLLATE "C" NOT NULL,
            package_version varchar(100) COLLATE "C" NOT NULL,
            missing_package_id varchar(100) COLLATE "C" NOT NULL,
            PRIMARY KEY (package_id, package_version, missing_package_id)
        );
        CREATE INDEX ix_package_pending_reverse ON ixiflow_package_pending_dependencies (missing_package_id, package_id, package_version);
        CREATE TABLE ixiflow_artifacts (
            digest text COLLATE "C" NOT NULL PRIMARY KEY,
            kind TEXT NOT NULL,
            content bytea NOT NULL,
            created_at_utc timestamptz NOT NULL
        );
        CREATE INDEX ix_artifacts_created ON ixiflow_artifacts (created_at_utc);
        """;

    internal override object ArtifactReferencesParameter(IReadOnlySet<ArtifactDigest> references) =>
        references.Select(digest => digest.Value).ToArray();

    private static string? CatalogSql(Statement statement) => statement switch
    {
        Statement.CatalogPackages => "SELECT id AS Id, version AS Version, digest AS Digest, metadata_json AS MetadataJson, dependency_groups_json AS DependencyGroupsJson, origin AS Origin, extraction AS Extraction, extraction_detail AS ExtractionDetail, closure_json AS ClosureJson, withdrawn AS Withdrawn, withdrawn_at_utc AS WithdrawnAtUtc, withdrawn_by AS WithdrawnBy, registered_at_utc AS RegisteredAtUtc, registered_by AS RegisteredBy, published_draft_generation AS PublishedDraftGeneration, published_draft_revision AS PublishedDraftRevision, published_draft_name AS PublishedDraftName, published_draft_version AS PublishedDraftVersion, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, lifecycle_revision AS LifecycleRevision, catalog_revision AS CatalogRevision FROM ixiflow_packages WHERE id=COALESCE(@Id,id)",
        Statement.CatalogPackage => "SELECT id AS Id, version AS Version, digest AS Digest, metadata_json AS MetadataJson, dependency_groups_json AS DependencyGroupsJson, origin AS Origin, extraction AS Extraction, extraction_detail AS ExtractionDetail, closure_json AS ClosureJson, withdrawn AS Withdrawn, withdrawn_at_utc AS WithdrawnAtUtc, withdrawn_by AS WithdrawnBy, registered_at_utc AS RegisteredAtUtc, registered_by AS RegisteredBy, published_draft_generation AS PublishedDraftGeneration, published_draft_revision AS PublishedDraftRevision, published_draft_name AS PublishedDraftName, published_draft_version AS PublishedDraftVersion, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, lifecycle_revision AS LifecycleRevision, catalog_revision AS CatalogRevision FROM ixiflow_packages WHERE id=@Id AND version=@Version",
        Statement.CatalogPackagePage => "SELECT id AS Id, version AS Version, digest AS Digest, metadata_json AS MetadataJson, dependency_groups_json AS DependencyGroupsJson, origin AS Origin, extraction AS Extraction, extraction_detail AS ExtractionDetail, closure_json AS ClosureJson, withdrawn AS Withdrawn, withdrawn_at_utc AS WithdrawnAtUtc, withdrawn_by AS WithdrawnBy, registered_at_utc AS RegisteredAtUtc, registered_by AS RegisteredBy, published_draft_generation AS PublishedDraftGeneration, published_draft_revision AS PublishedDraftRevision, published_draft_name AS PublishedDraftName, published_draft_version AS PublishedDraftVersion, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, lifecycle_revision AS LifecycleRevision, catalog_revision AS CatalogRevision FROM ixiflow_packages WHERE id=@Id AND (@AfterVersion IS NULL OR version>@AfterVersion) ORDER BY version LIMIT @Limit",
        Statement.CatalogPackageInsert => "INSERT INTO ixiflow_packages (id, version, digest, metadata_json, dependency_groups_json, origin, extraction, extraction_detail, closure_json, withdrawn, withdrawn_at_utc, withdrawn_by, registered_at_utc, registered_by, published_draft_generation, published_draft_revision, published_draft_name, published_draft_version, published_at_utc, published_by, lifecycle_revision, catalog_revision) VALUES (@Id, @Version, @Digest, @MetadataJson, @DependencyGroupsJson, @Origin, @Extraction, @ExtractionDetail, @ClosureJson, @Withdrawn, @WithdrawnAtUtc, @WithdrawnBy, @RegisteredAtUtc, @RegisteredBy, @PublishedDraftGeneration, @PublishedDraftRevision, @PublishedDraftName, @PublishedDraftVersion, @PublishedAtUtc, @PublishedBy, @LifecycleRevision, @CatalogRevision)",
        Statement.CatalogLock => """
            SELECT pg_advisory_xact_lock(hashtext('IxIFlow.Catalog'))
            """,
        Statement.CatalogSharedLock => """
            SELECT pg_advisory_xact_lock_shared(hashtext('IxIFlow.Catalog'))
            """,
        Statement.CatalogContentLock => """
            SELECT pg_advisory_xact_lock(hashtext('IxIFlow.CatalogContent'))
            """,
        Statement.CatalogInstanceDigests => "SELECT DISTINCT bundle_digest FROM ixiflow_workflow_instances WHERE bundle_digest IS NOT NULL",
        Statement.CatalogRevision => "SELECT revision FROM ixiflow_catalog_revision WHERE id=1",
        Statement.CatalogRevisionAdvance => "UPDATE ixiflow_catalog_revision SET revision=revision+1 WHERE id=1",
        Statement.CatalogVersionChanges => """
            SELECT name AS Name, version AS Version, structure_hash AS StructureHash, artifact_digest AS ArtifactDigest, catalog_revision AS CatalogRevision, metadata_json AS MetadataJson, state AS State, published_draft_generation AS PublishedDraftGeneration, published_draft_revision AS PublishedDraftRevision, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, state_changed_at_utc AS StateChangedAtUtc, state_changed_by AS StateChangedBy, lifecycle_revision AS LifecycleRevision FROM ixiflow_workflow_versions WHERE catalog_revision>@AfterRevision
            """,
        Statement.CatalogVersions => """
            SELECT name AS Name, version AS Version, structure_hash AS StructureHash, artifact_digest AS ArtifactDigest, catalog_revision AS CatalogRevision, metadata_json AS MetadataJson, state AS State, published_draft_generation AS PublishedDraftGeneration, published_draft_revision AS PublishedDraftRevision, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, state_changed_at_utc AS StateChangedAtUtc, state_changed_by AS StateChangedBy, lifecycle_revision AS LifecycleRevision FROM ixiflow_workflow_versions
            """,
        Statement.CatalogNamedVersions => """
            SELECT name AS Name, version AS Version, structure_hash AS StructureHash, artifact_digest AS ArtifactDigest, catalog_revision AS CatalogRevision, metadata_json AS MetadataJson, state AS State, published_draft_generation AS PublishedDraftGeneration, published_draft_revision AS PublishedDraftRevision, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, state_changed_at_utc AS StateChangedAtUtc, state_changed_by AS StateChangedBy, lifecycle_revision AS LifecycleRevision FROM ixiflow_workflow_versions WHERE name=@Name
            """,
        Statement.CatalogVersion => """
            SELECT name AS Name, version AS Version, structure_hash AS StructureHash, artifact_digest AS ArtifactDigest, catalog_revision AS CatalogRevision, metadata_json AS MetadataJson, state AS State, published_draft_generation AS PublishedDraftGeneration, published_draft_revision AS PublishedDraftRevision, published_at_utc AS PublishedAtUtc, published_by AS PublishedBy, state_changed_at_utc AS StateChangedAtUtc, state_changed_by AS StateChangedBy, lifecycle_revision AS LifecycleRevision FROM ixiflow_workflow_versions WHERE name=@Name AND version=@Version
            """,
        Statement.CatalogUnfinishedCallers => """
            SELECT caller."Name" AS Name, caller."Version" AS Version
            FROM jsonb_to_recordset(CAST(@Callers AS jsonb)) AS caller("Name" text, "Version" integer)
            WHERE EXISTS (
                SELECT 1 FROM ixiflow_workflow_instances AS instance
                WHERE instance.workflow_name = caller."Name"
                    AND instance.workflow_version = caller."Version"
                    AND instance.status NOT IN ('Completed','Failed','Cancelled','Terminated','TimedOut'))
            LIMIT 1
            """,
        Statement.CatalogVersionInsert => """
            INSERT INTO ixiflow_workflow_versions (name, version, structure_hash, state, artifact_digest, metadata_json, catalog_revision, published_draft_generation, published_draft_revision, published_at_utc, published_by, state_changed_at_utc, state_changed_by, lifecycle_revision) VALUES (@Name, @Version, @StructureHash, @State, @ArtifactDigest, @MetadataJson, @CatalogRevision, @PublishedDraftGeneration, @PublishedDraftRevision, @PublishedAtUtc, @PublishedBy, @StateChangedAtUtc, @StateChangedBy, @LifecycleRevision)
            """,
        Statement.CatalogVersionState => """
            UPDATE ixiflow_workflow_versions SET state=@State, metadata_json=@MetadataJson, catalog_revision=@CatalogRevision, lifecycle_revision=@LifecycleRevision, state_changed_at_utc=@StateChangedAtUtc, state_changed_by=@StateChangedBy WHERE name=@Name AND version=@Version AND lifecycle_revision=@ExpectedRevision
            """,
        Statement.CatalogDefinition => """
            SELECT name AS Name, version AS Version, kind AS Kind, source AS Source, revision AS Revision, generation AS Generation, source_length AS SourceLength, updated_at_utc AS UpdatedAtUtc, updated_by AS UpdatedBy FROM ixiflow_definitions WHERE kind=@Kind AND name=@Name AND version=@Version
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
            SELECT digest FROM ixiflow_artifacts WHERE created_at_utc < @Cutoff AND NOT (digest = ANY(@References)) ORDER BY created_at_utc, digest LIMIT @Limit
            """,
        Statement.DraftGenerationInsert => """
            INSERT INTO ixiflow_draft_generations (generation, kind, name, version) VALUES (@Generation, @Kind, @Name, @Version) ON CONFLICT (generation) DO NOTHING
            """,
        Statement.CatalogDefinitionPage => """
            SELECT name AS Name, version AS Version, kind AS Kind, revision AS Revision, generation AS Generation, source_length AS SourceLength, updated_at_utc AS UpdatedAtUtc, updated_by AS UpdatedBy FROM ixiflow_definitions WHERE ({scope}) AND (@Kind IS NULL OR kind=@Kind) AND (@NamePrefix IS NULL OR SUBSTRING(name,1,@PrefixLength)=@NamePrefix) AND (@AfterKind IS NULL OR kind>@AfterKind OR (kind=@AfterKind AND (name>@AfterName OR (name=@AfterName AND version>@AfterVersion)))) ORDER BY kind, name, version LIMIT @Limit
            """,
        Statement.PublicationHead => """
            SELECT active_id AS ActiveId, last_attempt AS LastAttempt FROM ixiflow_publication_heads WHERE kind=@Kind AND name=@Name AND version=@Version AND generation=@Generation AND revision=@Revision
            """,
        Statement.PublicationHeadInsert => """
            INSERT INTO ixiflow_publication_heads (kind, name, version, generation, revision, active_id, last_attempt) VALUES (@Kind, @Name, @Version, @Generation, @Revision, @ActiveId, @LastAttempt)
            """,
        Statement.PublicationHeadUpdate => """
            UPDATE ixiflow_publication_heads SET active_id=@ActiveId, last_attempt=@LastAttempt WHERE kind=@Kind AND name=@Name AND version=@Version AND generation=@Generation AND revision=@Revision
            """,
        Statement.Publication => """
            SELECT id AS Id, kind AS Kind, name AS Name, version AS Version, generation AS Generation, revision AS Revision, attempt AS Attempt, state AS State, host_id AS HostId, started_by AS StartedBy, started_at_utc AS StartedAtUtc, heartbeat_at_utc AS HeartbeatAtUtc, finished_at_utc AS FinishedAtUtc, outcome_json AS OutcomeJson, catalog_revision AS CatalogRevision FROM ixiflow_publication_attempts WHERE id=@Id
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
        Statement.PublicationPage => """
            SELECT id AS Id, kind AS Kind, name AS Name, version AS Version, generation AS Generation, revision AS Revision, attempt AS Attempt, state AS State, host_id AS HostId, started_by AS StartedBy, started_at_utc AS StartedAtUtc, heartbeat_at_utc AS HeartbeatAtUtc, finished_at_utc AS FinishedAtUtc, outcome_json AS OutcomeJson, catalog_revision AS CatalogRevision FROM ixiflow_publication_attempts WHERE kind=@Kind AND name=@Name AND version=@Version AND (@Generation IS NULL OR generation=@Generation) AND (@AfterTime IS NULL OR started_at_utc>@AfterTime OR (started_at_utc=@AfterTime AND id>@AfterId)) ORDER BY started_at_utc, id LIMIT @Limit
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

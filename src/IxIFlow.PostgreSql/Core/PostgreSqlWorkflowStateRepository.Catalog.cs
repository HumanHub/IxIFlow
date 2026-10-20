namespace IxIFlow.Core;

public sealed partial class PostgreSqlWorkflowStateRepository
{
    private const string CatalogSchema = """
        CREATE TABLE ixiflow_definitions (
            name text COLLATE "C" NOT NULL,
            version integer NOT NULL,
            kind text NOT NULL,
            source text NOT NULL,
            revision BIGINT NOT NULL,
            state TEXT NOT NULL CHECK (state = 'Draft'),
            PRIMARY KEY (name, version)
        );
        CREATE TABLE ixiflow_workflow_versions (
            name text COLLATE "C" NOT NULL,
            version integer NOT NULL,
            structure_hash text NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('Draft','Published','Deprecated','Retired')),
            artifact_digest text COLLATE "C" NULL,
            metadata_json text NOT NULL,
            PRIMARY KEY (name, version)
        );
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
        Statement.CatalogLock => """
            SELECT pg_advisory_xact_lock(hashtext('IxIFlow.Catalog'))
            """,
        Statement.CatalogSharedLock => """
            SELECT pg_advisory_xact_lock_shared(hashtext('IxIFlow.Catalog'))
            """,
        Statement.CatalogVersions => """
            SELECT metadata_json AS MetadataJson, state AS State FROM ixiflow_workflow_versions
            """,
        Statement.CatalogNamedVersions => """
            SELECT metadata_json AS MetadataJson, state AS State FROM ixiflow_workflow_versions WHERE name=@Name
            """,
        Statement.CatalogVersion => """
            SELECT metadata_json AS MetadataJson, state AS State FROM ixiflow_workflow_versions WHERE name=@Name AND version=@Version
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
            INSERT INTO ixiflow_workflow_versions (name, version, structure_hash, state, artifact_digest, metadata_json) VALUES (@Name, @Version, @StructureHash, @State, @ArtifactDigest, @MetadataJson)
            """,
        Statement.CatalogVersionState => """
            UPDATE ixiflow_workflow_versions SET state=@State WHERE name=@Name AND version=@Version
            """,
        Statement.CatalogDefinition => """
            SELECT name AS Name, version AS Version, kind AS Kind, source AS Source, revision AS Revision FROM ixiflow_definitions WHERE name=@Name AND version=@Version
            """,
        Statement.CatalogDefinitionInsert => """
            INSERT INTO ixiflow_definitions (name, version, kind, source, revision, state) VALUES (@Name, @Version, @Kind, @Source, @Revision, 'Draft') ON CONFLICT (name, version) DO NOTHING
            """,
        Statement.CatalogDefinitionUpdate => """
            UPDATE ixiflow_definitions SET kind=@Kind, source=@Source, revision=@Revision WHERE name=@Name AND version=@Version AND revision=@ExpectedRevision
            """,
        Statement.CatalogDefinitionDelete => """
            DELETE FROM ixiflow_definitions WHERE name=@Name AND version=@Version AND revision=@Revision
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
        _ => null
    };
}

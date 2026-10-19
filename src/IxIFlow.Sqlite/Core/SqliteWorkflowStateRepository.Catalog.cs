namespace IxIFlow.Core;

public sealed partial class SqliteWorkflowStateRepository
{
    private const string CatalogSchema = """
        CREATE TABLE ixiflow_definitions (
            name TEXT COLLATE BINARY NOT NULL,
            version integer NOT NULL,
            kind TEXT NOT NULL,
            source TEXT NOT NULL,
            revision BIGINT NOT NULL,
            state TEXT NOT NULL CHECK (state = 'Draft'),
            PRIMARY KEY (name, version)
        );
        CREATE TABLE ixiflow_workflow_versions (
            name TEXT COLLATE BINARY NOT NULL,
            version integer NOT NULL,
            structure_hash TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('Draft','Published','Deprecated','Retired')),
            artifact_digest TEXT COLLATE BINARY NULL,
            metadata_json TEXT NOT NULL,
            PRIMARY KEY (name, version)
        );
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
        Statement.CatalogLock => null,
        Statement.CatalogSharedLock => null,
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
            SELECT digest FROM ixiflow_artifacts WHERE created_at_utc < @Cutoff AND digest NOT IN (SELECT value FROM json_each(@References)) ORDER BY created_at_utc, digest LIMIT @Limit
            """,
        _ => null
    };
}

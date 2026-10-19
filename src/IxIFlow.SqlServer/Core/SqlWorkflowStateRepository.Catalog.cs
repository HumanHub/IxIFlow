namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    private const string CatalogSchema = """
        CREATE TABLE dbo.IxIFlowDefinitions (
            Name NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Version INT NOT NULL,
            Kind NVARCHAR(MAX) NOT NULL,
            Source NVARCHAR(MAX) NOT NULL,
            Revision BIGINT NOT NULL,
            State VARCHAR(10) NOT NULL CHECK (State = 'Draft'),
            PRIMARY KEY (Name, Version)
        );
        CREATE TABLE dbo.IxIFlowWorkflowVersions (
            Name NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Version INT NOT NULL,
            StructureHash NVARCHAR(MAX) NOT NULL,
            State VARCHAR(10) NOT NULL CHECK (State IN ('Draft','Published','Deprecated','Retired')),
            ArtifactDigest VARCHAR(71) COLLATE Latin1_General_100_BIN2 NULL,
            MetadataJson NVARCHAR(MAX) NOT NULL,
            PRIMARY KEY (Name, Version)
        );
        CREATE TABLE dbo.IxIFlowArtifacts (
            Digest VARCHAR(71) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            Kind VARCHAR(10) NOT NULL,
            Content VARBINARY(MAX) NOT NULL,
            CreatedAtUtc DATETIME2 NOT NULL
        );
        CREATE INDEX ix_artifacts_created ON dbo.IxIFlowArtifacts (CreatedAtUtc);
        """;

    private static string? CatalogSql(Statement statement) => statement switch
    {
        Statement.CatalogLock => """
            DECLARE @catalogLock INT; EXEC @catalogLock = sys.sp_getapplock @Resource=N'IxIFlow.Catalog', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000; IF @catalogLock < 0 THROW 51000, 'Could not lock the workflow catalog', 1;
            """,
        Statement.CatalogSharedLock => """
            DECLARE @catalogLock INT; EXEC @catalogLock = sys.sp_getapplock @Resource=N'IxIFlow.Catalog', @LockMode=N'Shared', @LockOwner=N'Transaction', @LockTimeout=30000; IF @catalogLock < 0 THROW 51000, 'Could not lock the workflow catalog', 1;
            """,
        Statement.CatalogVersions => """
            SELECT MetadataJson AS MetadataJson, State AS State FROM dbo.IxIFlowWorkflowVersions
            """,
        Statement.CatalogNamedVersions => """
            SELECT MetadataJson AS MetadataJson, State AS State FROM dbo.IxIFlowWorkflowVersions WHERE Name=@Name
            """,
        Statement.CatalogVersion => """
            SELECT MetadataJson AS MetadataJson, State AS State FROM dbo.IxIFlowWorkflowVersions WHERE Name=@Name AND Version=@Version
            """,
        Statement.CatalogUnfinishedCallers => """
            SELECT TOP (1) caller.Name, caller.Version
            FROM OPENJSON(@Callers) WITH (Name nvarchar(200), Version int) AS caller
            WHERE EXISTS (
                SELECT 1 FROM dbo.IxIFlowWorkflowInstances AS instance
                -- Seek in the column's collation, then enforce ordinal name identity.
                WHERE instance.WorkflowName = caller.Name
                    AND instance.WorkflowVersion = caller.Version
                    AND instance.WorkflowName = caller.Name COLLATE Latin1_General_100_BIN2
                    AND instance.Status NOT IN ('Completed','Failed','Cancelled','Terminated','TimedOut'))
            """,
        Statement.CatalogVersionInsert => """
            INSERT INTO dbo.IxIFlowWorkflowVersions (Name, Version, StructureHash, State, ArtifactDigest, MetadataJson) VALUES (@Name, @Version, @StructureHash, @State, @ArtifactDigest, @MetadataJson)
            """,
        Statement.CatalogVersionState => """
            UPDATE dbo.IxIFlowWorkflowVersions SET State=@State WHERE Name=@Name AND Version=@Version
            """,
        Statement.CatalogDefinition => """
            SELECT Name AS Name, Version AS Version, Kind AS Kind, Source AS Source, Revision AS Revision FROM dbo.IxIFlowDefinitions WHERE Name=@Name AND Version=@Version
            """,
        Statement.CatalogDefinitionInsert => """
            INSERT INTO dbo.IxIFlowDefinitions (Name, Version, Kind, Source, Revision, State) SELECT @Name, @Version, @Kind, @Source, @Revision, 'Draft' WHERE NOT EXISTS (SELECT 1 FROM dbo.IxIFlowDefinitions WITH (UPDLOCK,HOLDLOCK) WHERE Name=@Name AND Version=@Version)
            """,
        Statement.CatalogDefinitionUpdate => """
            UPDATE dbo.IxIFlowDefinitions SET Kind=@Kind, Source=@Source, Revision=@Revision WHERE Name=@Name AND Version=@Version AND Revision=@ExpectedRevision
            """,
        Statement.CatalogDefinitionDelete => """
            DELETE FROM dbo.IxIFlowDefinitions WHERE Name=@Name AND Version=@Version AND Revision=@Revision
            """,
        Statement.ArtifactPut => """
            MERGE dbo.IxIFlowArtifacts WITH (HOLDLOCK) AS target
            USING (SELECT @Digest AS Digest) AS source ON target.Digest=source.Digest
            WHEN MATCHED THEN UPDATE SET CreatedAtUtc=@Now
            WHEN NOT MATCHED THEN INSERT (Digest, Kind, Content, CreatedAtUtc) VALUES (@Digest,@Kind,@Content,@Now);
            """,
        Statement.ArtifactRead => """
            SELECT Content FROM dbo.IxIFlowArtifacts WHERE Digest=@Digest
            """,
        Statement.ArtifactExists => """
            SELECT COUNT(*) FROM dbo.IxIFlowArtifacts WHERE Digest=@Digest
            """,
        Statement.ArtifactDelete => """
            DELETE FROM dbo.IxIFlowArtifacts WHERE Digest=@Digest
            """,
        Statement.ArtifactDeleteOld => """
            DELETE FROM dbo.IxIFlowArtifacts WHERE Digest=@Digest AND CreatedAtUtc < @Cutoff
            """,
        Statement.ArtifactCandidates => """
            SELECT TOP (@Limit) Digest FROM dbo.IxIFlowArtifacts WHERE CreatedAtUtc < @Cutoff AND Digest NOT IN (SELECT value FROM OPENJSON(@References)) ORDER BY CreatedAtUtc, Digest
            """,
        _ => null
    };
}

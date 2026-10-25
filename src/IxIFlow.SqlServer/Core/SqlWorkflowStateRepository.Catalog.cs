namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    // Name uses non-SC Latin1_General_100_BIN2: SUBSTRING counts UTF-16 code units,
    // including both halves of a supplementary character, just like string.Length.
    internal override bool CatalogSqlServer => true;
    internal override int CatalogPrefixLength(string value) => value.Length;

    private const string CatalogSchema = """
        CREATE TABLE dbo.IxIFlowPublicationHeads (
            Kind VARCHAR(10) COLLATE Latin1_General_100_BIN2 NOT NULL CHECK (Kind IN ('workflow','activity','template')),
            Name NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Version INT NOT NULL,
            Generation UNIQUEIDENTIFIER NOT NULL,
            Revision BIGINT NOT NULL,
            ActiveId UNIQUEIDENTIFIER NULL,
            LastAttempt INT NOT NULL,
            PRIMARY KEY (Kind, Name, Version, Generation, Revision)
        );
        CREATE TABLE dbo.IxIFlowPublicationAttempts (
            Id UNIQUEIDENTIFIER NOT NULL,
            IdOrder AS (LOWER(CONVERT(VARCHAR(36), Id)) COLLATE Latin1_General_100_BIN2) PERSISTED,
            Kind VARCHAR(10) COLLATE Latin1_General_100_BIN2 NOT NULL CHECK (Kind IN ('workflow','activity','template')),
            Name NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Version INT NOT NULL,
            Generation UNIQUEIDENTIFIER NOT NULL,
            Revision BIGINT NOT NULL,
            Attempt INT NOT NULL,
            State VARCHAR(10) NOT NULL CHECK (State IN ('Running','Published','Failed','Abandoned')),
            HostId UNIQUEIDENTIFIER NOT NULL,
            StartedBy NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL,
            StartedAtUtc DATETIME2 NOT NULL,
            HeartbeatAtUtc DATETIME2 NOT NULL,
            FinishedAtUtc DATETIME2 NULL,
            OutcomeJson NVARCHAR(MAX) NULL,
            CatalogRevision BIGINT NOT NULL,
            PRIMARY KEY (Id)
        );
        CREATE UNIQUE INDEX ix_publication_attempt_identity ON dbo.IxIFlowPublicationAttempts (Kind, Name, Version, Generation, Revision, Attempt);
        CREATE INDEX ix_publication_history ON dbo.IxIFlowPublicationAttempts (Kind, Name, Version, StartedAtUtc, IdOrder);
        CREATE INDEX ix_publication_finished ON dbo.IxIFlowPublicationAttempts (FinishedAtUtc);
        CREATE TABLE dbo.IxIFlowDefinitions (
            Kind VARCHAR(10) COLLATE Latin1_General_100_BIN2 NOT NULL CHECK (Kind IN ('workflow','activity','template')),
            Name NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            -- Encode surrogate pairs as scalars and fix the VARCHAR result's code page before taking bytes.
            NameOrder AS (CONVERT(VARBINARY(800), CONVERT(VARCHAR(800), Name COLLATE Latin1_General_100_CI_AS_SC_UTF8) COLLATE Latin1_General_100_CI_AS_SC_UTF8)) PERSISTED,
            Version INT NOT NULL,
            Generation UNIQUEIDENTIFIER NOT NULL,
            Revision BIGINT NOT NULL,
            Source NVARCHAR(MAX) NOT NULL,
            SourceLength INT NOT NULL,
            UpdatedAtUtc DATETIME2 NOT NULL,
            UpdatedBy NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL,
            PRIMARY KEY (Kind, Name, Version)
        );
        CREATE INDEX ix_definitions_page ON dbo.IxIFlowDefinitions (Kind, NameOrder, Version)
            INCLUDE (Name, Generation, Revision, SourceLength, UpdatedAtUtc, UpdatedBy);
        CREATE TABLE dbo.IxIFlowDraftGenerations (
            Generation UNIQUEIDENTIFIER NOT NULL,
            Kind VARCHAR(10) COLLATE Latin1_General_100_BIN2 NOT NULL CHECK (Kind IN ('workflow','activity','template')),
            Name NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Version INT NOT NULL,
            PRIMARY KEY (Generation)
        );
        CREATE TABLE dbo.IxIFlowCatalogRevision (
            Id INT NOT NULL PRIMARY KEY CHECK (Id = 1),
            Revision BIGINT NOT NULL CHECK (Revision >= 0)
        );
        INSERT INTO dbo.IxIFlowCatalogRevision (Id, Revision) VALUES (1, 0);
        CREATE TABLE dbo.IxIFlowWorkflowVersions (
            Name NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Version INT NOT NULL,
            StructureHash NVARCHAR(MAX) NOT NULL,
            State VARCHAR(10) NOT NULL CHECK (State IN ('Draft','Published','Deprecated','Retired')),
            CatalogRevision BIGINT NOT NULL,
            PublishedDraftGeneration UNIQUEIDENTIFIER NULL,
            PublishedDraftRevision BIGINT NULL,
            PublishedAtUtc DATETIME2 NULL,
            PublishedBy NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL,
            StateChangedAtUtc DATETIME2 NULL,
            StateChangedBy NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL,
            LifecycleRevision BIGINT NOT NULL,
            ArtifactDigest VARCHAR(71) COLLATE Latin1_General_100_BIN2 NULL,
            DisplayName NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Category NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL,
            Tags NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
            SearchText NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
            DescriptorJson NVARCHAR(MAX) NOT NULL,
            ContractHash VARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            MetadataJson NVARCHAR(MAX) NOT NULL,
            PRIMARY KEY (Name, Version)
        );
        CREATE INDEX ix_workflow_versions_revision ON dbo.IxIFlowWorkflowVersions (CatalogRevision);
                CREATE INDEX ix_workflow_versions_category ON dbo.IxIFlowWorkflowVersions (Category, Name);
        CREATE TABLE dbo.IxIFlowTemplateVersions (
            [Key] nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Version] int NOT NULL,
            [Engine] nvarchar(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Format] varchar(10) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [ModelType] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [ModelTypeId] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [ModelPackagesJson] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [CulturesJson] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [DisplayName] nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
            [Description] nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL,
            [SearchText] nvarchar(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [PublishedDraftGeneration] uniqueidentifier NULL,
            [PublishedDraftRevision] bigint NULL,
            [PublishedAtUtc] datetime2 NOT NULL,
            [PublishedBy] nvarchar(200) COLLATE Latin1_General_100_BIN2 NULL,
            [MetadataJson] nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [CatalogRevision] bigint NOT NULL,
            PRIMARY KEY ([Key], [Version])
        );
        CREATE TABLE dbo.IxIFlowPackages (
            Id NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Version NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Digest VARCHAR(71) COLLATE Latin1_General_100_BIN2 NOT NULL,
            DependencyGroupsJson NVARCHAR(MAX) NOT NULL,
            MetadataJson NVARCHAR(MAX) NOT NULL,
            [Origin] VARCHAR(10) COLLATE Latin1_General_100_BIN2 NOT NULL CHECK ([Origin] IN ('Resolved','Uploaded','Published')),
            [Extraction] VARCHAR(10) COLLATE Latin1_General_100_BIN2 NOT NULL CHECK ([Extraction] IN ('Indexed','Pending','Unloadable')),
            [ExtractionDetail] NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NULL,
            [ClosureJson] NVARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NULL,
            [Withdrawn] BIT NOT NULL,
            [WithdrawnAtUtc] DATETIME2 NULL,
            [WithdrawnBy] NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL,
            [RegisteredAtUtc] DATETIME2 NOT NULL,
            [RegisteredBy] NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL,
            [PublishedDraftGeneration] UNIQUEIDENTIFIER NULL,
            [PublishedDraftRevision] BIGINT NULL,
            [PublishedDraftName] NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL,
            [PublishedDraftVersion] INT NULL,
            [PublishedAtUtc] DATETIME2 NULL,
            [PublishedBy] NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NULL,
            [LifecycleRevision] BIGINT NOT NULL,
            [CatalogRevision] BIGINT NOT NULL,
            PRIMARY KEY (Id, Version)
        );
        CREATE INDEX ix_packages_origin ON dbo.IxIFlowPackages ([Origin], [Id], [Version]);
        CREATE TABLE dbo.IxIFlowPackageActivities (
            [PackageId] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [PackageVersion] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Key] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Version] NVARCHAR(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [MetadataTypeName] NVARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [CSharpTypeName] NVARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [DisplayName] NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Category] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Icon] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL,
            [SearchText] NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [ContractHash] VARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [DescriptorJson] NVARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [CatalogRevision] BIGINT NOT NULL,
            PRIMARY KEY ([PackageId], [PackageVersion], [Key])
        );
        CREATE INDEX ix_package_activities_key ON dbo.IxIFlowPackageActivities ([Key], [Version], [PackageId], [PackageVersion]);
        CREATE INDEX ix_package_activities_category ON dbo.IxIFlowPackageActivities ([Category], [Key]);
        CREATE TABLE dbo.IxIFlowInstalledActivities (
            [PackageId] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [PackageVersion] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Key] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Version] NVARCHAR(50) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [MetadataTypeName] NVARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [CSharpTypeName] NVARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [DisplayName] NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Category] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Icon] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NULL,
            [SearchText] NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [ContractHash] VARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [DescriptorJson] NVARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [CatalogRevision] BIGINT NOT NULL,
            [Fingerprint] VARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PRIMARY KEY ([PackageId], [PackageVersion], [Fingerprint], [Key])
        );
        CREATE INDEX ix_installed_activities_key ON dbo.IxIFlowInstalledActivities ([Key], [Version], [PackageId], [PackageVersion]);
        CREATE INDEX ix_installed_activities_category ON dbo.IxIFlowInstalledActivities ([Category], [Key]);
        CREATE TABLE dbo.IxIFlowPackagePendingDependencies (
            [PackageId] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [PackageVersion] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [MissingPackageId] NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PRIMARY KEY ([PackageId], [PackageVersion], [MissingPackageId])
        );
        CREATE INDEX ix_package_pending_reverse ON dbo.IxIFlowPackagePendingDependencies ([MissingPackageId], [PackageId], [PackageVersion]);
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
        Statement.CatalogPackages => "SELECT Id, Version, Digest, MetadataJson, DependencyGroupsJson, [Origin], [Extraction], [ExtractionDetail], [ClosureJson], [Withdrawn], [WithdrawnAtUtc], [WithdrawnBy], [RegisteredAtUtc], [RegisteredBy], [PublishedDraftGeneration], [PublishedDraftRevision], [PublishedDraftName], [PublishedDraftVersion], [PublishedAtUtc], [PublishedBy], [LifecycleRevision], [CatalogRevision] FROM dbo.IxIFlowPackages WHERE Id=COALESCE(@Id,Id)",
        Statement.CatalogPackage => "SELECT Id, Version, Digest, MetadataJson, DependencyGroupsJson, [Origin], [Extraction], [ExtractionDetail], [ClosureJson], [Withdrawn], [WithdrawnAtUtc], [WithdrawnBy], [RegisteredAtUtc], [RegisteredBy], [PublishedDraftGeneration], [PublishedDraftRevision], [PublishedDraftName], [PublishedDraftVersion], [PublishedAtUtc], [PublishedBy], [LifecycleRevision], [CatalogRevision] FROM dbo.IxIFlowPackages WHERE Id=@Id AND Version=@Version",
        Statement.CatalogPackagePage => "SELECT TOP (@Limit) Id, Version, Digest, MetadataJson, DependencyGroupsJson, [Origin], [Extraction], [ExtractionDetail], [ClosureJson], [Withdrawn], [WithdrawnAtUtc], [WithdrawnBy], [RegisteredAtUtc], [RegisteredBy], [PublishedDraftGeneration], [PublishedDraftRevision], [PublishedDraftName], [PublishedDraftVersion], [PublishedAtUtc], [PublishedBy], [LifecycleRevision], [CatalogRevision] FROM dbo.IxIFlowPackages WHERE Id=@Id AND (@AfterVersion IS NULL OR Version>@AfterVersion) ORDER BY Version",
        Statement.CatalogPackageInsert => "INSERT INTO dbo.IxIFlowPackages (Id, Version, Digest, MetadataJson, DependencyGroupsJson, [Origin], [Extraction], [ExtractionDetail], [ClosureJson], [Withdrawn], [WithdrawnAtUtc], [WithdrawnBy], [RegisteredAtUtc], [RegisteredBy], [PublishedDraftGeneration], [PublishedDraftRevision], [PublishedDraftName], [PublishedDraftVersion], [PublishedAtUtc], [PublishedBy], [LifecycleRevision], [CatalogRevision]) VALUES (@Id, @Version, @Digest, @MetadataJson, @DependencyGroupsJson, @Origin, @Extraction, @ExtractionDetail, @ClosureJson, @Withdrawn, @WithdrawnAtUtc, @WithdrawnBy, @RegisteredAtUtc, @RegisteredBy, @PublishedDraftGeneration, @PublishedDraftRevision, @PublishedDraftName, @PublishedDraftVersion, @PublishedAtUtc, @PublishedBy, @LifecycleRevision, @CatalogRevision)",
        Statement.CatalogLock => """
            DECLARE @catalogLock INT; EXEC @catalogLock = sys.sp_getapplock @Resource=N'IxIFlow.Catalog', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000; IF @catalogLock < 0 THROW 51000, 'Could not lock the workflow catalog', 1;
            """,
        Statement.CatalogSharedLock => """
            DECLARE @catalogLock INT; EXEC @catalogLock = sys.sp_getapplock @Resource=N'IxIFlow.Catalog', @LockMode=N'Shared', @LockOwner=N'Transaction', @LockTimeout=30000; IF @catalogLock < 0 THROW 51000, 'Could not lock the workflow catalog', 1;
            """,
        Statement.CatalogContentLock => """
            DECLARE @contentLock INT; EXEC @contentLock = sys.sp_getapplock @Resource=N'IxIFlow.CatalogContent', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000; IF @contentLock < 0 THROW 51000, 'Could not lock the catalog content', 1;
            """,
        Statement.CatalogInstanceDigests => "SELECT DISTINCT BundleDigest FROM dbo.IxIFlowWorkflowInstances WHERE BundleDigest IS NOT NULL",
        Statement.CatalogRevision => "SELECT Revision FROM dbo.IxIFlowCatalogRevision WHERE Id=1",
        Statement.CatalogRevisionAdvance => "UPDATE dbo.IxIFlowCatalogRevision SET Revision=Revision+1 WHERE Id=1",
        Statement.CatalogVersionChanges => """
            SELECT DisplayName AS DisplayName, Category AS Category, Tags AS Tags, SearchText AS SearchText, DescriptorJson AS DescriptorJson, ContractHash AS ContractHash, Name AS Name, Version AS Version, StructureHash AS StructureHash, ArtifactDigest AS ArtifactDigest, CatalogRevision AS CatalogRevision, MetadataJson AS MetadataJson, State AS State, PublishedDraftGeneration AS PublishedDraftGeneration, PublishedDraftRevision AS PublishedDraftRevision, PublishedAtUtc AS PublishedAtUtc, PublishedBy AS PublishedBy, StateChangedAtUtc AS StateChangedAtUtc, StateChangedBy AS StateChangedBy, LifecycleRevision AS LifecycleRevision FROM dbo.IxIFlowWorkflowVersions WHERE CatalogRevision>@AfterRevision
            """,
        Statement.CatalogVersions => """
            SELECT DisplayName AS DisplayName, Category AS Category, Tags AS Tags, SearchText AS SearchText, DescriptorJson AS DescriptorJson, ContractHash AS ContractHash, Name AS Name, Version AS Version, StructureHash AS StructureHash, ArtifactDigest AS ArtifactDigest, CatalogRevision AS CatalogRevision, MetadataJson AS MetadataJson, State AS State, PublishedDraftGeneration AS PublishedDraftGeneration, PublishedDraftRevision AS PublishedDraftRevision, PublishedAtUtc AS PublishedAtUtc, PublishedBy AS PublishedBy, StateChangedAtUtc AS StateChangedAtUtc, StateChangedBy AS StateChangedBy, LifecycleRevision AS LifecycleRevision FROM dbo.IxIFlowWorkflowVersions
            """,
        Statement.CatalogNamedVersions => """
            SELECT DisplayName AS DisplayName, Category AS Category, Tags AS Tags, SearchText AS SearchText, DescriptorJson AS DescriptorJson, ContractHash AS ContractHash, Name AS Name, Version AS Version, StructureHash AS StructureHash, ArtifactDigest AS ArtifactDigest, CatalogRevision AS CatalogRevision, MetadataJson AS MetadataJson, State AS State, PublishedDraftGeneration AS PublishedDraftGeneration, PublishedDraftRevision AS PublishedDraftRevision, PublishedAtUtc AS PublishedAtUtc, PublishedBy AS PublishedBy, StateChangedAtUtc AS StateChangedAtUtc, StateChangedBy AS StateChangedBy, LifecycleRevision AS LifecycleRevision FROM dbo.IxIFlowWorkflowVersions WHERE Name=@Name
            """,
        Statement.CatalogVersion => """
            SELECT DisplayName AS DisplayName, Category AS Category, Tags AS Tags, SearchText AS SearchText, DescriptorJson AS DescriptorJson, ContractHash AS ContractHash, Name AS Name, Version AS Version, StructureHash AS StructureHash, ArtifactDigest AS ArtifactDigest, CatalogRevision AS CatalogRevision, MetadataJson AS MetadataJson, State AS State, PublishedDraftGeneration AS PublishedDraftGeneration, PublishedDraftRevision AS PublishedDraftRevision, PublishedAtUtc AS PublishedAtUtc, PublishedBy AS PublishedBy, StateChangedAtUtc AS StateChangedAtUtc, StateChangedBy AS StateChangedBy, LifecycleRevision AS LifecycleRevision FROM dbo.IxIFlowWorkflowVersions WHERE Name=@Name AND Version=@Version
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
            INSERT INTO dbo.IxIFlowWorkflowVersions (Name, Version, StructureHash, State, ArtifactDigest, MetadataJson, CatalogRevision, PublishedDraftGeneration, PublishedDraftRevision, PublishedAtUtc, PublishedBy, StateChangedAtUtc, StateChangedBy, LifecycleRevision, DisplayName, Category, Tags, SearchText, DescriptorJson, ContractHash) VALUES (@Name, @Version, @StructureHash, @State, @ArtifactDigest, @MetadataJson, @CatalogRevision, @PublishedDraftGeneration, @PublishedDraftRevision, @PublishedAtUtc, @PublishedBy, @StateChangedAtUtc, @StateChangedBy, @LifecycleRevision, @DisplayName, @Category, @Tags, @SearchText, @DescriptorJson, @ContractHash)
            """,
        Statement.CatalogVersionState => """
            UPDATE dbo.IxIFlowWorkflowVersions SET State=@State, MetadataJson=@MetadataJson, CatalogRevision=@CatalogRevision, LifecycleRevision=@LifecycleRevision, StateChangedAtUtc=@StateChangedAtUtc, StateChangedBy=@StateChangedBy WHERE Name=@Name AND Version=@Version AND LifecycleRevision=@ExpectedRevision
            """,
        Statement.CatalogDefinition => """
            SELECT Name AS Name, Version AS Version, Kind AS Kind, Source AS Source, Revision AS Revision, Generation AS Generation, SourceLength AS SourceLength, UpdatedAtUtc AS UpdatedAtUtc, UpdatedBy AS UpdatedBy FROM dbo.IxIFlowDefinitions WHERE Kind=@Kind AND Name=@Name AND Version=@Version
            """,
        Statement.CatalogDefinitionInsert => """
            INSERT INTO dbo.IxIFlowDefinitions (Name, Version, Kind, Source, Revision, Generation, SourceLength, UpdatedAtUtc, UpdatedBy) VALUES (@Name, @Version, @Kind, @Source, @Revision, @Generation, @SourceLength, @UpdatedAtUtc, @UpdatedBy)
            """,
        Statement.CatalogDefinitionUpdate => """
            UPDATE dbo.IxIFlowDefinitions SET Source=@Source, SourceLength=@SourceLength, Revision=@Revision, UpdatedAtUtc=@UpdatedAtUtc, UpdatedBy=@UpdatedBy WHERE Kind=@Kind AND Name=@Name AND Version=@Version AND Generation=@Generation AND Revision=@ExpectedRevision
            """,
        Statement.CatalogDefinitionDelete => """
            DELETE FROM dbo.IxIFlowDefinitions WHERE Kind=@Kind AND Name=@Name AND Version=@Version AND Generation=@Generation AND Revision=@Revision
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
        Statement.DraftGenerationInsert => """
            INSERT INTO dbo.IxIFlowDraftGenerations (Generation, Kind, Name, Version) SELECT @Generation, @Kind, @Name, @Version WHERE NOT EXISTS (SELECT 1 FROM dbo.IxIFlowDraftGenerations WHERE Generation=@Generation)
            """,
        // Explicit lower bounds permit a composite seek; recompilation removes absent optional filters.
        // Convert parameters to the exact key types, leaving indexed column operands untouched.
        // SQL string equality pads trailing spaces even with BIN2; compare byte lengths too.
        Statement.CatalogDefinitionPage => """
            SELECT TOP (@Limit) Name AS Name, Version AS Version, Kind AS Kind, Revision AS Revision, Generation AS Generation, SourceLength AS SourceLength, UpdatedAtUtc AS UpdatedAtUtc, UpdatedBy AS UpdatedBy
            FROM dbo.IxIFlowDefinitions
            WHERE ({scope}) AND (@Kind IS NULL OR Kind=CONVERT(VARCHAR(10),@Kind))
              AND (@NamePrefix IS NULL OR (SUBSTRING(Name,1,@PrefixLength)=@NamePrefix AND DATALENGTH(SUBSTRING(Name,1,@PrefixLength))=DATALENGTH(@NamePrefix)))
              AND (@AfterKind IS NULL OR Kind>=CONVERT(VARCHAR(10),@AfterKind))
              AND (@AfterKind IS NULL OR Kind<>CONVERT(VARCHAR(10),@AfterKind) OR NameOrder>=CONVERT(VARBINARY(800),@AfterNameOrder))
              AND (@AfterKind IS NULL OR Kind>CONVERT(VARCHAR(10),@AfterKind) OR (Kind=CONVERT(VARCHAR(10),@AfterKind) AND (NameOrder>CONVERT(VARBINARY(800),@AfterNameOrder) OR (NameOrder=CONVERT(VARBINARY(800),@AfterNameOrder) AND Version>@AfterVersion))))
            ORDER BY Kind, NameOrder, Version
            OPTION (RECOMPILE)
            """,
        Statement.PublicationHead => """
            SELECT ActiveId AS ActiveId, LastAttempt AS LastAttempt FROM dbo.IxIFlowPublicationHeads WHERE Kind=@Kind AND Name=@Name AND Version=@Version AND Generation=@Generation AND Revision=@Revision
            """,
        Statement.PublicationHeadInsert => """
            INSERT INTO dbo.IxIFlowPublicationHeads (Kind, Name, Version, Generation, Revision, ActiveId, LastAttempt) VALUES (@Kind, @Name, @Version, @Generation, @Revision, @ActiveId, @LastAttempt)
            """,
        Statement.PublicationHeadUpdate => """
            UPDATE dbo.IxIFlowPublicationHeads SET ActiveId=@ActiveId, LastAttempt=@LastAttempt WHERE Kind=@Kind AND Name=@Name AND Version=@Version AND Generation=@Generation AND Revision=@Revision
            """,
        Statement.Publication => """
            SELECT Id AS Id, Kind AS Kind, Name AS Name, Version AS Version, Generation AS Generation, Revision AS Revision, Attempt AS Attempt, State AS State, HostId AS HostId, StartedBy AS StartedBy, StartedAtUtc AS StartedAtUtc, HeartbeatAtUtc AS HeartbeatAtUtc, FinishedAtUtc AS FinishedAtUtc, OutcomeJson AS OutcomeJson, CatalogRevision AS CatalogRevision FROM dbo.IxIFlowPublicationAttempts WHERE Id=@Id
            """,
        Statement.PublicationInsert => """
            INSERT INTO dbo.IxIFlowPublicationAttempts (Id, Kind, Name, Version, Generation, Revision, Attempt, State, HostId, StartedBy, StartedAtUtc, HeartbeatAtUtc, FinishedAtUtc, OutcomeJson, CatalogRevision) VALUES (@Id, @Kind, @Name, @Version, @Generation, @Revision, @Attempt, @State, @HostId, @StartedBy, @StartedAtUtc, @HeartbeatAtUtc, @FinishedAtUtc, @OutcomeJson, @CatalogRevision)
            """,
        Statement.PublicationUpdate => """
            UPDATE dbo.IxIFlowPublicationAttempts SET State=@State, FinishedAtUtc=@FinishedAtUtc, OutcomeJson=@OutcomeJson, CatalogRevision=@CatalogRevision WHERE Id=@Id AND HostId=@HostId AND State='Running'
            """,
        Statement.PublicationHeartbeat => """
            UPDATE dbo.IxIFlowPublicationAttempts SET HeartbeatAtUtc=@Now, CatalogRevision=@CatalogRevision WHERE Id=@Id AND HostId=@HostId AND State='Running'
            """,
        Statement.PublicationPage => """
            SELECT TOP (@Limit) Id AS Id, Kind AS Kind, Name AS Name, Version AS Version, Generation AS Generation, Revision AS Revision, Attempt AS Attempt, State AS State, HostId AS HostId, StartedBy AS StartedBy, StartedAtUtc AS StartedAtUtc, HeartbeatAtUtc AS HeartbeatAtUtc, FinishedAtUtc AS FinishedAtUtc, OutcomeJson AS OutcomeJson, CatalogRevision AS CatalogRevision
            FROM dbo.IxIFlowPublicationAttempts
            WHERE Kind=@Kind AND Name=@Name AND Version=@Version AND (@Generation IS NULL OR Generation=@Generation)
              AND (@AfterTime IS NULL OR StartedAtUtc>=@AfterTime)
              AND (@AfterTime IS NULL OR StartedAtUtc>@AfterTime OR (StartedAtUtc=@AfterTime AND IdOrder>@AfterIdOrder))
            ORDER BY StartedAtUtc, IdOrder
            OPTION (RECOMPILE)
            """,
        Statement.PublicationPurgeCandidates => """
            SELECT TOP (@Limit) Id AS Id FROM dbo.IxIFlowPublicationAttempts WHERE State<>'Running' AND FinishedAtUtc<@Cutoff ORDER BY FinishedAtUtc, Id
            """,
        Statement.PublicationDelete => """
            DELETE FROM dbo.IxIFlowPublicationAttempts WHERE Id=@Id AND State<>'Running'
            """,
        _ => null
    };
}

using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;

namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    // Zero-row reads check every required table and column without changing an existing database.
    private static async Task ValidateCurrentSchemaAsync(DbConnection connection, DbTransaction transaction)
    {
        try
        {
            using var schema = await connection.QueryMultipleAsync("""
                SELECT [PackageId], [PackageVersion], [Key], [Version], [MetadataTypeName], [CSharpTypeName], [DisplayName], [Category], [Icon], [SearchText], [ContractHash], [DescriptorJson], [CatalogRevision] FROM dbo.IxIFlowPackageActivities WHERE 1=0;
                SELECT [PackageId], [PackageVersion], [Key], [Version], [MetadataTypeName], [CSharpTypeName], [DisplayName], [Category], [Icon], [SearchText], [ContractHash], [DescriptorJson], [CatalogRevision], [Fingerprint] FROM dbo.IxIFlowInstalledActivities WHERE 1=0;
                SELECT [PackageId], [PackageVersion], [MissingPackageId] FROM dbo.IxIFlowPackagePendingDependencies WHERE 1=0;
                SELECT Id, IdOrder, Kind, Name, Version, Generation, Revision, Attempt, State, HostId, StartedBy, StartedAtUtc, HeartbeatAtUtc, FinishedAtUtc, OutcomeJson, CatalogRevision FROM dbo.IxIFlowPublicationAttempts WHERE 1=0;
                SELECT Kind, Name, Version, Generation, Revision, ActiveId, LastAttempt FROM dbo.IxIFlowPublicationHeads WHERE 1=0;
                SELECT Generation, Kind, Name, Version FROM dbo.IxIFlowDraftGenerations WHERE 1=0;
                SELECT Id, Version, Fingerprint, CatalogRevision FROM dbo.IxIFlowInstalledPackages WHERE 1=0;
                SELECT MemberId, PackageId, PackageVersion, Fingerprint FROM dbo.IxIFlowHostPackages WHERE 1=0;
                SELECT InstanceId, CommandId, CommandOrder, CommandJson FROM dbo.IxIFlowWorkflowDebugCommands WHERE 1=0;
                SELECT Digest, Bytes, Body, MetadataJson FROM dbo.IxIFlowWorkflowPayloads WHERE 1=0;
                SELECT Digest, OwnerKind, OwnerId FROM dbo.IxIFlowWorkflowPayloadRefs WHERE 1=0;
                SELECT Name, NameOrder, Version, Kind, Source, Revision, Generation, SourceLength, UpdatedAtUtc, UpdatedBy FROM dbo.IxIFlowDefinitions WHERE 1=0;
                SELECT Id, Revision FROM dbo.IxIFlowCatalogRevision WHERE 1=0;
                SELECT Name, Version, StructureHash, State, ArtifactDigest, MetadataJson, CatalogRevision, PublishedDraftGeneration, PublishedDraftRevision, PublishedAtUtc, PublishedBy, StateChangedAtUtc, StateChangedBy, LifecycleRevision FROM dbo.IxIFlowWorkflowVersions WHERE 1=0;
                SELECT Digest, Kind, Content, CreatedAtUtc FROM dbo.IxIFlowArtifacts WHERE 1=0;
                SELECT [Id], [Version], [Digest], [MetadataJson], [DependencyGroupsJson], [Origin], [Extraction], [ExtractionDetail], [ClosureJson], [Withdrawn], [WithdrawnAtUtc], [WithdrawnBy], [RegisteredAtUtc], [RegisteredBy], [PublishedDraftGeneration], [PublishedDraftRevision], [PublishedDraftName], [PublishedDraftVersion], [PublishedAtUtc], [PublishedBy], [LifecycleRevision], [CatalogRevision] FROM dbo.IxIFlowPackages WHERE 1=0;
                SELECT [InstanceId], [WorkflowName], [WorkflowVersion], [BundleDigest], [CreatedAtUtc], [StopKind], [StopCrashCount], [Status], [CorrelationId], [RunnableAtUtc], [EndedAtUtc], [StateJson], [CancellationJson], [CancellationAcknowledged], [Revision], [InboxSequence], [InboxReadThrough], [Incarnation], [ParentInstanceId], [ParentWaitId], [LastHostId], [DemandSetId], [PinnedHost], [PinExpiresAtUtc], [PinInherit] FROM dbo.IxIFlowWorkflowInstances WHERE 1=0;
                SELECT [InstanceId], [EventId], [Sequence], [EventType], [EventKey], [TargetWaitId], [Subject], [StoredPayloadJson], [PayloadHash], [ReceivedAtUtc], [Outcome], [WaitId], [Rejection], [SettledAtUtc], [NoticeJson] FROM dbo.IxIFlowWorkflowEvents WHERE 1=0;
                SELECT [InstanceId], [PartKey], [Json] FROM dbo.IxIFlowWorkflowParts WHERE 1=0;
                SELECT [InstanceId], [Sequence], [EntryJson], [Revision], [CommitEndSequence], [CommitStatus] FROM dbo.IxIFlowWorkflowTraces WHERE 1=0;
                SELECT InstanceId, DiagnosticId, DiagnosticJson FROM dbo.IxIFlowWorkflowDiagnostics WHERE 1=0;
                SELECT [InstanceId], [WaitId], [WaitRef], [Kind], [WaitKey], [EventType], [DefaultPayloadJson], [DeadlineUtc], [OpenedSequence], [OpenedAtUtc], [CloseReason], [ClosedAtUtc] FROM dbo.IxIFlowWorkflowWaits WHERE 1=0;
                SELECT [InstanceId], [CommitId], [ExpectedRevision], [Revision], [PayloadHash], [NoticeJson], [MadeRunnableJson] FROM dbo.IxIFlowWorkflowCommits WHERE 1=0;
                SELECT [InstanceId], [Token], [ExpiresAtUtc], [Epoch], [CrashCount], [HostId] FROM dbo.IxIFlowWorkflowLeases WHERE 1=0;
                SELECT [Name], [TypesJson], [DisabledAtUtc], [EnabledAtUtc], [EnabledBy], [UnregisteredAtUtc], [UnregisteredBy], [LastError] FROM dbo.IxIFlowOutboxPublishers WHERE 1=0;
                SELECT [EventId], [Incarnation], [InstanceId], [EventType], [EventJson], [OccurredAtUtc] FROM dbo.IxIFlowOutboxEvents WHERE 1=0;
                SELECT [EventId], [PublisherName], [Attempts], [FirstAttemptAtUtc], [GivenUpReason], [RetriedAtUtc], [RetriedBy], [NextAttemptAtUtc], [Status], [LastError], [LeaseToken], [LeaseUntilUtc] FROM dbo.IxIFlowOutboxDeliveries WHERE 1=0;
                SELECT [InstanceId], [RemovedAtUtc], [ExpiresAtUtc] FROM dbo.IxIFlowWorkflowTombstones WHERE 1=0;
                SELECT [Incarnation], [Sender], [Trigger], [Key], [Fingerprint], [InstanceId], [WorkflowName], [WorkflowVersion], [ExpiresAtUtc], [NoticeJson] FROM dbo.IxIFlowWorkflowAdmissions WHERE 1=0;
                SELECT [Incarnation], [InstanceId], [Sender], [Trigger], [OwnerKind], [Owner], [Tenant] FROM dbo.IxIFlowWorkflowOwnership WHERE 1=0;
                SELECT [DeliveryIdentity], [PayloadHash], [Sequence], [Rejection], [ExpiresAtUtc], [NoticeJson] FROM dbo.IxIFlowWebhookDeliveries WHERE 1=0;
                SELECT MemberId, Kind, Name, Endpoint, Slots, EngineVersion, HeartbeatIntervalTicks, StartedAtUtc, HeartbeatAtUtc FROM dbo.IxIFlowHosts WHERE 1=0;
                SELECT MemberId, CapabilityKey, CapabilityValue FROM dbo.IxIFlowHostCapabilities WHERE 1=0;
                SELECT DemandSetId FROM dbo.IxIFlowDemandSets WHERE 1=0;
                SELECT DemandSetId, DemandKey, DemandValue, ExpiresAtUtc, ValueExpiresAtUtc, Inherit, DemandKeyBytes FROM dbo.IxIFlowDemandSetRows WHERE 1=0;
                """, transaction: transaction);
            while (!schema.IsConsumed) await schema.ReadAsync();
            await ValidateDraftKeysAsync(connection, transaction);
            await ValidatePackageKeysAsync(connection, transaction);
            if (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.IxIFlowWorkflowPayloadRefs') AND name='IX_IxIFlowPayloadRefs_Owner'", transaction: transaction) != 1)
                throw EarlierBuild();
            if (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.IxIFlowWorkflowInstances') AND name=N'IX_IxIFlowInstances_SetPin'", transaction: transaction) != 1)
                throw EarlierBuild();
            // Opening an existing database must retain catalog, operations and outbox access paths.
            var indexes = (await connection.QueryAsync<string>("SELECT i.name FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE SCHEMA_NAME(t.schema_id) = 'dbo'",
                transaction: transaction)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] requiredIndexes =
            [
                "IX_IxIFlowInstances_DemandSet",
                "IX_IxIFlowInstances_SetAffinity",
                "IX_IxIFlowInstances_SetPin",
                "IX_IxIFlowInstances_SetRunnable",

                "IX_IxIFlowAdmissions_Expiry",
                "IX_IxIFlowHosts_Heartbeat",
                "IX_IxIFlowInstances_Ended",
                "IX_IxIFlowOutboxDeliveries_Listing",
                "IX_IxIFlowOutboxDeliveries_Pending",
                "IX_IxIFlowOutboxDeliveries_PublisherListing",
                "IX_IxIFlowOutboxDeliveries_StatusListing",
                "IX_IxIFlowOutboxEvents_Instance",
                "IX_IxIFlowOutboxEvents_Listing",
                "IX_IxIFlowTombstones_Expiry",
                "IX_IxIFlowWebhookDeliveries_Expiry",
                "IX_IxIFlowWorkflowEvents_Pending",
                "IX_IxIFlowWorkflowInstances_CorrelationId",
                "IX_IxIFlowWorkflowInstances_Parent",
                "IX_IxIFlowWorkflowInstances_Status",
                "IX_IxIFlowWorkflowInstances_UnfinishedWorkflow",
                "IX_IxIFlowWorkflowInstances_WorkflowName",
                "IX_IxIFlowWorkflowInstances_correlation_creation",
                "IX_IxIFlowWorkflowInstances_creation",
                "IX_IxIFlowWorkflowInstances_ended_creation",
                "IX_IxIFlowWorkflowInstances_parent_creation",
                "IX_IxIFlowWorkflowInstances_status_creation",
                "IX_IxIFlowWorkflowInstances_stop_creation",
                "IX_IxIFlowWorkflowInstances_version_creation",
                "IX_IxIFlowWorkflowInstances_workflow_creation",
                "IX_IxIFlowWorkflowInstances_workflow_status_creation",
                "IX_IxIFlowWorkflowInstances_workflow_version_creation",
                "IX_IxIFlowWorkflowInstances_BundleDigest",
                "IX_IxIFlowWorkflowLeases_ExpiresAt",
                "IX_IxIFlowWorkflowWaits_Open",
                "IX_IxIFlowWorkflowWaits_Page",
                "ix_publication_attempt_identity",
                "ix_publication_history",
                "ix_publication_finished",
                "ix_artifacts_created",
                "ix_workflow_versions_revision",
            ];
            if (requiredIndexes.Any(name => !indexes.Contains(name)))
                throw EarlierBuild();
            if (await connection.ExecuteScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(N'dbo.IxIFlowExecutionEpoch', N'SO') IS NOT NULL THEN 1 ELSE 0 END", transaction: transaction) != 1)
                throw EarlierBuild();
        }
        catch (Exception error) when (error is SqlException { Number: 207 or 208 })
        {
            throw EarlierBuild(error);
        }
    }

    private static async Task ValidateDraftKeysAsync(DbConnection connection, DbTransaction transaction)
    {
        await ValidateDraftPageIndexAsync(connection, transaction);
        if (!(await connection.QueryAsync<string>("SELECT c.name FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.object_id=OBJECT_ID('dbo.IxIFlowDefinitions') AND i.is_primary_key=1 AND ic.key_ordinal>0 ORDER BY ic.key_ordinal", transaction: transaction)).SequenceEqual(new[] { "Kind", "Name", "Version" }, StringComparer.Ordinal))
            throw EarlierBuild();
        if (!(await connection.QueryAsync<string>("SELECT c.name FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.object_id=OBJECT_ID('dbo.IxIFlowWorkflowVersions') AND i.is_primary_key=1 AND ic.key_ordinal>0 ORDER BY ic.key_ordinal", transaction: transaction)).SequenceEqual(new[] { "Name", "Version" }, StringComparer.Ordinal))
            throw EarlierBuild();
        if (!(await connection.QueryAsync<string>("SELECT c.name FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.object_id=OBJECT_ID('dbo.IxIFlowDraftGenerations') AND i.is_primary_key=1 AND ic.key_ordinal>0 ORDER BY ic.key_ordinal", transaction: transaction)).SequenceEqual(new[] { "Generation" }, StringComparer.Ordinal))
            throw EarlierBuild();
        if (!(await connection.QueryAsync<string>("SELECT c.name FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.object_id=OBJECT_ID('dbo.IxIFlowPublicationHeads') AND i.is_primary_key=1 AND ic.key_ordinal>0 ORDER BY ic.key_ordinal", transaction: transaction)).SequenceEqual(new[] { "Kind", "Name", "Version", "Generation", "Revision" }, StringComparer.Ordinal))
            throw EarlierBuild();
        if (!(await connection.QueryAsync<string>("SELECT c.name FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.object_id=OBJECT_ID('dbo.IxIFlowPublicationAttempts') AND i.is_primary_key=1 AND ic.key_ordinal>0 ORDER BY ic.key_ordinal", transaction: transaction)).SequenceEqual(new[] { "Id" }, StringComparer.Ordinal))
            throw EarlierBuild();
        if (!(await connection.QueryAsync<string>("SELECT c.name FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE i.object_id=OBJECT_ID('dbo.IxIFlowPublicationAttempts') AND i.name='ix_publication_attempt_identity' AND i.is_unique=1 AND i.has_filter=0 AND i.is_disabled=0 AND ic.key_ordinal>0 ORDER BY ic.key_ordinal", transaction: transaction)).SequenceEqual(new[] { "Kind", "Name", "Version", "Generation", "Revision", "Attempt" }, StringComparer.Ordinal))
            throw EarlierBuild();
        foreach (var (name, columns) in new[]
        {
            ("ix_publication_finished", new[] { "FinishedAtUtc" }),
            ("ix_publication_history", new[] { "Kind", "Name", "Version", "StartedAtUtc", "IdOrder" })
        })
        {
            if (!(await connection.QueryAsync<string>("""
                SELECT CASE WHEN ic.is_descending_key=0 THEN c.name ELSE '' END
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
                JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE i.object_id=OBJECT_ID('dbo.IxIFlowPublicationAttempts') AND i.name=@Name
                  AND i.type=2 AND i.is_unique=0 AND i.has_filter=0 AND i.filter_definition IS NULL
                  AND i.is_disabled=0 AND i.is_hypothetical=0 AND ic.key_ordinal>0
                ORDER BY ic.key_ordinal
                """, new { Name = name }, transaction)).SequenceEqual(columns, StringComparer.Ordinal))
                throw EarlierBuild();
        }
        if (await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.computed_columns c
            WHERE c.object_id=OBJECT_ID('dbo.IxIFlowPublicationAttempts') AND c.name='IdOrder'
              AND c.is_persisted=1 AND c.system_type_id=TYPE_ID('varchar') AND c.max_length=36
              AND c.collation_name='Latin1_General_100_BIN2'
              AND COLUMNPROPERTY(c.object_id,c.name,'IsDeterministic')=1
              AND COLUMNPROPERTY(c.object_id,c.name,'IsPrecise')=1
              AND LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(c.definition,'[',''),']',''),' ',''),'(',''),')',''))
                  = 'lowerconvertvarchar36,idcollatelatin1_general_100_bin2'
            """, transaction: transaction) != 1)
            throw EarlierBuild();
    }

    private static async Task ValidateDraftPageIndexAsync(DbConnection connection, DbTransaction transaction)
    {
        // The persisted UTF-8 bytes order names by code point, independently of NVARCHAR's UTF-16 order.
        if (await connection.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.computed_columns c
            WHERE c.object_id=OBJECT_ID('dbo.IxIFlowDefinitions') AND c.name='NameOrder'
              AND c.is_persisted=1 AND c.system_type_id=TYPE_ID('varbinary') AND c.max_length=800
              AND COLUMNPROPERTY(c.object_id,c.name,'IsDeterministic')=1
              AND COLUMNPROPERTY(c.object_id,c.name,'IsPrecise')=1
              AND LOWER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(c.definition,'[',''),']',''),' ',''),'(',''),')',''))
                  = 'convertvarbinary800,convertvarchar800,namecollatelatin1_general_100_ci_as_sc_utf8collatelatin1_general_100_ci_as_sc_utf8'
            """, transaction: transaction) != 1)
            throw EarlierBuild();
        if (!(await connection.QueryAsync<string>("""
            SELECT CASE WHEN ic.is_descending_key=0 THEN c.name ELSE '' END
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE i.object_id=OBJECT_ID('dbo.IxIFlowDefinitions') AND i.name='ix_definitions_page'
              AND i.type=2 AND i.is_unique=0 AND i.has_filter=0 AND i.filter_definition IS NULL
              AND i.is_disabled=0 AND i.is_hypothetical=0 AND ic.key_ordinal>0
            ORDER BY ic.key_ordinal
            """, transaction: transaction)).SequenceEqual(new[] { "Kind", "NameOrder", "Version" }, StringComparer.Ordinal))
            throw EarlierBuild();
        if (!(await connection.QueryAsync<string>("""
            SELECT c.name FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
            WHERE i.object_id=OBJECT_ID('dbo.IxIFlowDefinitions') AND i.name='ix_definitions_page' AND ic.is_included_column=1
            """, transaction: transaction)).ToHashSet(StringComparer.Ordinal)
            .SetEquals(new[] { "Name", "Generation", "Revision", "SourceLength", "UpdatedAtUtc", "UpdatedBy" }))
            throw EarlierBuild();
    }

    private static async Task ValidatePackageKeysAsync(DbConnection connection, DbTransaction transaction)
    {
        foreach (var (table, name, primary, columns) in new (string, string?, bool, string[])[]
        {
            ("dbo.IxIFlowPackages", null, true, ["Id", "Version"]),
            ("dbo.IxIFlowInstalledPackages", null, true, ["Id", "Version", "Fingerprint"]),
            ("dbo.IxIFlowPackageActivities", null, true, ["PackageId", "PackageVersion", "Key"]),
            ("dbo.IxIFlowInstalledActivities", null, true, ["PackageId", "PackageVersion", "Fingerprint", "Key"]),
            ("dbo.IxIFlowPackagePendingDependencies", null, true, ["PackageId", "PackageVersion", "MissingPackageId"]),
            ("dbo.IxIFlowPackages", "ix_packages_origin", false, ["Origin", "Id", "Version"]),
            ("dbo.IxIFlowPackageActivities", "ix_package_activities_key", false, ["Key", "Version", "PackageId", "PackageVersion"]),
            ("dbo.IxIFlowPackageActivities", "ix_package_activities_category", false, ["Category", "Key"]),
            ("dbo.IxIFlowInstalledActivities", "ix_installed_activities_key", false, ["Key", "Version", "PackageId", "PackageVersion"]),
            ("dbo.IxIFlowInstalledActivities", "ix_installed_activities_category", false, ["Category", "Key"]),
            ("dbo.IxIFlowPackagePendingDependencies", "ix_package_pending_reverse", false, ["MissingPackageId", "PackageId", "PackageVersion"]),
        })
            if (!(await connection.QueryAsync<string>("""
                SELECT CASE WHEN ic.is_descending_key=0 AND (c.collation_name IS NULL OR c.collation_name='Latin1_General_100_BIN2') THEN c.name ELSE '' END
                FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
                JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE i.object_id=OBJECT_ID(@Table) AND i.is_primary_key=@Primary AND i.is_unique=@Primary
                  AND (@Name IS NULL OR i.name=@Name) AND i.has_filter=0 AND i.filter_definition IS NULL
                  AND i.is_disabled=0 AND i.is_hypothetical=0 AND ic.key_ordinal>0 ORDER BY ic.key_ordinal
                """, new { Table = table, Name = name, Primary = primary }, transaction)).SequenceEqual(columns, StringComparer.Ordinal))
                throw EarlierBuild();
    }

    private static WorkflowEngineException EarlierBuild(Exception? error = null) =>
        new(WorkflowErrorKind.InvalidState,
            "The workflow database was created by an earlier build and must be recreated. In-place migration is not supported.", error);
}

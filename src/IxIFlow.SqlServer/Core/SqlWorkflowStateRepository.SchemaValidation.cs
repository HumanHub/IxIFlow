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
                SELECT Name, Version, Kind, Source, Revision, State FROM dbo.IxIFlowDefinitions WHERE 1=0;
                SELECT Name, Version, StructureHash, State, ArtifactDigest, MetadataJson FROM dbo.IxIFlowWorkflowVersions WHERE 1=0;
                SELECT Digest, Kind, Content, CreatedAtUtc FROM dbo.IxIFlowArtifacts WHERE 1=0;
                SELECT [InstanceId], [WorkflowName], [WorkflowVersion], [CreatedAtUtc], [StopKind], [StopCrashCount], [Status], [CorrelationId], [RunnableAtUtc], [EndedAtUtc], [StateJson], [CancellationJson], [CancellationAcknowledged], [Revision], [InboxSequence], [InboxReadThrough], [Incarnation], [ParentInstanceId], [ParentWaitId], [LastHostId], [DemandSetId], [PinnedHost], [PinExpiresAtUtc], [PinInherit] FROM dbo.IxIFlowWorkflowInstances WHERE 1=0;
                SELECT [InstanceId], [EventId], [Sequence], [EventType], [EventKey], [TargetWaitId], [Subject], [PayloadJson], [PayloadHash], [ReceivedAtUtc], [Outcome], [WaitId], [Rejection], [SettledAtUtc], [NoticeJson] FROM dbo.IxIFlowWorkflowEvents WHERE 1=0;
                SELECT [InstanceId], [PartKey], [Json] FROM dbo.IxIFlowWorkflowParts WHERE 1=0;
                SELECT [InstanceId], [Sequence], [EntryJson] FROM dbo.IxIFlowWorkflowTraces WHERE 1=0;
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
                SELECT DemandSetId, DemandKey, DemandValue, ExpiresAtUtc, Inherit, DemandKeyBytes FROM dbo.IxIFlowDemandSetRows WHERE 1=0;
                """, transaction: transaction);
            while (!schema.IsConsumed) await schema.ReadAsync();
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
                "IX_IxIFlowWorkflowInstances_Runnable",
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
                "IX_IxIFlowWorkflowLeases_ExpiresAt",
                "IX_IxIFlowWorkflowWaits_Open",
                "IX_IxIFlowWorkflowWaits_Page",
                "ix_artifacts_created",
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

    private static WorkflowEngineException EarlierBuild(Exception? error = null) =>
        new(WorkflowErrorKind.InvalidState,
            "The workflow database was created by an earlier build and must be recreated. In-place migration is not supported.", error);
}

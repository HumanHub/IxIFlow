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
                SELECT Digest, Bytes, Generation, Body, StateJson, CreatedAtUtc FROM dbo.IxIFlowWorkflowPayloads WHERE 1=0;
                SELECT Digest, OwnerKind, OwnerId FROM dbo.IxIFlowWorkflowPayloadRefs WHERE 1=0;
                SELECT [InstanceId], [WorkflowName], [Status], [CorrelationId], [RunnableAtUtc], [EndedAtUtc], [StateJson], [CancellationJson], [CancellationAcknowledged], [Revision], [InboxSequence], [InboxReadThrough], [Incarnation], [ParentInstanceId] FROM dbo.IxIFlowWorkflowInstances WHERE 1=0;
                SELECT [InstanceId], [EventId], [Sequence], [EventType], [EventKey], [TargetWaitId], [Subject], [PayloadJson], [PayloadHash], [ReceivedAtUtc], [Outcome], [WaitId], [Rejection], [SettledAtUtc] FROM dbo.IxIFlowWorkflowEvents WHERE 1=0;
                SELECT [InstanceId], [PartKey], [Json] FROM dbo.IxIFlowWorkflowParts WHERE 1=0;
                SELECT [InstanceId], [Sequence], [EntryJson] FROM dbo.IxIFlowWorkflowTraces WHERE 1=0;
                SELECT [InstanceId], [WaitId], [Kind], [WaitKey], [EventType], [DefaultPayloadJson], [DeadlineUtc], [OpenedSequence], [OpenedAtUtc], [CloseReason], [ClosedAtUtc] FROM dbo.IxIFlowWorkflowWaits WHERE 1=0;
                SELECT [InstanceId], [CommitId], [ExpectedRevision], [Revision], [PayloadHash] FROM dbo.IxIFlowWorkflowCommits WHERE 1=0;
                SELECT [InstanceId], [Token], [ExpiresAtUtc], [Epoch], [CrashCount] FROM dbo.IxIFlowWorkflowLeases WHERE 1=0;
                SELECT [Name], [TypesJson], [DisabledAtUtc], [LastError] FROM dbo.IxIFlowOutboxPublishers WHERE 1=0;
                SELECT [EventId], [Incarnation], [InstanceId], [EventType], [EventJson], [OccurredAtUtc] FROM dbo.IxIFlowOutboxEvents WHERE 1=0;
                SELECT [EventId], [PublisherName], [Attempts], [FirstAttemptAtUtc], [NextAttemptAtUtc], [Status], [LastError], [LeaseToken], [LeaseUntilUtc] FROM dbo.IxIFlowOutboxDeliveries WHERE 1=0;
                SELECT [InstanceId], [RemovedAtUtc], [ExpiresAtUtc] FROM dbo.IxIFlowWorkflowTombstones WHERE 1=0;
                SELECT [Incarnation], [Sender], [Trigger], [Key], [Fingerprint], [InstanceId], [WorkflowName], [WorkflowVersion], [ExpiresAtUtc] FROM dbo.IxIFlowWorkflowAdmissions WHERE 1=0;
                SELECT [Incarnation], [InstanceId], [Sender], [Trigger], [OwnerKind], [Owner], [Tenant] FROM dbo.IxIFlowWorkflowOwnership WHERE 1=0;
                SELECT [DeliveryIdentity], [PayloadHash], [Sequence], [Rejection], [ExpiresAtUtc] FROM dbo.IxIFlowWebhookDeliveries WHERE 1=0;
                SELECT member_id, kind, name, endpoint, slots, engine_version, heartbeat_interval_ticks, started_at_utc, heartbeat_at_utc FROM dbo.ixiflow_hosts WHERE 1=0;
                SELECT member_id, capability_key, capability_value FROM dbo.ixiflow_host_capabilities WHERE 1=0;
                """, transaction: transaction);
            while (!schema.IsConsumed) await schema.ReadAsync();
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

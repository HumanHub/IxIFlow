namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    private const string RetentionSchema = """
        IF OBJECT_ID(N'dbo.IxIFlowWorkflowTombstones', N'U') IS NULL
        CREATE TABLE dbo.IxIFlowWorkflowTombstones (
            InstanceId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            RemovedAtUtc DATETIME2 NOT NULL,
            ExpiresAtUtc DATETIME2 NOT NULL
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.IxIFlowWorkflowTombstones') AND name=N'IX_IxIFlowTombstones_Expiry')
            CREATE INDEX IX_IxIFlowTombstones_Expiry ON dbo.IxIFlowWorkflowTombstones (ExpiresAtUtc, InstanceId);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.IxIFlowWorkflowInstances') AND name=N'IX_IxIFlowInstances_Ended')
            CREATE INDEX IX_IxIFlowInstances_Ended ON dbo.IxIFlowWorkflowInstances (EndedAtUtc, InstanceId) WHERE EndedAtUtc IS NOT NULL;
        """;

    private static string? RetentionSql(Statement statement) => statement switch
    {
        Statement.RetentionLock => """
            DECLARE @r INT;
            EXEC @r=sys.sp_getapplock @Resource=N'IxIFlow.Retention', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000;
            IF @r<0 THROW 51000, 'Could not lock retention', 1;
            """,
        Statement.StampEnd => "UPDATE dbo.IxIFlowWorkflowInstances SET EndedAtUtc=COALESCE(EndedAtUtc, @Now) WHERE InstanceId=@InstanceId",
        Statement.Tombstone => "SELECT InstanceId, RemovedAtUtc, ExpiresAtUtc FROM dbo.IxIFlowWorkflowTombstones WHERE InstanceId=@InstanceId AND ExpiresAtUtc>@Now",
        Statement.WriteTombstone => """
            UPDATE dbo.IxIFlowWorkflowTombstones SET RemovedAtUtc=@Now, ExpiresAtUtc=@Expires WHERE InstanceId=@InstanceId;
            IF @@ROWCOUNT=0 INSERT INTO dbo.IxIFlowWorkflowTombstones (InstanceId, RemovedAtUtc, ExpiresAtUtc) VALUES (@InstanceId, @Now, @Expires);
            """,
        // Existing instance tables may have inherited the database collation; ID joins stay binary.
        Statement.RetentionCandidates => """
            SELECT TOP (@Limit) i.InstanceId FROM dbo.IxIFlowWorkflowInstances i WHERE i.EndedAtUtc<=@Cutoff AND i.Status IN ('Completed','Failed','Cancelled','Terminated','TimedOut')
              AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowOutboxEvents e JOIN dbo.IxIFlowOutboxDeliveries d ON d.EventId=e.EventId WHERE e.InstanceId=i.InstanceId COLLATE Latin1_General_100_BIN2 AND d.Status=0)
              AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances c WHERE c.ParentInstanceId COLLATE Latin1_General_100_BIN2=i.InstanceId COLLATE Latin1_General_100_BIN2 AND c.Status NOT IN ('Completed','Failed','Cancelled','Terminated','TimedOut'))
            ORDER BY i.EndedAtUtc, i.InstanceId
            """,
        Statement.RetentionEligible => """
            SELECT count(*) FROM dbo.IxIFlowWorkflowInstances i WHERE i.InstanceId=@InstanceId AND i.EndedAtUtc<=@Cutoff AND i.Status IN ('Completed','Failed','Cancelled','Terminated','TimedOut')
              AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowOutboxEvents e JOIN dbo.IxIFlowOutboxDeliveries d ON d.EventId=e.EventId WHERE e.InstanceId=i.InstanceId COLLATE Latin1_General_100_BIN2 AND d.Status=0)
              AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances c WHERE c.ParentInstanceId COLLATE Latin1_General_100_BIN2=i.InstanceId COLLATE Latin1_General_100_BIN2 AND c.Status NOT IN ('Completed','Failed','Cancelled','Terminated','TimedOut'))
            """,
        Statement.ExpireTombstones => """
            DELETE FROM dbo.IxIFlowWorkflowTombstones WHERE InstanceId IN
                (SELECT TOP (@Limit) InstanceId FROM dbo.IxIFlowWorkflowTombstones WHERE ExpiresAtUtc<=@Now ORDER BY ExpiresAtUtc, InstanceId)
            """,
        _ => null
    };
}

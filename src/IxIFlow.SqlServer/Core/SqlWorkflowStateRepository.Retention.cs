namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    private const string RetentionSchema = """
        IF OBJECT_ID(N'dbo.IxIFlowWorkflowTombstones', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.IxIFlowWorkflowTombstones (
                InstanceId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
                RemovedAtUtc DATETIME2 NOT NULL,
                ExpiresAtUtc DATETIME2 NOT NULL
            );
            CREATE INDEX IX_IxIFlowTombstones_Expiry ON dbo.IxIFlowWorkflowTombstones (ExpiresAtUtc, InstanceId);
        END
        """;

    private static string? RetentionSql(Statement statement) => statement switch
    {
        Statement.ExpireUnattemptedDeliveries => """
            ;WITH candidates AS (
                SELECT TOP (@Limit) d.*
                FROM dbo.IxIFlowOutboxDeliveries d WITH (UPDLOCK, READPAST, ROWLOCK)
                JOIN dbo.IxIFlowOutboxEvents e ON e.EventId=d.EventId
                WHERE d.Status=0 AND d.Attempts=0 AND d.FirstAttemptAtUtc IS NULL AND COALESCE(d.RetriedAtUtc, e.OccurredAtUtc)<=@Cutoff
                ORDER BY e.OccurredAtUtc, d.EventId, d.PublisherName
            )
            UPDATE candidates SET Status=2, GivenUpReason=1, LastError=@Error, LeaseToken=NULL, LeaseUntilUtc=NULL
            """,
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
        Statement.RetentionCandidates => """
            SELECT TOP (@Limit) i.InstanceId FROM dbo.IxIFlowWorkflowInstances i WHERE i.EndedAtUtc<=@Cutoff AND i.Status IN ('Completed','Failed','Cancelled','Terminated','TimedOut')
              AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowOutboxEvents e JOIN dbo.IxIFlowOutboxDeliveries d ON d.EventId=e.EventId WHERE e.InstanceId=i.InstanceId AND d.Status=0)
              AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances c WHERE c.ParentInstanceId=i.InstanceId AND c.Status NOT IN ('Completed','Failed','Cancelled','Terminated','TimedOut'))
            ORDER BY i.EndedAtUtc, i.InstanceId
            """,
        Statement.RetentionBlockedPublishers => """
            SELECT d.PublisherName, COUNT(DISTINCT i.InstanceId) AS Instances
            FROM dbo.IxIFlowWorkflowInstances i
            JOIN dbo.IxIFlowOutboxEvents e ON e.InstanceId=i.InstanceId
            JOIN dbo.IxIFlowOutboxDeliveries d ON d.EventId=e.EventId
            WHERE i.EndedAtUtc<=@Cutoff AND i.Status IN ('Completed','Failed','Cancelled','Terminated','TimedOut')
              AND d.Status=0 AND d.FirstAttemptAtUtc IS NULL
              AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances c WHERE c.ParentInstanceId=i.InstanceId AND c.Status NOT IN ('Completed','Failed','Cancelled','Terminated','TimedOut'))
            GROUP BY d.PublisherName
            """,
        Statement.RetentionEligible => """
            SELECT count(*) FROM dbo.IxIFlowWorkflowInstances i WHERE i.InstanceId=@InstanceId AND i.EndedAtUtc<=@Cutoff AND i.Status IN ('Completed','Failed','Cancelled','Terminated','TimedOut')
              AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowOutboxEvents e JOIN dbo.IxIFlowOutboxDeliveries d ON d.EventId=e.EventId WHERE e.InstanceId=i.InstanceId AND d.Status=0)
              AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances c WHERE c.ParentInstanceId=i.InstanceId AND c.Status NOT IN ('Completed','Failed','Cancelled','Terminated','TimedOut'))
            """,
        Statement.ExpireTombstones => """
            DELETE FROM dbo.IxIFlowWorkflowTombstones WHERE InstanceId IN
                (SELECT TOP (@Limit) InstanceId FROM dbo.IxIFlowWorkflowTombstones WHERE ExpiresAtUtc<=@Now ORDER BY ExpiresAtUtc, InstanceId)
            """,
        _ => null
    };
}

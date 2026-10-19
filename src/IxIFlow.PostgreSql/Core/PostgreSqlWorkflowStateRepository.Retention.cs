namespace IxIFlow.Core;

public sealed partial class PostgreSqlWorkflowStateRepository
{
    private const string RetentionSchema = """
        CREATE INDEX IF NOT EXISTS ix_ixiflow_instances_ended ON ixiflow_workflow_instances (ended_at_utc, instance_id) WHERE ended_at_utc IS NOT NULL;
        CREATE TABLE IF NOT EXISTS ixiflow_workflow_tombstones (
            instance_id varchar(100) COLLATE "C" NOT NULL PRIMARY KEY,
            removed_at_utc timestamptz NOT NULL,
            expires_at_utc timestamptz NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_ixiflow_tombstones_expiry ON ixiflow_workflow_tombstones (expires_at_utc, instance_id);
        """;

    private static string? RetentionSql(Statement statement) => statement switch
    {
        Statement.ExpireUnattemptedDeliveries => """
            WITH candidates AS (
                SELECT d.event_id, d.publisher_name
                FROM ixiflow_outbox_deliveries d
                JOIN ixiflow_outbox_events e ON e.event_id=d.event_id
                WHERE d.status=0 AND d.attempts=0 AND d.first_attempt_at_utc IS NULL AND COALESCE(d.retried_at_utc, e.occurred_at_utc)<=@Cutoff
                ORDER BY e.occurred_at_utc, d.event_id, d.publisher_name LIMIT @Limit
                FOR UPDATE OF d SKIP LOCKED
            )
            UPDATE ixiflow_outbox_deliveries d
            SET status=2, given_up_reason=1, last_error=@Error, lease_token=NULL, lease_until_utc=NULL
            FROM candidates c WHERE d.event_id=c.event_id AND d.publisher_name=c.publisher_name
            """,
        Statement.RetentionLock => "SELECT pg_advisory_xact_lock(73489623)",
        Statement.StampEnd => "UPDATE ixiflow_workflow_instances SET ended_at_utc=COALESCE(ended_at_utc, @Now) WHERE instance_id=@InstanceId",
        Statement.Tombstone => """
            SELECT instance_id AS InstanceId, removed_at_utc AS RemovedAtUtc, expires_at_utc AS ExpiresAtUtc
            FROM ixiflow_workflow_tombstones WHERE instance_id=@InstanceId AND expires_at_utc>@Now
            """,
        Statement.WriteTombstone => """
            INSERT INTO ixiflow_workflow_tombstones (instance_id, removed_at_utc, expires_at_utc)
            VALUES (@InstanceId, @Now, @Expires)
            ON CONFLICT (instance_id) DO UPDATE SET removed_at_utc=excluded.removed_at_utc, expires_at_utc=excluded.expires_at_utc
            """,
        Statement.RetentionCandidates => """
            SELECT i.instance_id FROM ixiflow_workflow_instances i WHERE i.ended_at_utc <= @Cutoff AND i.status IN ('Completed','Failed','Cancelled','Terminated','TimedOut')
              AND NOT EXISTS (SELECT 1 FROM ixiflow_outbox_events e JOIN ixiflow_outbox_deliveries d ON d.event_id=e.event_id WHERE e.instance_id=i.instance_id AND d.status=0)
              AND NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances c WHERE c.parent_instance_id=i.instance_id)
            ORDER BY i.ended_at_utc, i.instance_id LIMIT @Limit
            """,
        Statement.RetentionBlockedPublishers => """
            SELECT d.publisher_name AS PublisherName, COUNT(DISTINCT i.instance_id) AS Instances
            FROM ixiflow_workflow_instances i
            JOIN ixiflow_outbox_events e ON e.instance_id=i.instance_id
            JOIN ixiflow_outbox_deliveries d ON d.event_id=e.event_id
            WHERE i.ended_at_utc<=@Cutoff AND i.status IN ('Completed','Failed','Cancelled','Terminated','TimedOut')
              AND d.status=0 AND d.first_attempt_at_utc IS NULL
              AND NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances c WHERE c.parent_instance_id=i.instance_id)
            GROUP BY d.publisher_name
            """,
        Statement.RetentionEligible => """
            SELECT count(*) FROM ixiflow_workflow_instances i WHERE i.instance_id=@InstanceId AND i.ended_at_utc <= @Cutoff AND i.status IN ('Completed','Failed','Cancelled','Terminated','TimedOut')
              AND NOT EXISTS (SELECT 1 FROM ixiflow_outbox_events e JOIN ixiflow_outbox_deliveries d ON d.event_id=e.event_id WHERE e.instance_id=i.instance_id AND d.status=0)
              AND NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances c WHERE c.parent_instance_id=i.instance_id)
            """,
        Statement.ExpireTombstones => """
            DELETE FROM ixiflow_workflow_tombstones WHERE instance_id IN
                (SELECT instance_id FROM ixiflow_workflow_tombstones WHERE expires_at_utc<=@Now ORDER BY expires_at_utc, instance_id LIMIT @Limit)
            """,
        _ => null
    };
}

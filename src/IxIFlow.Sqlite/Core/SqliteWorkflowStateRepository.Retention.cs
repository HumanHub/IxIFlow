namespace IxIFlow.Core;

public sealed partial class SqliteWorkflowStateRepository
{
    private const string RetentionSchema = """
        CREATE INDEX IF NOT EXISTS ix_ixiflow_instances_ended ON ixiflow_workflow_instances (ended_at_utc, instance_id) WHERE ended_at_utc IS NOT NULL;
        CREATE TABLE IF NOT EXISTS ixiflow_workflow_tombstones (
            instance_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
            removed_at_utc TEXT NOT NULL,
            expires_at_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_ixiflow_tombstones_expiry ON ixiflow_workflow_tombstones (expires_at_utc, instance_id);
        """;

    private static string? RetentionSql(Statement statement) => statement switch
    {
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
              AND NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances c WHERE c.parent_instance_id=i.instance_id AND c.status NOT IN ('Completed','Failed','Cancelled','Terminated','TimedOut'))
            ORDER BY i.ended_at_utc, i.instance_id LIMIT @Limit
            """,
        Statement.RetentionBlockedPublishers => """
            SELECT d.publisher_name AS PublisherName, COUNT(DISTINCT i.instance_id) AS Instances
            FROM ixiflow_workflow_instances i
            JOIN ixiflow_outbox_events e ON e.instance_id=i.instance_id
            JOIN ixiflow_outbox_deliveries d ON d.event_id=e.event_id
            WHERE i.ended_at_utc<=@Cutoff AND i.status IN ('Completed','Failed','Cancelled','Terminated','TimedOut')
              AND d.status=0 AND d.first_attempt_at_utc IS NULL
              AND NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances c WHERE c.parent_instance_id=i.instance_id AND c.status NOT IN ('Completed','Failed','Cancelled','Terminated','TimedOut'))
            GROUP BY d.publisher_name
            """,
        Statement.RetentionEligible => """
            SELECT count(*) FROM ixiflow_workflow_instances i WHERE i.instance_id=@InstanceId AND i.ended_at_utc <= @Cutoff AND i.status IN ('Completed','Failed','Cancelled','Terminated','TimedOut')
              AND NOT EXISTS (SELECT 1 FROM ixiflow_outbox_events e JOIN ixiflow_outbox_deliveries d ON d.event_id=e.event_id WHERE e.instance_id=i.instance_id AND d.status=0)
              AND NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances c WHERE c.parent_instance_id=i.instance_id AND c.status NOT IN ('Completed','Failed','Cancelled','Terminated','TimedOut'))
            """,
        Statement.ExpireTombstones => """
            DELETE FROM ixiflow_workflow_tombstones WHERE instance_id IN
                (SELECT instance_id FROM ixiflow_workflow_tombstones WHERE expires_at_utc<=@Now ORDER BY expires_at_utc, instance_id LIMIT @Limit)
            """,
        _ => null
    };
}

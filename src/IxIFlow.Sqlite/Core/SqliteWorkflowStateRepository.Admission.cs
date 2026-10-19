namespace IxIFlow.Core;

public sealed partial class SqliteWorkflowStateRepository
{
    private const string AdmissionSchema = """
        CREATE TABLE ixiflow_workflow_admissions (
            incarnation TEXT NOT NULL,
            sender TEXT COLLATE BINARY NOT NULL,
            trigger_id TEXT COLLATE BINARY NOT NULL,
            request_key TEXT COLLATE BINARY NOT NULL,
            fingerprint TEXT COLLATE BINARY NOT NULL,
            instance_id TEXT COLLATE BINARY NOT NULL,
            workflow_name TEXT COLLATE BINARY NOT NULL,
            workflow_version integer NOT NULL,
            expires_at_utc TEXT NOT NULL,
            PRIMARY KEY (sender, trigger_id, request_key)
        );
        CREATE INDEX ix_ixiflow_admissions_expiry ON ixiflow_workflow_admissions (expires_at_utc);
        CREATE TABLE ixiflow_workflow_ownership (
            incarnation TEXT NOT NULL,
            instance_id TEXT COLLATE BINARY NOT NULL,
            sender TEXT COLLATE BINARY NOT NULL,
            trigger_id TEXT COLLATE BINARY NOT NULL,
            owner_id TEXT COLLATE BINARY NOT NULL,
            tenant TEXT COLLATE BINARY NULL,
            PRIMARY KEY (instance_id)
        );
        CREATE TABLE ixiflow_webhook_deliveries (
            delivery_identity TEXT NOT NULL PRIMARY KEY,
            payload_hash BLOB NOT NULL,
            sequence BIGINT NOT NULL,
            rejection SMALLINT NULL,
            expires_at_utc TEXT NOT NULL
        );
        CREATE INDEX ix_ixiflow_webhook_deliveries_expiry ON ixiflow_webhook_deliveries (expires_at_utc);
        """;

    private static string? AdmissionSql(Statement statement) => statement switch
    {
        Statement.AdmissionLock => null,
        Statement.AdmissionRead => """
            SELECT incarnation AS "Incarnation", sender AS "Sender", trigger_id AS "Trigger", request_key AS "Key", fingerprint AS "Fingerprint", instance_id AS "InstanceId", workflow_name AS "WorkflowName", workflow_version AS "WorkflowVersion", expires_at_utc AS "ExpiresAtUtc" FROM ixiflow_workflow_admissions WHERE sender=@Sender AND trigger_id=@Trigger AND request_key=@Key AND expires_at_utc>@Now
            """,
        Statement.AdmissionDelete => """
            DELETE FROM ixiflow_workflow_admissions WHERE sender=@Sender AND trigger_id=@Trigger AND request_key=@Key
            """,
        Statement.AdmissionInsert => """
            INSERT INTO ixiflow_workflow_admissions (incarnation, sender, trigger_id, request_key, fingerprint, instance_id, workflow_name, workflow_version, expires_at_utc) VALUES (@Incarnation, @Sender, @Trigger, @Key, @Fingerprint, @InstanceId, @WorkflowName, @WorkflowVersion, @ExpiresAtUtc)
            """,
        Statement.OwnershipDelete => """
            DELETE FROM ixiflow_workflow_ownership WHERE instance_id=@InstanceId
            """,
        Statement.OwnershipInsert => """
            INSERT INTO ixiflow_workflow_ownership (incarnation, instance_id, sender, trigger_id, owner_id, tenant) VALUES (@Incarnation, @InstanceId, @Sender, @Trigger, @Owner, @Tenant)
            """,
        Statement.OwnershipRead => """
            SELECT instance_id AS "InstanceId", sender AS "Sender", trigger_id AS "Trigger", owner_id AS "Owner", tenant AS "Tenant", incarnation AS "Incarnation" FROM ixiflow_workflow_ownership o WHERE o.instance_id=@InstanceId AND (EXISTS (SELECT 1 FROM ixiflow_workflow_instances i WHERE i.instance_id=o.instance_id AND i.incarnation=o.incarnation) OR EXISTS (SELECT 1 FROM ixiflow_workflow_tombstones t WHERE t.instance_id=o.instance_id AND t.expires_at_utc>@Now))
            """,
        Statement.AdmissionExpire => """
            DELETE FROM ixiflow_workflow_admissions WHERE (sender, trigger_id, request_key) IN (SELECT sender, trigger_id, request_key FROM ixiflow_workflow_admissions WHERE expires_at_utc<=@Now ORDER BY expires_at_utc LIMIT @Limit)
            """,
        Statement.OwnershipExpire => """
            DELETE FROM ixiflow_workflow_ownership WHERE instance_id IN (SELECT o.instance_id FROM ixiflow_workflow_ownership o WHERE NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances i WHERE i.instance_id=o.instance_id AND i.incarnation=o.incarnation) AND NOT EXISTS (SELECT 1 FROM ixiflow_workflow_tombstones t WHERE t.instance_id=o.instance_id AND t.expires_at_utc>@Now) LIMIT @Limit)
            """,
        Statement.WebhookDeliveryRead => """
            SELECT payload_hash AS PayloadHash, sequence AS "Sequence", rejection AS Rejection FROM ixiflow_webhook_deliveries WHERE delivery_identity=@DeliveryIdentity AND expires_at_utc>@Now
            """,
        Statement.WebhookDeliveryDelete => """
            DELETE FROM ixiflow_webhook_deliveries WHERE delivery_identity=@DeliveryIdentity
            """,
        Statement.WebhookDeliveryInsert => """
            INSERT INTO ixiflow_webhook_deliveries (delivery_identity, payload_hash, sequence, rejection, expires_at_utc) VALUES (@DeliveryIdentity, @PayloadHash, @Sequence, @Rejection, @Expires)
            """,
        Statement.WebhookDeliveryExpire => """
            DELETE FROM ixiflow_webhook_deliveries WHERE delivery_identity IN (SELECT delivery_identity FROM ixiflow_webhook_deliveries WHERE expires_at_utc<=@Now ORDER BY expires_at_utc LIMIT @Limit)
            """,
        _ => RetentionSql(statement)
    };
}

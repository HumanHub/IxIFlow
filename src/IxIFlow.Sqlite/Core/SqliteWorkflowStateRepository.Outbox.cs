namespace IxIFlow.Core;

public sealed partial class SqliteWorkflowStateRepository
{
    private const string OutboxSchema = """
       
        CREATE TABLE IF NOT EXISTS ixiflow_outbox_publishers (
            name TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
            types_json TEXT NOT NULL,
            disabled_at_utc TEXT NULL,
            last_error TEXT NULL
        );
       
        CREATE TABLE IF NOT EXISTS ixiflow_outbox_events (
            event_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
            incarnation TEXT NOT NULL,
            instance_id TEXT COLLATE BINARY NOT NULL,
            event_type TEXT COLLATE BINARY NOT NULL,
            event_json TEXT NOT NULL,
            occurred_at_utc TEXT NOT NULL
        );
       
        CREATE TABLE IF NOT EXISTS ixiflow_outbox_deliveries (
            event_id TEXT COLLATE BINARY NOT NULL,
            publisher_name TEXT COLLATE BINARY NOT NULL,
            attempts INTEGER NOT NULL,
            first_attempt_at_utc TEXT NULL,
            next_attempt_at_utc TEXT NOT NULL,
            status INTEGER NOT NULL,
            last_error TEXT NULL,
            lease_token TEXT COLLATE BINARY NULL,
            lease_until_utc TEXT NULL,
            PRIMARY KEY (event_id, publisher_name)
        );
       
        CREATE INDEX IF NOT EXISTS ix_ixiflow_outbox_deliveries_pending ON ixiflow_outbox_deliveries (publisher_name, next_attempt_at_utc, event_id) WHERE status=0;
        CREATE INDEX IF NOT EXISTS ix_ixiflow_outbox_events_instance ON ixiflow_outbox_events (instance_id, incarnation, event_type);
        """;
}

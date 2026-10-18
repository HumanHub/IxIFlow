namespace IxIFlow.Core;

public sealed partial class PostgreSqlWorkflowStateRepository
{
    private const string OutboxSchema = """
       
        CREATE TABLE IF NOT EXISTS ixiflow_outbox_publishers (
            name varchar(100) COLLATE "C" NOT NULL PRIMARY KEY,
            types_json text NOT NULL,
            disabled_at_utc timestamptz NULL,
            last_error text NULL
        );
       
        CREATE TABLE IF NOT EXISTS ixiflow_outbox_events (
            event_id varchar(100) COLLATE "C" NOT NULL PRIMARY KEY,
            incarnation uuid NOT NULL,
            instance_id varchar(100) COLLATE "C" NOT NULL,
            event_type varchar(400) COLLATE "C" NOT NULL,
            event_json text NOT NULL,
            occurred_at_utc timestamptz NOT NULL
        );
       
        CREATE TABLE IF NOT EXISTS ixiflow_outbox_deliveries (
            event_id varchar(100) COLLATE "C" NOT NULL,
            publisher_name varchar(100) COLLATE "C" NOT NULL,
            attempts integer NOT NULL,
            first_attempt_at_utc timestamptz NULL,
            next_attempt_at_utc timestamptz NOT NULL,
            status integer NOT NULL,
            last_error text NULL,
            lease_token varchar(100) COLLATE "C" NULL,
            lease_until_utc timestamptz NULL,
            PRIMARY KEY (event_id, publisher_name)
        );
       
        CREATE INDEX IF NOT EXISTS ix_ixiflow_outbox_deliveries_pending ON ixiflow_outbox_deliveries (publisher_name, next_attempt_at_utc, event_id) WHERE status=0;
        CREATE INDEX IF NOT EXISTS ix_ixiflow_outbox_events_instance ON ixiflow_outbox_events (instance_id, incarnation, event_type);
        """;
}

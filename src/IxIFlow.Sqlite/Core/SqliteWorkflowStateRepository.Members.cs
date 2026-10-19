namespace IxIFlow.Core;

public sealed partial class SqliteWorkflowStateRepository
{
    internal override bool SingleHostRegistry => true;

    private const string RegistrySchema = """
        CREATE TABLE ixiflow_hosts (
            member_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
            kind TEXT NOT NULL CHECK (kind IN ('host', 'coordinator')),
            name TEXT NOT NULL,
            endpoint TEXT NULL,
            slots INTEGER NOT NULL,
            engine_version TEXT NOT NULL,
            heartbeat_interval_ticks INTEGER NOT NULL,
            started_at_utc TEXT NOT NULL,
            heartbeat_at_utc TEXT NOT NULL
        );
        CREATE INDEX ix_ixiflow_hosts_heartbeat ON ixiflow_hosts (heartbeat_at_utc);
        CREATE TABLE ixiflow_host_capabilities (
            member_id TEXT COLLATE BINARY NOT NULL REFERENCES ixiflow_hosts(member_id) ON DELETE CASCADE,
            capability_key TEXT COLLATE BINARY NOT NULL,
            capability_value TEXT NOT NULL,
            PRIMARY KEY (member_id, capability_key)
        );
        """;

    private static string? RegistrySql(Statement statement) => statement switch
    {
        // The immediate transaction already owns the writer lock.
        Statement.RegistryLock => null,
        Statement.RegisterMember => """
            DELETE FROM ixiflow_host_capabilities WHERE member_id <> @MemberId;
            DELETE FROM ixiflow_hosts WHERE member_id <> @MemberId;
            INSERT INTO ixiflow_hosts (member_id, kind, name, endpoint, slots, engine_version,
                heartbeat_interval_ticks, started_at_utc, heartbeat_at_utc)
            VALUES (@MemberId, @Kind, @Name, @Endpoint, @Slots, @EngineVersion, @HeartbeatIntervalTicks, @Now, @Now)
            ON CONFLICT (member_id) DO UPDATE SET kind=@Kind, name=@Name, endpoint=@Endpoint, slots=@Slots,
                engine_version=@EngineVersion, heartbeat_interval_ticks=@HeartbeatIntervalTicks, heartbeat_at_utc=@Now;
            """,
        Statement.MemberHeartbeat => "UPDATE ixiflow_hosts SET heartbeat_at_utc=@Now WHERE member_id=@MemberId",
        Statement.UnregisterMember => """
            DELETE FROM ixiflow_host_capabilities WHERE member_id=@MemberId;
            DELETE FROM ixiflow_hosts WHERE member_id=@MemberId;
            """,
        Statement.PruneMembers => """
            DELETE FROM ixiflow_host_capabilities WHERE member_id IN
                (SELECT member_id FROM ixiflow_hosts WHERE heartbeat_at_utc < @PruneBefore);
            DELETE FROM ixiflow_hosts WHERE heartbeat_at_utc < @PruneBefore;
            """,
        Statement.Members => """
            SELECT member_id AS MemberId, kind AS Kind, name AS Name, endpoint AS Endpoint, slots AS Slots,
                engine_version AS EngineVersion, heartbeat_interval_ticks AS HeartbeatIntervalTicks,
                started_at_utc AS StartedAtUtc, heartbeat_at_utc AS HeartbeatAtUtc FROM ixiflow_hosts
            """,
        Statement.MemberCapabilities => """
            SELECT member_id AS MemberId, capability_key AS Key, capability_value AS Value FROM ixiflow_host_capabilities
            """,
        Statement.DeleteCapabilities => "DELETE FROM ixiflow_host_capabilities WHERE member_id=@MemberId",
        Statement.InsertCapability => """
            INSERT INTO ixiflow_host_capabilities (member_id, capability_key, capability_value) VALUES (@MemberId, @Key, @Value)
            """,
        _ => null
    };
}

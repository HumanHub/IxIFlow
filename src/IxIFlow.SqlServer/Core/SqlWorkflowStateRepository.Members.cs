namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    private const string RegistrySchema = """
        CREATE TABLE dbo.ixiflow_hosts (
            member_id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
            kind VARCHAR(11) NOT NULL CHECK (kind IN ('host', 'coordinator')),
            name NVARCHAR(200) NOT NULL,
            endpoint NVARCHAR(2048) NULL,
            slots INT NOT NULL,
            engine_version NVARCHAR(100) NOT NULL,
            heartbeat_interval_ticks BIGINT NOT NULL,
            started_at_utc DATETIME2 NOT NULL,
            heartbeat_at_utc DATETIME2 NOT NULL
        );
        CREATE INDEX ix_ixiflow_hosts_heartbeat ON dbo.ixiflow_hosts (heartbeat_at_utc);
        CREATE TABLE dbo.ixiflow_host_capabilities (
            member_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.ixiflow_hosts(member_id) ON DELETE CASCADE,
            capability_key NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
            capability_value NVARCHAR(MAX) NOT NULL,
            PRIMARY KEY (member_id, capability_key)
        );
        """;

    private static string? RegistrySql(Statement statement) => statement switch
    {
        Statement.RegistryLock => """
            DECLARE @result INT;
            EXEC @result = sys.sp_getapplock @Resource=N'IxIFlow.Registry', @LockMode=N'Exclusive',
                @LockOwner=N'Transaction', @LockTimeout=30000;
            IF @result < 0 THROW 51000, 'Could not lock the member registry', 1;
            """,
        Statement.RegisterMember => """
            UPDATE dbo.ixiflow_hosts SET kind=@Kind, name=@Name, endpoint=@Endpoint, slots=@Slots,
                engine_version=@EngineVersion, heartbeat_interval_ticks=@HeartbeatIntervalTicks, heartbeat_at_utc=@Now
                WHERE member_id=@MemberId;
            IF @@ROWCOUNT=0
                INSERT INTO dbo.ixiflow_hosts (member_id, kind, name, endpoint, slots, engine_version,
                    heartbeat_interval_ticks, started_at_utc, heartbeat_at_utc)
                VALUES (@MemberId, @Kind, @Name, @Endpoint, @Slots, @EngineVersion, @HeartbeatIntervalTicks, @Now, @Now);
            """,
        Statement.MemberHeartbeat => "UPDATE dbo.ixiflow_hosts SET heartbeat_at_utc=@Now WHERE member_id=@MemberId",
        Statement.UnregisterMember => """
            DELETE FROM dbo.ixiflow_host_capabilities WHERE member_id=@MemberId;
            DELETE FROM dbo.ixiflow_hosts WHERE member_id=@MemberId;
            """,
        Statement.PruneMembers => """
            DELETE FROM dbo.ixiflow_host_capabilities WHERE member_id IN
                (SELECT member_id FROM dbo.ixiflow_hosts WHERE heartbeat_at_utc < @PruneBefore);
            DELETE FROM dbo.ixiflow_hosts WHERE heartbeat_at_utc < @PruneBefore;
            """,
        Statement.Members => """
            SELECT member_id AS MemberId, kind AS Kind, name AS Name, endpoint AS Endpoint, slots AS Slots,
                engine_version AS EngineVersion, heartbeat_interval_ticks AS HeartbeatIntervalTicks,
                started_at_utc AS StartedAtUtc, heartbeat_at_utc AS HeartbeatAtUtc FROM dbo.ixiflow_hosts
            """,
        Statement.MemberCapabilities => """
            SELECT member_id AS MemberId, capability_key AS [Key], capability_value AS Value FROM dbo.ixiflow_host_capabilities
            """,
        Statement.DeleteCapabilities => "DELETE FROM dbo.ixiflow_host_capabilities WHERE member_id=@MemberId",
        Statement.InsertCapability => """
            INSERT INTO dbo.ixiflow_host_capabilities (member_id, capability_key, capability_value) VALUES (@MemberId, @Key, @Value)
            """,
        _ => null
    };
}

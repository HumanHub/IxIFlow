namespace IxIFlow.Core;

public sealed partial class PostgreSqlWorkflowStateRepository
{
    private const string RegistrySchema = """
        CREATE TABLE ixiflow_hosts (
            member_id uuid NOT NULL PRIMARY KEY,
            kind varchar(11) NOT NULL CHECK (kind IN ('host', 'coordinator')),
            name varchar(200) NOT NULL,
            endpoint varchar(2048) NULL,
            slots integer NOT NULL,
            engine_version varchar(100) NOT NULL,
            heartbeat_interval_ticks bigint NOT NULL,
            started_at_utc timestamptz NOT NULL,
            heartbeat_at_utc timestamptz NOT NULL
        );
        CREATE INDEX ix_ixiflow_hosts_heartbeat ON ixiflow_hosts (heartbeat_at_utc);
        CREATE TABLE ixiflow_host_capabilities (
            member_id uuid NOT NULL REFERENCES ixiflow_hosts(member_id) ON DELETE CASCADE,
            capability_key varchar(400) COLLATE "C" NOT NULL,
            capability_value text NOT NULL,
            PRIMARY KEY (member_id, capability_key)
        );
        CREATE TABLE ixiflow_installed_packages (
            id TEXT COLLATE "C" NOT NULL,
            version TEXT COLLATE "C" NOT NULL,
            fingerprint TEXT COLLATE "C" NOT NULL,
            activities_json TEXT NOT NULL,
            PRIMARY KEY (id, version, fingerprint)
        );
        CREATE TABLE ixiflow_host_packages (
            member_id uuid NOT NULL REFERENCES ixiflow_hosts(member_id),
            package_id TEXT COLLATE "C" NOT NULL,
            package_version TEXT COLLATE "C" NOT NULL,
            fingerprint TEXT COLLATE "C" NOT NULL,
            PRIMARY KEY (member_id, package_id, package_version, fingerprint),
            FOREIGN KEY (package_id, package_version, fingerprint) REFERENCES ixiflow_installed_packages(id, version, fingerprint)
        );
        """;

    private static string? RegistrySql(Statement statement) => statement switch
    {
        Statement.RegistryLock => "SELECT pg_advisory_xact_lock(hashtext('IxIFlow.Registry'))",
        Statement.RegisterMember => """
            INSERT INTO ixiflow_hosts (member_id, kind, name, endpoint, slots, engine_version,
                heartbeat_interval_ticks, started_at_utc, heartbeat_at_utc)
            VALUES (@MemberId, @Kind, @Name, @Endpoint, @Slots, @EngineVersion, @HeartbeatIntervalTicks, @Now, @Now)
            ON CONFLICT (member_id) DO UPDATE SET kind=@Kind, name=@Name, endpoint=@Endpoint, slots=@Slots,
                engine_version=@EngineVersion, heartbeat_interval_ticks=@HeartbeatIntervalTicks, heartbeat_at_utc=@Now;
            """,
        Statement.MemberHeartbeat => "UPDATE ixiflow_hosts SET heartbeat_at_utc=@Now WHERE member_id=@MemberId",
        Statement.UnregisterMember => """
            DELETE FROM ixiflow_host_packages WHERE member_id=@MemberId;
            DELETE FROM ixiflow_host_capabilities WHERE member_id=@MemberId;
            DELETE FROM ixiflow_hosts WHERE member_id=@MemberId;
            """,
        Statement.PruneMembers => """
            DELETE FROM ixiflow_host_packages WHERE member_id IN
                (SELECT member_id FROM ixiflow_hosts WHERE heartbeat_at_utc < @PruneBefore);
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
        Statement.InstalledPackages => "SELECT id AS Id, version AS Version, fingerprint AS Fingerprint, activities_json AS ActivitiesJson FROM ixiflow_installed_packages WHERE CAST(@Id AS text) IS NULL OR id=@Id",
        Statement.HostPackages => "SELECT member_id AS MemberId, package_id AS Id, package_version AS Version, fingerprint AS Fingerprint FROM ixiflow_host_packages",
        Statement.InsertInstalledPackage => """
            INSERT INTO ixiflow_installed_packages (id, version, fingerprint, activities_json) VALUES (@Id, @Version, @Fingerprint, @ActivitiesJson)
            ON CONFLICT (id, version, fingerprint) DO NOTHING;
            """,
        Statement.DeleteHostPackages => "DELETE FROM ixiflow_host_packages WHERE member_id=@MemberId",
        Statement.InsertHostPackage => "INSERT INTO ixiflow_host_packages (member_id, package_id, package_version, fingerprint) VALUES (@MemberId, @Id, @Version, @Fingerprint)",
        _ => null
    };
}

namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    private const string RegistrySchema = """
        CREATE TABLE dbo.IxIFlowHosts (
            MemberId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
            Kind VARCHAR(11) NOT NULL CHECK (Kind IN ('host', 'coordinator')),
            Name NVARCHAR(200) NOT NULL,
            Endpoint NVARCHAR(2048) NULL,
            Slots INT NOT NULL,
            EngineVersion NVARCHAR(100) NOT NULL,
            HeartbeatIntervalTicks BIGINT NOT NULL,
            StartedAtUtc DATETIME2 NOT NULL,
            HeartbeatAtUtc DATETIME2 NOT NULL
        );
        CREATE INDEX IX_IxIFlowHosts_Heartbeat ON dbo.IxIFlowHosts (HeartbeatAtUtc);
        CREATE TABLE dbo.IxIFlowHostCapabilities (
            MemberId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.IxIFlowHosts(MemberId) ON DELETE CASCADE,
            CapabilityKey NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
            CapabilityValue NVARCHAR(MAX) NOT NULL,
            PRIMARY KEY (MemberId, CapabilityKey)
        );
        CREATE TABLE dbo.IxIFlowInstalledPackages (
            Id NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Version NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Fingerprint VARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            ActivitiesJson NVARCHAR(MAX) NOT NULL,
            PRIMARY KEY (Id, Version, Fingerprint)
        );
        CREATE TABLE dbo.IxIFlowHostPackages (
            MemberId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.IxIFlowHosts(MemberId),
            PackageId NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PackageVersion NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Fingerprint VARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PRIMARY KEY (MemberId, PackageId, PackageVersion, Fingerprint),
            FOREIGN KEY (PackageId, PackageVersion, Fingerprint) REFERENCES dbo.IxIFlowInstalledPackages(Id, Version, Fingerprint)
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
            UPDATE dbo.IxIFlowHosts SET Kind=@Kind, Name=@Name, Endpoint=@Endpoint, Slots=@Slots,
                EngineVersion=@EngineVersion, HeartbeatIntervalTicks=@HeartbeatIntervalTicks, HeartbeatAtUtc=@Now
                WHERE MemberId=@MemberId;
            IF @@ROWCOUNT=0
                INSERT INTO dbo.IxIFlowHosts (MemberId, Kind, Name, Endpoint, Slots, EngineVersion,
                    HeartbeatIntervalTicks, StartedAtUtc, HeartbeatAtUtc)
                VALUES (@MemberId, @Kind, @Name, @Endpoint, @Slots, @EngineVersion, @HeartbeatIntervalTicks, @Now, @Now);
            """,
        Statement.MemberHeartbeat => "UPDATE dbo.IxIFlowHosts SET HeartbeatAtUtc=@Now WHERE MemberId=@MemberId",
        Statement.UnregisterMember => """
            DELETE FROM dbo.IxIFlowHostPackages WHERE MemberId=@MemberId;
            DELETE FROM dbo.IxIFlowHostCapabilities WHERE MemberId=@MemberId;
            DELETE FROM dbo.IxIFlowHosts WHERE MemberId=@MemberId;
            """,
        Statement.PruneMembers => """
            DELETE FROM dbo.IxIFlowHostPackages WHERE MemberId IN
                (SELECT MemberId FROM dbo.IxIFlowHosts WHERE HeartbeatAtUtc < @PruneBefore);
            DELETE FROM dbo.IxIFlowHostCapabilities WHERE MemberId IN
                (SELECT MemberId FROM dbo.IxIFlowHosts WHERE HeartbeatAtUtc < @PruneBefore);
            DELETE FROM dbo.IxIFlowHosts WHERE HeartbeatAtUtc < @PruneBefore;
            """,
        Statement.Members => """
            SELECT MemberId, Kind, Name, Endpoint, Slots,
                EngineVersion, HeartbeatIntervalTicks,
                StartedAtUtc, HeartbeatAtUtc FROM dbo.IxIFlowHosts
            """,
        Statement.MemberCapabilities => """
            SELECT MemberId, CapabilityKey AS [Key], CapabilityValue AS Value FROM dbo.IxIFlowHostCapabilities
            """,
        Statement.DeleteCapabilities => "DELETE FROM dbo.IxIFlowHostCapabilities WHERE MemberId=@MemberId",
        Statement.InsertCapability => """
            INSERT INTO dbo.IxIFlowHostCapabilities (MemberId, CapabilityKey, CapabilityValue) VALUES (@MemberId, @Key, @Value)
            """,
        Statement.InstalledPackages => "SELECT Id, Version, Fingerprint, ActivitiesJson FROM dbo.IxIFlowInstalledPackages WHERE @Id IS NULL OR Id=@Id",
        Statement.HostPackages => "SELECT MemberId, PackageId AS Id, PackageVersion AS Version, Fingerprint FROM dbo.IxIFlowHostPackages",
        Statement.InsertInstalledPackage => """
            IF NOT EXISTS (SELECT 1 FROM dbo.IxIFlowInstalledPackages WHERE Id=@Id AND Version=@Version AND Fingerprint=@Fingerprint)
                INSERT INTO dbo.IxIFlowInstalledPackages (Id, Version, Fingerprint, ActivitiesJson) VALUES (@Id, @Version, @Fingerprint, @ActivitiesJson);
            """,
        Statement.DeleteHostPackages => "DELETE FROM dbo.IxIFlowHostPackages WHERE MemberId=@MemberId",
        Statement.InsertHostPackage => "INSERT INTO dbo.IxIFlowHostPackages (MemberId, PackageId, PackageVersion, Fingerprint) VALUES (@MemberId, @Id, @Version, @Fingerprint)",
        _ => null
    };
}

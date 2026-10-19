namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    private const string DemandsSchema = """
        CREATE TYPE dbo.IxIFlowClaimCapabilities AS TABLE (
            capability_key NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
            capability_value NVARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NOT NULL,
            capability_key_bytes INT NOT NULL,
            PRIMARY KEY (capability_key, capability_key_bytes)
        );
        CREATE TABLE dbo.IxIFlowDemandSets (DemandSetId VARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY);
        CREATE TABLE dbo.IxIFlowDemandSetRows (
            DemandSetId VARCHAR(64) COLLATE Latin1_General_100_BIN2 NOT NULL REFERENCES dbo.IxIFlowDemandSets (DemandSetId) ON DELETE CASCADE,
            DemandKey NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
            DemandValue NVARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NULL,
            ExpiresAtUtc DATETIME2 NULL,
            Inherit BIT NOT NULL,
            DemandKeyBytes AS DATALENGTH(DemandKey) PERSISTED NOT NULL,
            PRIMARY KEY (DemandSetId, DemandKey, DemandKeyBytes)
        );
        """;

    internal override string DemandSetsLockSql => """
        DECLARE @r INT;
        EXEC @r = sys.sp_getapplock @Resource=N'IxIFlow.DemandSets', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000;
        IF @r < 0 THROW 51000, 'Could not lock demand sets', 1;
        """;

    internal override string ReadDemandsSql => """
        SELECT demand.DemandKey AS [Key], demand.DemandValue AS [Value], demand.ExpiresAtUtc AS [ExpiresAtUtc], demand.Inherit AS [Inherit]
        FROM dbo.IxIFlowWorkflowInstances AS instance JOIN dbo.IxIFlowDemandSetRows AS demand ON demand.DemandSetId = instance.DemandSetId
        WHERE instance.InstanceId = @InstanceId
        UNION ALL
        SELECT 'host', PinnedHost, PinExpiresAtUtc, PinInherit FROM dbo.IxIFlowWorkflowInstances
        WHERE InstanceId = @InstanceId AND PinnedHost IS NOT NULL
        """;

    internal override string ReadDemandSetSql => """
        SELECT DemandKey AS [Key], DemandValue AS [Value], ExpiresAtUtc AS [ExpiresAtUtc], Inherit AS [Inherit]
        FROM dbo.IxIFlowDemandSetRows WHERE DemandSetId = @DemandSetId
        """;

    internal override string FindDemandSetSql => "SELECT DemandSetId FROM dbo.IxIFlowDemandSets WHERE DemandSetId = @DemandSetId";
    internal override string InsertDemandSetSql => "INSERT INTO dbo.IxIFlowDemandSets (DemandSetId) VALUES (@DemandSetId)";
    internal override string InsertDemandSql => """
        INSERT INTO dbo.IxIFlowDemandSetRows (DemandSetId, DemandKey, DemandValue, ExpiresAtUtc, Inherit)
        VALUES (@DemandSetId, @Key, @Value, @ExpiresAtUtc, @Inherit)
        """;

    internal override string CollectDemandSetsSql => """
        DELETE FROM dbo.IxIFlowDemandSets WHERE DemandSetId IN (
            SELECT TOP (@Limit) sets.DemandSetId FROM dbo.IxIFlowDemandSets AS sets
            WHERE NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances AS instance WHERE instance.DemandSetId = sets.DemandSetId)
            ORDER BY sets.DemandSetId);
        """;
}

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
            ValueExpiresAtUtc DATETIME2 NULL,
            Inherit BIT NOT NULL,
            DemandKeyBytes AS DATALENGTH(DemandKey) PERSISTED NOT NULL,
            PRIMARY KEY (DemandSetId, DemandKey, DemandKeyBytes)
        );
        """;

    internal override string CreateDemandSetsLockSql => """
        DECLARE @r INT;
        EXEC @r = sys.sp_getapplock @Resource=N'IxIFlow.DemandSets', @LockMode=N'Shared', @LockOwner=N'Transaction', @LockTimeout=30000;
        IF @r < 0 THROW 51000, 'Could not lock demand sets', 1;
        """;

    internal override string CollectDemandSetsLockSql => """
        DECLARE @r INT;
        EXEC @r = sys.sp_getapplock @Resource=N'IxIFlow.DemandSets', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000;
        IF @r < 0 THROW 51000, 'Could not lock demand sets', 1;
        """;

    internal override string ReadDemandsSql => """
        SELECT demand.DemandKey AS [Key], demand.DemandValue AS [Value], demand.ExpiresAtUtc AS [ExpiresAtUtc], demand.ValueExpiresAtUtc AS [ValueExpiresAtUtc], demand.Inherit AS [Inherit]
        FROM dbo.IxIFlowWorkflowInstances AS instance JOIN dbo.IxIFlowDemandSetRows AS demand ON demand.DemandSetId = instance.DemandSetId
        WHERE instance.InstanceId = @InstanceId
        UNION ALL
        SELECT 'host', PinnedHost, PinExpiresAtUtc, NULL, PinInherit FROM dbo.IxIFlowWorkflowInstances
        WHERE InstanceId = @InstanceId AND PinnedHost IS NOT NULL
        """;

    internal override string ReadDemandSetSql => """
        SELECT DemandKey AS [Key], DemandValue AS [Value], ExpiresAtUtc AS [ExpiresAtUtc], ValueExpiresAtUtc AS [ValueExpiresAtUtc], Inherit AS [Inherit]
        FROM dbo.IxIFlowDemandSetRows WHERE DemandSetId = @DemandSetId
        """;

    // A key-range lock on an absent header can also cover other, different new keys.
    // Use the digest as the lock resource instead, and avoid a transaction-long lock for
    // committed sets. Collection cannot remove a header while the shared lifecycle lock is held.
    internal override string InsertDemandSetSql => """
        IF NOT EXISTS (SELECT 1 FROM dbo.IxIFlowDemandSets WHERE DemandSetId = CONVERT(VARCHAR(64), @DemandSetId))
        BEGIN
            DECLARE @r INT, @resource NVARCHAR(255) = N'IxIFlow.DemandSet:' + @DemandSetId;
            EXEC @r = sys.sp_getapplock @Resource=@resource, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000;
            IF @r = -3 THROW 51001, 'Demand set lock deadlock', 1;
            IF @r < 0 THROW 51000, 'Could not lock demand set', 1;
            INSERT INTO dbo.IxIFlowDemandSets (DemandSetId)
            OUTPUT inserted.DemandSetId
            SELECT @DemandSetId
            WHERE NOT EXISTS (SELECT 1 FROM dbo.IxIFlowDemandSets WHERE DemandSetId = CONVERT(VARCHAR(64), @DemandSetId));
        END;
        """;
    internal override string InsertDemandSql => """
        INSERT INTO dbo.IxIFlowDemandSetRows (DemandSetId, DemandKey, DemandValue, ExpiresAtUtc, ValueExpiresAtUtc, Inherit)
        VALUES (@DemandSetId, @Key, @Value, @ExpiresAtUtc, @ValueExpiresAtUtc, @Inherit)
        """;

    internal override string CollectDemandSetsSql => """
        DELETE FROM dbo.IxIFlowDemandSets WHERE DemandSetId IN (
            SELECT TOP (@Limit) sets.DemandSetId FROM dbo.IxIFlowDemandSets AS sets
            WHERE NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances AS instance WHERE instance.DemandSetId = sets.DemandSetId)
            ORDER BY sets.DemandSetId);
        """;
}

using System.Data;
using Dapper;

namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    // A TVP has no column statistics and can retain the first poll's one-row estimate.
    // Materialize this call's snapshot, including its ordinal key identity, once.
    private const string CapabilitySnapshotSql = """
        CREATE TABLE #ClaimCapabilities (
            capability_key NVARCHAR(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
            capability_value NVARCHAR(MAX) COLLATE Latin1_General_100_BIN2 NOT NULL,
            capability_key_bytes INT NOT NULL,
            PRIMARY KEY (capability_key, capability_key_bytes)
        );
        INSERT INTO #ClaimCapabilities SELECT capability_key, capability_value, capability_key_bytes FROM @Capabilities;
        """;

    private const string CapabilityKeyJoin = """
        capability.capability_key = demand.DemandKey
        AND capability.capability_key_bytes = DATALENGTH(demand.DemandKey)
        """;

    private const string UnmetDemand = """
        (demand.ExpiresAtUtc IS NULL OR demand.ExpiresAtUtc > @Now)
        AND (capability.capability_key IS NULL OR (demand.DemandValue IS NOT NULL
            AND (capability.capability_value <> demand.DemandValue
                OR DATALENGTH(capability.capability_value) <> DATALENGTH(demand.DemandValue))))
        """;

    internal override string RunnableSql => $"""
        {CapabilitySnapshotSql}
        -- This statement boundary prevents a correlated anti-join from rescanning
        -- the snapshot for every instance. Duplicated blocked IDs are harmless.
        SELECT demand.InstanceId INTO #ClaimBlocked
        FROM dbo.IxIFlowWorkflowDemands AS demand
        LEFT HASH JOIN #ClaimCapabilities AS capability ON {CapabilityKeyJoin}
        WHERE {UnmetDemand}
        OPTION (RECOMPILE);

        SELECT TOP (@Limit) instance.InstanceId AS InstanceId, instance.RunnableAtUtc AS RunnableAtUtc
        FROM dbo.IxIFlowWorkflowInstances AS instance
        WHERE instance.RunnableAtUtc <= @Now
          AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowLeases AS lease
              WHERE lease.InstanceId = instance.InstanceId AND lease.ExpiresAtUtc > @Now)
          AND NOT EXISTS (SELECT 1 FROM #ClaimBlocked AS blocked WHERE blocked.InstanceId = instance.InstanceId)
        ORDER BY CASE WHEN instance.LastHostId = @HostId THEN 0 ELSE 1 END,
            instance.RunnableAtUtc, instance.InstanceId
        OPTION (HASH JOIN, RECOMPILE);
        DROP TABLE #ClaimBlocked;
        DROP TABLE #ClaimCapabilities;
        """;

    internal override string ClaimEligibilitySql => $"""
        {CapabilitySnapshotSql}
        SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId)
            OR EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances AS instance
                WHERE instance.InstanceId = @InstanceId
                  AND instance.Status NOT IN ('Completed', 'Failed', 'Cancelled', 'Terminated', 'TimedOut')
                  AND (instance.RunnableAtUtc <= @Now OR (@Operator = 1
                      AND (instance.Status = 'NeedsResolution' OR JSON_QUERY(instance.StateJson, '$.ExecutionStateJson.OperatorStop') IS NOT NULL)))
                  AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowDemands AS demand
                      LEFT JOIN #ClaimCapabilities AS capability ON {CapabilityKeyJoin}
                      WHERE demand.InstanceId = instance.InstanceId AND {UnmetDemand})) THEN 1 ELSE 0 END;
        DROP TABLE #ClaimCapabilities;
        """;

    internal override string SetLeaseHostSql => """
        UPDATE dbo.IxIFlowWorkflowLeases SET HostId = @HostId WHERE InstanceId = @InstanceId
        """;

    internal override string StampHostSql => """
        UPDATE dbo.IxIFlowWorkflowInstances SET LastHostId =
            (SELECT HostId FROM dbo.IxIFlowWorkflowLeases WHERE InstanceId = @InstanceId)
        WHERE InstanceId = @InstanceId
        """;

    internal override DynamicParameters PlacementParameters(ClaimQuery query, DateTime now, int limit)
    {
        var table = new DataTable();
        table.Columns.Add("capability_key", typeof(string));
        table.Columns.Add("capability_value", typeof(string));
        table.Columns.Add("capability_key_bytes", typeof(int));
        foreach (var capability in query.Capabilities) table.Rows.Add(capability.Key, capability.Value, capability.Key.Length * 2);
        var parameters = WithTimes(new { query.HostId, Limit = limit }, ("Now", now));
        parameters.Add("Capabilities", table.AsTableValuedParameter("dbo.IxIFlowClaimCapabilities"));
        return parameters;
    }
}

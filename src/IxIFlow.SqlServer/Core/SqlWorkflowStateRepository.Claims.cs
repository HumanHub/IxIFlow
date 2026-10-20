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
            AND (demand.ValueExpiresAtUtc IS NULL OR demand.ValueExpiresAtUtc > @Now)
            AND (capability.capability_value <> demand.DemandValue
                OR DATALENGTH(capability.capability_value) <> DATALENGTH(demand.DemandValue))))
        """;

    private const string PinMatches = """
        (instance.PinnedHost IS NULL OR instance.PinExpiresAtUtc <= @Now
            OR (instance.PinnedHost = @CapabilityHost AND DATALENGTH(instance.PinnedHost) = DATALENGTH(@CapabilityHost)))
        """;

    internal override string RunnableSql => $"""
        {CapabilitySnapshotSql}
        -- Only distinct sets and their rows participate in capability matching.
        SELECT sets.DemandSetId INTO #ClaimMatched
        FROM dbo.IxIFlowDemandSets AS sets
        WHERE (SELECT TOP (1) 1 FROM dbo.IxIFlowWorkflowInstances AS due WITH (FORCESEEK, INDEX(IX_IxIFlowInstances_SetRunnable))
                WHERE due.DemandSetId = sets.DemandSetId AND due.RunnableAtUtc <= @Now
                ORDER BY due.RunnableAtUtc, due.InstanceId) = 1
          AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowDemandSetRows AS demand
            LEFT JOIN #ClaimCapabilities AS capability ON {CapabilityKeyJoin}
            WHERE demand.DemandSetId = sets.DemandSetId AND {UnmetDemand})
        OPTION (RECOMPILE);
        CREATE UNIQUE CLUSTERED INDEX IX_ClaimMatched ON #ClaimMatched (DemandSetId);

        -- A per-set ordered limit bounds the final merge. FORCESEEK prevents a
        -- demand-mismatching population from becoming an instance-table scan.
        WITH candidates AS (
            SELECT own.*, 0 AS Affinity FROM #ClaimMatched AS matched
            CROSS APPLY (
                SELECT TOP (@Limit) instance.InstanceId, instance.RunnableAtUtc
                FROM dbo.IxIFlowWorkflowInstances AS instance WITH (FORCESEEK, INDEX(IX_IxIFlowInstances_SetAffinity))
                WHERE instance.DemandSetId = matched.DemandSetId AND instance.LastHostId = @HostId
                  AND instance.RunnableAtUtc <= @Now AND {PinMatches}
                  AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowLeases AS lease
                      WHERE lease.InstanceId = instance.InstanceId AND lease.ExpiresAtUtc > @Now)
                ORDER BY instance.RunnableAtUtc, instance.InstanceId) AS own
            UNION ALL
            SELECT other.*, 1 AS Affinity FROM #ClaimMatched AS matched
            CROSS APPLY (
                SELECT TOP (@Limit) instance.InstanceId, instance.RunnableAtUtc
                FROM dbo.IxIFlowWorkflowInstances AS instance WITH (FORCESEEK, INDEX(IX_IxIFlowInstances_SetRunnable))
                WHERE instance.DemandSetId = matched.DemandSetId
                  AND (instance.LastHostId IS NULL OR instance.LastHostId <> @HostId)
                  AND instance.RunnableAtUtc <= @Now AND {PinMatches}
                  AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowLeases AS lease
                      WHERE lease.InstanceId = instance.InstanceId AND lease.ExpiresAtUtc > @Now)
                ORDER BY instance.RunnableAtUtc, instance.InstanceId) AS other)
        -- Under read committed, affinity can move between the two seeks.
        -- Deduplicate the bounded merge before applying the global limit.
        SELECT TOP (@Limit) InstanceId, MIN(RunnableAtUtc) AS RunnableAtUtc FROM candidates
        GROUP BY InstanceId
        ORDER BY MIN(Affinity), MIN(RunnableAtUtc), InstanceId
        OPTION (LOOP JOIN, RECOMPILE);
        DROP TABLE #ClaimMatched;
        DROP TABLE #ClaimCapabilities;
        """;

    internal override string ClaimEligibilitySql => $"""
        {CapabilitySnapshotSql}
        SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId)
            OR EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances AS instance
                WHERE instance.InstanceId = @InstanceId
                  AND instance.Status NOT IN ('Completed', 'Failed', 'Cancelled', 'Terminated', 'TimedOut')
                  AND (instance.RunnableAtUtc <= @Now OR (@Operator = 1
                      AND (instance.Status IN ('NeedsResolution', 'Paused') OR JSON_QUERY(instance.StateJson, '$.ExecutionStateJson.OperatorStop') IS NOT NULL)))
                  AND {PinMatches}
                  AND NOT EXISTS (SELECT 1 FROM dbo.IxIFlowDemandSetRows AS demand
                      LEFT JOIN #ClaimCapabilities AS capability ON {CapabilityKeyJoin}
                      WHERE demand.DemandSetId = instance.DemandSetId AND {UnmetDemand})) THEN 1 ELSE 0 END;
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
        var parameters = WithTimes(new { query.HostId, Limit = limit, CapabilityHost = query.Capabilities.GetValueOrDefault("host") }, ("Now", now));
        parameters.Add("Capabilities", table.AsTableValuedParameter("dbo.IxIFlowClaimCapabilities"));
        return parameters;
    }
}

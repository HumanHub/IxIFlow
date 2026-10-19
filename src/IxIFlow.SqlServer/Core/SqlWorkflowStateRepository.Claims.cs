using System.Data;
using System.Text.Json;
using Dapper;

namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    private const string MatchingDemands = """
        NOT EXISTS (
            SELECT 1 FROM dbo.IxIFlowWorkflowDemands AS demand
            WHERE demand.InstanceId = instance.InstanceId
              AND (demand.ExpiresAtUtc IS NULL OR demand.ExpiresAtUtc > @Now)
              AND NOT EXISTS (SELECT 1 FROM @Capabilities AS capability
                  WHERE capability.capability_key = demand.DemandKey
                    AND capability.capability_key_bytes = DATALENGTH(demand.DemandKey)
                    AND (demand.DemandValue IS NULL OR (capability.capability_value = demand.DemandValue
                        AND DATALENGTH(capability.capability_value) = DATALENGTH(demand.DemandValue)))))
        """;

    internal override string RunnableSql(bool affinity) => $"""
        SELECT TOP (@Limit) instance.InstanceId AS InstanceId, instance.RunnableAtUtc AS RunnableAtUtc
        FROM dbo.IxIFlowWorkflowInstances AS instance
        LEFT JOIN dbo.IxIFlowWorkflowLeases AS lease ON lease.InstanceId = instance.InstanceId
        WHERE instance.RunnableAtUtc <= @Now
          AND (lease.InstanceId IS NULL OR lease.ExpiresAtUtc <= @Now)
          AND {(affinity ? "instance.LastHostId = @HostId" : "(instance.LastHostId IS NULL OR instance.LastHostId <> @HostId)")}
          AND {MatchingDemands}
        ORDER BY instance.RunnableAtUtc, instance.InstanceId
        """;

    internal override string ClaimEligibilitySql => $"""
        SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId = @InstanceId)
            OR EXISTS (SELECT 1 FROM dbo.IxIFlowWorkflowInstances AS instance
                WHERE instance.InstanceId = @InstanceId
                  AND (instance.RunnableAtUtc <= @Now OR (@Operator = 1 AND instance.RunnableAtUtc IS NULL
                      AND (instance.Status = 'NeedsResolution' OR JSON_QUERY(instance.StateJson, '$.ExecutionStateJson.OperatorStop') IS NOT NULL)))
                  AND {MatchingDemands}) THEN 1 ELSE 0 END
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

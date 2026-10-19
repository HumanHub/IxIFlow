using System.Data;
using System.Text.Json;
using Dapper;

namespace IxIFlow.Core;

public sealed partial class PostgreSqlWorkflowStateRepository
{
    private const string MatchingDemands = """
        NOT EXISTS (
            SELECT 1 FROM ixiflow_workflow_demands AS demand
            WHERE demand.instance_id = instance.instance_id
              AND (demand.expires_at_utc IS NULL OR demand.expires_at_utc > @Now)
              AND NOT EXISTS (SELECT 1 FROM unnest(CAST(@CapabilityKeys AS text[]), CAST(@CapabilityValues AS text[])) AS capability(key, value)
                  WHERE capability.key COLLATE "C" = demand.demand_key
                    AND (demand.demand_value IS NULL OR capability.value COLLATE "C" = demand.demand_value)))
        """;

    internal override string RunnableSql(bool affinity) => $"""
        SELECT instance.instance_id AS InstanceId, instance.runnable_at_utc AS RunnableAtUtc
        FROM ixiflow_workflow_instances AS instance
        LEFT JOIN ixiflow_workflow_leases AS lease ON lease.instance_id = instance.instance_id
        WHERE instance.runnable_at_utc <= @Now
          AND (lease.instance_id IS NULL OR lease.expires_at_utc <= @Now)
          AND {(affinity ? "instance.last_host_id = @HostId" : "(instance.last_host_id IS NULL OR instance.last_host_id <> @HostId)")}
          AND {MatchingDemands}
        ORDER BY instance.runnable_at_utc, instance.instance_id
        LIMIT @Limit
        """;

    internal override string ClaimEligibilitySql => $"""
        SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId)
            OR EXISTS (SELECT 1 FROM ixiflow_workflow_instances AS instance
                WHERE instance.instance_id = @InstanceId
                  AND (instance.runnable_at_utc <= @Now OR (@Operator AND instance.runnable_at_utc IS NULL
                      AND (instance.status = 'NeedsResolution' OR jsonb_typeof(instance.state_json::jsonb -> 'ExecutionStateJson' -> 'OperatorStop') = 'object')))
                  AND {MatchingDemands}) THEN 1 ELSE 0 END
        """;

    internal override string SetLeaseHostSql => """
        UPDATE ixiflow_workflow_leases SET host_id = @HostId WHERE instance_id = @InstanceId
        """;

    internal override string StampHostSql => """
        UPDATE ixiflow_workflow_instances SET last_host_id =
            (SELECT host_id FROM ixiflow_workflow_leases WHERE instance_id = @InstanceId)
        WHERE instance_id = @InstanceId
        """;

    internal override DynamicParameters PlacementParameters(ClaimQuery query, DateTime now, int limit)
    {
        return WithTimes(new { query.HostId, Limit = limit,
            CapabilityKeys = query.Capabilities.Keys.ToArray(),
            CapabilityValues = query.Capabilities.Values.ToArray() }, ("Now", now));
    }
}

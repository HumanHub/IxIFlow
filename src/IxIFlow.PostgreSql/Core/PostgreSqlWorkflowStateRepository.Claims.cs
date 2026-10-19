using Dapper;

namespace IxIFlow.Core;

public sealed partial class PostgreSqlWorkflowStateRepository
{
    private const string CapabilitySnapshotSql = """
        capabilities AS MATERIALIZED (
            SELECT key COLLATE "C" AS key, value COLLATE "C" AS value
            FROM unnest(CAST(@CapabilityKeys AS text[]), CAST(@CapabilityValues AS text[])) AS snapshot(key, value))
        """;

    private const string UnmetDemand = """
        (demand.expires_at_utc IS NULL OR demand.expires_at_utc > @Now)
        AND (capability.key IS NULL OR (demand.demand_value IS NOT NULL AND capability.value <> demand.demand_value))
        """;

    internal override string RunnableSql => $"""
        WITH {CapabilitySnapshotSql},
        blocked AS MATERIALIZED (
            SELECT demand.instance_id
            FROM ixiflow_workflow_demands AS demand
            LEFT JOIN capabilities AS capability ON capability.key = demand.demand_key
            WHERE {UnmetDemand})
        SELECT instance.instance_id AS InstanceId, instance.runnable_at_utc AS RunnableAtUtc
        FROM ixiflow_workflow_instances AS instance
        WHERE instance.runnable_at_utc <= @Now
          AND NOT EXISTS (SELECT 1 FROM ixiflow_workflow_leases AS lease
              WHERE lease.instance_id = instance.instance_id AND lease.expires_at_utc > @Now)
          AND NOT EXISTS (SELECT 1 FROM blocked WHERE blocked.instance_id = instance.instance_id)
        ORDER BY CASE WHEN instance.last_host_id = @HostId THEN 0 ELSE 1 END,
            instance.runnable_at_utc, instance.instance_id
        LIMIT @Limit
        """;

    internal override string ClaimEligibilitySql => $"""
        WITH {CapabilitySnapshotSql}
        SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId)
            OR EXISTS (SELECT 1 FROM ixiflow_workflow_instances AS instance
                WHERE instance.instance_id = @InstanceId
                  AND instance.status NOT IN ('Completed', 'Failed', 'Cancelled', 'Terminated', 'TimedOut')
                  AND (instance.runnable_at_utc <= @Now OR (@Operator
                      AND (instance.status = 'NeedsResolution' OR jsonb_typeof(instance.state_json::jsonb -> 'ExecutionStateJson' -> 'OperatorStop') = 'object')))
                  AND NOT EXISTS (SELECT 1 FROM ixiflow_workflow_demands AS demand
                      LEFT JOIN capabilities AS capability ON capability.key = demand.demand_key
                      WHERE demand.instance_id = instance.instance_id AND {UnmetDemand})) THEN 1 ELSE 0 END
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

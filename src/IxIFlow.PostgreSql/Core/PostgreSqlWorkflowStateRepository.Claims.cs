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
        AND (capability.key IS NULL OR (demand.demand_value IS NOT NULL
            AND (demand.value_expires_at_utc IS NULL OR demand.value_expires_at_utc > @Now) AND capability.value <> demand.demand_value))
        """;

    private const string PinMatches = """
        (instance.pinned_host IS NULL OR instance.pin_expires_at_utc <= @Now OR instance.pinned_host = @CapabilityHost)
        """;

    internal override string RunnableSql => $"""
        WITH {CapabilitySnapshotSql},
        matched AS MATERIALIZED (
            SELECT sets.demand_set_id FROM ixiflow_demand_sets AS sets
            -- A scalar ordered lookup cannot be flattened into a semi join over all instances.
            WHERE (SELECT 1 FROM ixiflow_workflow_instances AS due
                WHERE due.demand_set_id = sets.demand_set_id AND due.runnable_at_utc <= @Now
                ORDER BY due.runnable_at_utc, due.instance_id COLLATE "C" LIMIT 1) = 1
              AND NOT EXISTS (SELECT 1 FROM ixiflow_demand_set_rows AS demand
                LEFT JOIN capabilities AS capability ON capability.key = demand.demand_key
                WHERE demand.demand_set_id = sets.demand_set_id AND {UnmetDemand})),
        -- A scalar lookup keeps lease checking correlated to each matching
        -- candidate; an anti join could hash every unrelated instance's lease.
        candidates AS (
            SELECT own.*, 0 AS affinity FROM matched
            CROSS JOIN LATERAL (
                SELECT instance.instance_id, instance.runnable_at_utc
                FROM ixiflow_workflow_instances AS instance
                WHERE instance.demand_set_id = matched.demand_set_id AND instance.last_host_id = @HostId
                  AND instance.runnable_at_utc <= @Now AND {PinMatches}
                  AND COALESCE((SELECT lease.expires_at_utc <= @Now FROM ixiflow_workflow_leases AS lease
                      WHERE lease.instance_id = instance.instance_id), true)
                ORDER BY instance.runnable_at_utc, instance.instance_id LIMIT @ScanLimit) AS own
            UNION ALL
            SELECT other.*, 1 AS affinity FROM matched
            CROSS JOIN LATERAL (
                SELECT instance.instance_id, instance.runnable_at_utc
                FROM ixiflow_workflow_instances AS instance
                WHERE instance.demand_set_id = matched.demand_set_id
                  AND (instance.last_host_id IS NULL OR instance.last_host_id <> @HostId)
                  AND instance.runnable_at_utc <= @Now AND {PinMatches}
                  AND COALESCE((SELECT lease.expires_at_utc <= @Now FROM ixiflow_workflow_leases AS lease
                      WHERE lease.instance_id = instance.instance_id), true)
                ORDER BY instance.runnable_at_utc, instance.instance_id LIMIT @ScanLimit) AS other)
        SELECT instance_id AS InstanceId, runnable_at_utc AS RunnableAtUtc FROM candidates
        ORDER BY affinity, runnable_at_utc, instance_id LIMIT @Limit OFFSET @ScanOffset
        """;

    internal override string ClaimEligibilitySql => $"""
        WITH {CapabilitySnapshotSql}
        SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId)
            OR EXISTS (SELECT 1 FROM ixiflow_workflow_instances AS instance
                WHERE instance.instance_id = @InstanceId
                  AND instance.status NOT IN ('Completed', 'Failed', 'Cancelled', 'Terminated', 'TimedOut')
                  AND (instance.runnable_at_utc <= @Now OR (@Operator
                      AND (instance.status IN ('NeedsResolution', 'Paused') OR jsonb_typeof(instance.state_json::jsonb -> 'ExecutionStateJson' -> 'OperatorStop') = 'object')))
                  AND {PinMatches}
                  AND NOT EXISTS (SELECT 1 FROM ixiflow_demand_set_rows AS demand
                      LEFT JOIN capabilities AS capability ON capability.key = demand.demand_key
                      WHERE demand.demand_set_id = instance.demand_set_id AND {UnmetDemand})) THEN 1 ELSE 0 END
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
        return WithScan(WithTimes(new { query.HostId, Limit = limit, CapabilityHost = query.Capabilities.GetValueOrDefault("host"),
            CapabilityKeys = query.Capabilities.Keys.ToArray(),
            CapabilityValues = query.Capabilities.Values.ToArray() }, ("Now", now)), query, limit);
    }
}

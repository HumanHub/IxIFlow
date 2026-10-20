using System.Text.Json;
using Dapper;

namespace IxIFlow.Core;

public sealed partial class SqliteWorkflowStateRepository
{
    // TEXT affinity lets SQLite build the automatic key index on this CTE.
    // json_extract alone has no affinity and causes a snapshot scan per demand.
    private const string CapabilitySnapshotSql = """
        capabilities AS MATERIALIZED (
            SELECT CAST(json_extract(value, '$.Key') AS TEXT) COLLATE BINARY AS key,
                CAST(json_extract(value, '$.Value') AS TEXT) COLLATE BINARY AS value
            FROM json_each(@Capabilities))
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
            WHERE (SELECT 1 FROM ixiflow_workflow_instances AS due INDEXED BY ix_ixiflow_instances_set_runnable
                WHERE due.demand_set_id = sets.demand_set_id AND due.runnable_at_utc <= @Now
                ORDER BY due.runnable_at_utc, due.instance_id LIMIT 1) = 1
              AND NOT EXISTS (SELECT 1 FROM ixiflow_demand_set_rows AS demand
                LEFT JOIN capabilities AS capability ON capability.key = demand.demand_key
                WHERE demand.demand_set_id = sets.demand_set_id AND {UnmetDemand}))
        SELECT instance.instance_id AS InstanceId, instance.runnable_at_utc AS RunnableAtUtc
        FROM matched CROSS JOIN ixiflow_workflow_instances AS instance INDEXED BY ix_ixiflow_instances_set_runnable
        WHERE instance.demand_set_id = matched.demand_set_id AND instance.runnable_at_utc <= @Now
          AND {PinMatches}
          AND NOT EXISTS (SELECT 1 FROM ixiflow_workflow_leases AS lease
              WHERE lease.instance_id = instance.instance_id AND lease.expires_at_utc > @Now)
        ORDER BY CASE WHEN instance.last_host_id = @HostId THEN 0 ELSE 1 END,
            instance.runnable_at_utc, instance.instance_id LIMIT @Limit
        """;

    internal override string ClaimEligibilitySql => $"""
        WITH {CapabilitySnapshotSql}
        SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId)
            OR EXISTS (SELECT 1 FROM ixiflow_workflow_instances AS instance
                WHERE instance.instance_id = @InstanceId
                  AND instance.status NOT IN ('Completed', 'Failed', 'Cancelled', 'Terminated', 'TimedOut')
                  AND (instance.runnable_at_utc <= @Now OR (@Operator
                      AND (instance.status = 'NeedsResolution' OR json_type(instance.state_json, '$.ExecutionStateJson.OperatorStop') = 'object')))
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
        return WithTimes(new { query.HostId, Limit = limit, CapabilityHost = query.Capabilities.GetValueOrDefault("host"),
            Capabilities = JsonSerializer.Serialize(query.Capabilities.Select(item => new { item.Key, item.Value })) },
            ("Now", now));
    }
}

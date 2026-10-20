namespace IxIFlow.Core;

public sealed partial class SqliteWorkflowStateRepository
{
    internal override string AllDemandSetsSql => """
        SELECT sets.demand_set_id AS DemandSetId, demand.demand_key AS [Key],
            demand.demand_value AS Value, demand.expires_at_utc AS ExpiresAtUtc, demand.value_expires_at_utc AS ValueExpiresAtUtc, demand.inherit AS Inherit
        FROM ixiflow_demand_sets AS sets LEFT JOIN ixiflow_demand_set_rows AS demand
            ON demand.demand_set_id = sets.demand_set_id
        WHERE (SELECT 1 FROM ixiflow_workflow_instances AS due INDEXED BY ix_ixiflow_instances_set_runnable
                WHERE due.demand_set_id = sets.demand_set_id AND due.runnable_at_utc <= @Now
                ORDER BY due.runnable_at_utc, due.instance_id LIMIT 1) = 1
        """;

    internal override string UnschedulableSql => """
        WITH sets AS MATERIALIZED (SELECT json_extract(value, '$.Id') AS id, json_extract(value, '$.Failed') AS failed,
                json_extract(value, '$.Pins') AS pins FROM json_each(@Sets)),
        pins AS MATERIALIZED (SELECT sets.id, CAST(pin.value AS TEXT) COLLATE BINARY AS value FROM sets CROSS JOIN json_each(sets.pins) AS pin),
        candidates AS (SELECT instance.instance_id, instance.runnable_at_utc FROM sets
            CROSS JOIN ixiflow_workflow_instances AS instance INDEXED BY ix_ixiflow_instances_set_runnable
            WHERE sets.failed = 1 AND instance.demand_set_id = sets.id AND instance.runnable_at_utc <= @Now
                  AND (@AfterTime IS NULL OR instance.runnable_at_utc > @AfterTime OR
                      (instance.runnable_at_utc = @AfterTime AND instance.instance_id > @AfterId))
                  AND COALESCE((SELECT lease.expires_at_utc <= @Now FROM ixiflow_workflow_leases AS lease
                      WHERE lease.instance_id = instance.instance_id), 1) UNION ALL SELECT instance.instance_id, instance.runnable_at_utc FROM sets
            CROSS JOIN ixiflow_workflow_instances AS instance INDEXED BY ix_ixiflow_instances_set_pin
            WHERE sets.failed = 0 AND instance.demand_set_id = sets.id AND instance.runnable_at_utc <= @Now
                  AND (@AfterTime IS NULL OR instance.runnable_at_utc > @AfterTime OR
                      (instance.runnable_at_utc = @AfterTime AND instance.instance_id > @AfterId))
                  AND COALESCE((SELECT lease.expires_at_utc <= @Now FROM ixiflow_workflow_leases AS lease
                      WHERE lease.instance_id = instance.instance_id), 1) AND instance.pinned_host IS NOT NULL
                  AND instance.pin_expires_at_utc IS NULL
                  AND NOT EXISTS (SELECT 1 FROM pins WHERE pins.id = sets.id AND pins.value = instance.pinned_host) UNION ALL SELECT instance.instance_id, instance.runnable_at_utc FROM sets
            CROSS JOIN ixiflow_workflow_instances AS instance INDEXED BY ix_ixiflow_instances_set_pin
            WHERE sets.failed = 0 AND instance.demand_set_id = sets.id AND instance.runnable_at_utc <= @Now
                  AND (@AfterTime IS NULL OR instance.runnable_at_utc > @AfterTime OR
                      (instance.runnable_at_utc = @AfterTime AND instance.instance_id > @AfterId))
                  AND COALESCE((SELECT lease.expires_at_utc <= @Now FROM ixiflow_workflow_leases AS lease
                      WHERE lease.instance_id = instance.instance_id), 1) AND instance.pinned_host IS NOT NULL
                  AND instance.pin_expires_at_utc > @Now
                  AND NOT EXISTS (SELECT 1 FROM pins WHERE pins.id = sets.id AND pins.value = instance.pinned_host)),
        page AS MATERIALIZED (SELECT * FROM candidates ORDER BY runnable_at_utc, instance_id LIMIT @Limit)
        SELECT instance.instance_id AS InstanceId, instance.incarnation AS Incarnation, instance.workflow_name AS WorkflowName,
            json_extract(instance.state_json, '$.WorkflowVersion') AS WorkflowVersion, page.runnable_at_utc AS RunnableAtUtc,
            instance.demand_set_id AS DemandSetId, instance.pinned_host AS PinnedHost,
            instance.pin_expires_at_utc AS PinExpiresAtUtc, instance.pin_inherit AS PinInherit
        FROM page CROSS JOIN ixiflow_workflow_instances AS instance
        WHERE instance.instance_id = page.instance_id
        ORDER BY page.runnable_at_utc, page.instance_id
        """;
}

namespace IxIFlow.Core;

public sealed partial class PostgreSqlWorkflowStateRepository
{
    internal override string AllDemandSetsSql => """
        SELECT sets.demand_set_id AS DemandSetId, demand.demand_key AS "Key",
            demand.demand_value AS Value, demand.expires_at_utc AS ExpiresAtUtc, demand.inherit AS Inherit
        FROM ixiflow_demand_sets AS sets LEFT JOIN ixiflow_demand_set_rows AS demand
            ON demand.demand_set_id = sets.demand_set_id
        """;

    internal override string UnschedulableSql => """
        WITH sets AS MATERIALIZED (SELECT (value->>'Id') COLLATE "C" AS id, (value->>'Failed')::boolean AS failed, value->'Pins' AS pins
            FROM jsonb_array_elements(CAST(@Sets AS jsonb))),
        pins AS MATERIALIZED (SELECT sets.id, pin.value COLLATE "C" AS value FROM sets
            CROSS JOIN LATERAL jsonb_array_elements_text(sets.pins) AS pin(value)),
        candidates AS (SELECT candidate.* FROM sets CROSS JOIN LATERAL (
                SELECT instance.instance_id, instance.runnable_at_utc FROM (
                    SELECT instance.instance_id, instance.runnable_at_utc FROM ixiflow_workflow_instances AS instance
                    WHERE instance.demand_set_id = sets.id AND instance.runnable_at_utc IS NOT NULL
                      AND instance.runnable_at_utc <= @Now
                      AND (CAST(@AfterTime AS timestamptz) IS NULL OR instance.runnable_at_utc > CAST(@AfterTime AS timestamptz) OR
                          (instance.runnable_at_utc = CAST(@AfterTime AS timestamptz) AND instance.instance_id > CAST(@AfterId AS text) COLLATE "C"))
                    -- Plan the complete set range before the lease filter and its LIMIT row goal.
                    -- Otherwise average set cardinality can favor walking the global runnable index.
                    ORDER BY instance.runnable_at_utc, instance.instance_id COLLATE "C" OFFSET 0) AS instance
                WHERE sets.failed AND COALESCE((SELECT lease.expires_at_utc <= @Now FROM ixiflow_workflow_leases AS lease
                      WHERE lease.instance_id = instance.instance_id), true)
                ORDER BY instance.runnable_at_utc, instance.instance_id COLLATE "C" LIMIT @Limit) AS candidate
            WHERE sets.failed UNION ALL SELECT candidate.* FROM sets CROSS JOIN LATERAL (
                SELECT instance.instance_id, instance.runnable_at_utc FROM (
                    -- Each arm implies its own partial index predicate. In particular, the null
                    -- arm must not cost a nullable expiry range against the whole schedulable set.
                    (SELECT instance.instance_id, instance.runnable_at_utc, instance.pinned_host
                    FROM ixiflow_workflow_instances AS instance
                    WHERE instance.demand_set_id = sets.id AND instance.pin_expires_at_utc IS NULL
                      AND instance.pinned_host IS NOT NULL AND instance.runnable_at_utc IS NOT NULL
                    ORDER BY instance.runnable_at_utc, instance.instance_id COLLATE "C" OFFSET 0)
                    UNION ALL
                    (SELECT instance.instance_id, instance.runnable_at_utc, instance.pinned_host
                    FROM ixiflow_workflow_instances AS instance
                    WHERE instance.demand_set_id = sets.id AND instance.pin_expires_at_utc > @Now
                      AND instance.pin_expires_at_utc IS NOT NULL
                      AND instance.pinned_host IS NOT NULL AND instance.runnable_at_utc IS NOT NULL
                    ORDER BY instance.pin_expires_at_utc, instance.runnable_at_utc, instance.instance_id COLLATE "C" OFFSET 0)
                    -- Keep runnable ordering outside these seeks so LIMIT cannot favor the set/runnable index.
                    OFFSET 0) AS instance
                WHERE NOT sets.failed AND instance.runnable_at_utc <= @Now
                  AND (CAST(@AfterTime AS timestamptz) IS NULL OR instance.runnable_at_utc > CAST(@AfterTime AS timestamptz) OR
                      (instance.runnable_at_utc = CAST(@AfterTime AS timestamptz) AND instance.instance_id > CAST(@AfterId AS text) COLLATE "C"))
                  AND COALESCE((SELECT lease.expires_at_utc <= @Now FROM ixiflow_workflow_leases AS lease
                      WHERE lease.instance_id = instance.instance_id), true)
                  AND NOT EXISTS (SELECT 1 FROM pins WHERE pins.id = sets.id AND pins.value = instance.pinned_host)
                ORDER BY instance.runnable_at_utc, instance.instance_id COLLATE "C" LIMIT @Limit) AS candidate
            WHERE NOT sets.failed),
        page AS MATERIALIZED (SELECT * FROM candidates ORDER BY runnable_at_utc, instance_id COLLATE "C" LIMIT @Limit)
        SELECT instance.instance_id AS InstanceId, instance.workflow_name AS WorkflowName,
            instance.state_json::jsonb ->> 'WorkflowVersion' AS WorkflowVersion, page.runnable_at_utc AS RunnableAtUtc,
            instance.demand_set_id AS DemandSetId, instance.pinned_host AS PinnedHost,
            instance.pin_expires_at_utc AS PinExpiresAtUtc, instance.pin_inherit AS PinInherit
        FROM page CROSS JOIN LATERAL (SELECT * FROM ixiflow_workflow_instances
            WHERE instance_id = page.instance_id OFFSET 0) AS instance
        ORDER BY page.runnable_at_utc, page.instance_id COLLATE "C"
        """;
}

namespace IxIFlow.Core;

public sealed partial class PostgreSqlWorkflowStateRepository
{
    private const string DemandsSchema = """
        CREATE TABLE ixiflow_demand_sets (demand_set_id varchar(64) COLLATE "C" NOT NULL PRIMARY KEY);
        CREATE TABLE ixiflow_demand_set_rows (
            demand_set_id varchar(64) COLLATE "C" NOT NULL REFERENCES ixiflow_demand_sets (demand_set_id) ON DELETE CASCADE,
            demand_key varchar(400) COLLATE "C" NOT NULL,
            demand_value text COLLATE "C" NULL,
            expires_at_utc timestamptz NULL,
            value_expires_at_utc timestamptz NULL,
            inherit boolean NOT NULL,
            PRIMARY KEY (demand_set_id, demand_key)
        );
        """;

    internal override string CreateDemandSetsLockSql => """
        SELECT pg_advisory_xact_lock_shared(7843921052389);
        """;

    internal override string CollectDemandSetsLockSql => """
        SELECT pg_advisory_xact_lock(7843921052389);
        """;

    internal override string ReadDemandsSql => """
        SELECT demand.demand_key AS "Key", demand.demand_value AS "Value", demand.value_expires_at_utc AS ValueExpiresAtUtc, demand.expires_at_utc AS "ExpiresAtUtc", demand.inherit AS "Inherit"
        FROM ixiflow_workflow_instances AS instance JOIN ixiflow_demand_set_rows AS demand ON demand.demand_set_id = instance.demand_set_id
        WHERE instance.instance_id = @InstanceId
        UNION ALL
        SELECT 'host', pinned_host, NULL, pin_expires_at_utc, pin_inherit FROM ixiflow_workflow_instances
        WHERE instance_id = @InstanceId AND pinned_host IS NOT NULL
        """;

    internal override string ReadDemandSetSql => """
        SELECT demand_key AS "Key", demand_value AS "Value", value_expires_at_utc AS ValueExpiresAtUtc, expires_at_utc AS "ExpiresAtUtc", inherit AS "Inherit"
        FROM ixiflow_demand_set_rows WHERE demand_set_id = @DemandSetId
        """;

    internal override string InsertDemandSetSql => """
        INSERT INTO ixiflow_demand_sets (demand_set_id) VALUES (@DemandSetId)
        ON CONFLICT (demand_set_id) DO NOTHING
        RETURNING demand_set_id;
        """;
    internal override string InsertDemandSql => """
        INSERT INTO ixiflow_demand_set_rows (demand_set_id, demand_key, demand_value, expires_at_utc, value_expires_at_utc, inherit)
        VALUES (@DemandSetId, @Key, @Value, @ExpiresAtUtc, @ValueExpiresAtUtc, @Inherit)
        """;

    internal override string CollectDemandSetsSql => """
        DELETE FROM ixiflow_demand_sets WHERE demand_set_id IN (
            SELECT sets.demand_set_id FROM ixiflow_demand_sets AS sets
            WHERE NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances AS instance WHERE instance.demand_set_id = sets.demand_set_id)
            ORDER BY sets.demand_set_id LIMIT @Limit);
        """;
}

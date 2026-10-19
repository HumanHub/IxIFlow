namespace IxIFlow.Core;

public sealed partial class PostgreSqlWorkflowStateRepository
{
    private const string DemandsSchema = """
        CREATE TABLE ixiflow_workflow_demands (
            instance_id varchar(100) COLLATE "C" NOT NULL,
            demand_key varchar(400) COLLATE "C" NOT NULL,
            demand_value text COLLATE "C" NULL,
            expires_at_utc timestamp with time zone NULL,
            inherit boolean NOT NULL,
            PRIMARY KEY (instance_id, demand_key)
        );
        """;

    internal override string ReadDemandsSql => """
        SELECT demand_key AS "Key", demand_value AS "Value", expires_at_utc AS "ExpiresAtUtc", inherit AS "Inherit" FROM ixiflow_workflow_demands WHERE instance_id = @InstanceId ORDER BY demand_key
        """;

    internal override string InsertDemandSql => """
        INSERT INTO ixiflow_workflow_demands (instance_id, demand_key, demand_value, expires_at_utc, inherit)
        VALUES (@InstanceId, @Key, @Value, @ExpiresAtUtc, @Inherit)
        """;
}

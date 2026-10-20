using Dapper;

namespace IxIFlow.Core;

public partial class PostgreSqlWorkflowStateRepository
{
    // Arrays avoid a parameter per element; bound each transaction's lock and result set.
    internal override int RenewalBatchSize => 1000;

    internal override string RenewalSql(IReadOnlyList<ExecutionLeaseRenewal> leases, DynamicParameters parameters)
    {
        parameters.Add("InstanceIds", leases.Select(lease => lease.InstanceId).ToArray());
        parameters.Add("Tokens", leases.Select(lease => lease.Token).ToArray());
        parameters.Add("Milliseconds", leases.Select(lease => (int)Math.Ceiling(lease.Duration.TotalMilliseconds)).ToArray());
        return """
            WITH requests AS MATERIALIZED (
                SELECT * FROM unnest(@InstanceIds::text[], @Tokens::text[], @Milliseconds::int[])
                    WITH ORDINALITY AS r(instance_id, token, milliseconds, position)
            ), commands AS MATERIALIZED (
                SELECT instance_id, cancellation_json FROM ixiflow_workflow_instances
                WHERE instance_id = ANY(@InstanceIds) ORDER BY instance_id FOR UPDATE
            ), held AS MATERIALIZED (
                SELECT l.instance_id, l.token, l.expires_at_utc, c.cancellation_json
                FROM ixiflow_workflow_leases l LEFT JOIN commands c ON c.instance_id = l.instance_id
                WHERE l.instance_id = ANY(@InstanceIds) ORDER BY l.instance_id FOR UPDATE OF l
            ), stamp AS MATERIALIZED (
                SELECT clock_timestamp() AS now FROM (SELECT count(*) FROM held) AS locked
            ), renewed AS (
                UPDATE ixiflow_workflow_leases l
                SET expires_at_utc = stamp.now + r.milliseconds * INTERVAL '1 millisecond'
                FROM requests r JOIN held h ON h.instance_id = r.instance_id CROSS JOIN stamp
                WHERE l.instance_id = r.instance_id AND h.token = r.token AND h.expires_at_utc > stamp.now
                    AND COALESCE((h.cancellation_json::jsonb ->> 'Mode')::int, 0) <> 1
                RETURNING l.instance_id
            )
            SELECT CASE WHEN renewed.instance_id IS NOT NULL THEN 0
                WHEN h.token = r.token AND h.expires_at_utc > stamp.now
                    AND (h.cancellation_json::jsonb ->> 'Mode')::int = 1 THEN 1 ELSE 2 END AS Status,
                h.cancellation_json AS CancellationJson
            FROM requests r LEFT JOIN held h ON h.instance_id = r.instance_id
            LEFT JOIN renewed ON renewed.instance_id = r.instance_id CROSS JOIN stamp ORDER BY r.position;
            """;
    }
}

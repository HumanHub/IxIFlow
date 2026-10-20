using Dapper;

namespace IxIFlow.Core;

public partial class PostgreSqlWorkflowStateRepository
{
    // Arrays avoid a parameter per element; bound each transaction's lock and result set.
    internal override int RenewalBatchSize => 1000;

    internal override string RenewalSql(IReadOnlyList<ExecutionLeaseRenewal> leases,
        IReadOnlySet<string> terminations, DynamicParameters parameters)
    {
        parameters.Add("InstanceIds", leases.Select(lease => lease.InstanceId).ToArray());
        parameters.Add("Tokens", leases.Select(lease => lease.Token).ToArray());
        parameters.Add("Milliseconds", leases.Select(lease => (int)Math.Ceiling(lease.Duration.TotalMilliseconds)).ToArray());
        parameters.Add("Terminating", leases.Select(lease => terminations.Contains(lease.InstanceId)).ToArray());
        return """
            WITH requests AS MATERIALIZED (
                SELECT * FROM unnest(@InstanceIds::text[], @Tokens::text[], @Milliseconds::int[], @Terminating::boolean[])
                    WITH ORDINALITY AS r(instance_id, token, milliseconds, terminating, position)
            ), held AS MATERIALIZED (
                SELECT l.instance_id, l.token, l.expires_at_utc
                FROM ixiflow_workflow_leases l
                WHERE l.instance_id = ANY(@InstanceIds) ORDER BY l.instance_id FOR UPDATE OF l
            ), stamp AS MATERIALIZED (
                SELECT clock_timestamp() AS now FROM (SELECT count(*) FROM held) AS locked
            ), renewed AS (
                UPDATE ixiflow_workflow_leases l
                SET expires_at_utc = stamp.now + r.milliseconds * INTERVAL '1 millisecond'
                FROM requests r JOIN held h ON h.instance_id = r.instance_id CROSS JOIN stamp
                WHERE l.instance_id = r.instance_id AND h.token = r.token AND h.expires_at_utc > stamp.now
                    AND NOT r.terminating
                RETURNING l.instance_id
            )
            SELECT CASE WHEN renewed.instance_id IS NOT NULL THEN 0
                WHEN h.token = r.token AND h.expires_at_utc > stamp.now
                    AND r.terminating THEN 1 ELSE 2 END AS Status
            FROM requests r LEFT JOIN held h ON h.instance_id = r.instance_id
            LEFT JOIN renewed ON renewed.instance_id = r.instance_id CROSS JOIN stamp ORDER BY r.position;
            """;
    }
}

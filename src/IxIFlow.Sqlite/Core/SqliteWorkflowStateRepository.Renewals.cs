using Dapper;

namespace IxIFlow.Core;

public partial class SqliteWorkflowStateRepository
{
    // Also fits SQLite builds with the older 999-variable limit (3 per row plus the clock).
    internal override int RenewalBatchSize => 300;

    internal override string RenewalSql(IReadOnlyList<ExecutionLeaseRenewal> leases, DynamicParameters parameters)
    {
        var now = LocalNow();
        parameters.Add("Now", Timestamp(now));
        var values = new List<string>(leases.Count);
        for (var index = 0; index < leases.Count; index++)
        {
            parameters.Add($"Id{index}", leases[index].InstanceId);
            parameters.Add($"Token{index}", leases[index].Token);
            parameters.Add($"Until{index}", Timestamp(now.AddMilliseconds(Math.Ceiling(leases[index].Duration.TotalMilliseconds))));
            values.Add($"({index}, @Id{index}, @Token{index}, @Until{index})");
        }
        var requests = $"WITH requests(position, instance_id, token, until) AS (VALUES {string.Join(",", values)})";
        return $$"""
            {{requests}}
            UPDATE ixiflow_workflow_leases AS l
            SET expires_at_utc = (SELECT r.until FROM requests r WHERE r.instance_id = l.instance_id)
            WHERE expires_at_utc > @Now
                AND EXISTS (SELECT 1 FROM requests r WHERE r.instance_id = l.instance_id AND r.token = l.token)
                AND NOT EXISTS (SELECT 1 FROM ixiflow_workflow_instances i WHERE i.instance_id = l.instance_id
                    AND json_extract(i.cancellation_json, '$.Mode') = 1);
            {{requests}}
            SELECT CASE WHEN l.token = r.token AND l.expires_at_utc > @Now
                THEN CASE WHEN json_extract(i.cancellation_json, '$.Mode') = 1 THEN 1 ELSE 0 END ELSE 2 END AS Status,
                i.cancellation_json AS CancellationJson FROM requests r
                LEFT JOIN ixiflow_workflow_leases l ON l.instance_id = r.instance_id
                LEFT JOIN ixiflow_workflow_instances i ON i.instance_id = r.instance_id ORDER BY r.position;
            """;
    }
}

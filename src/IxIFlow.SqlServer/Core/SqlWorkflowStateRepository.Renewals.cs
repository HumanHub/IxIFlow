using Dapper;

namespace IxIFlow.Core;

public partial class SqlWorkflowStateRepository
{
    // Three parameters per request, leaving room below SQL Server's 2,100-parameter ceiling.
    internal override int RenewalBatchSize => 699;

    internal override string RenewalSql(IReadOnlyList<ExecutionLeaseRenewal> leases, DynamicParameters parameters)
    {
        var values = new List<string>(leases.Count);
        for (var index = 0; index < leases.Count; index++)
        {
            parameters.Add($"Id{index}", leases[index].InstanceId);
            parameters.Add($"Token{index}", leases[index].Token);
            parameters.Add($"Ms{index}", (int)Math.Ceiling(leases[index].Duration.TotalMilliseconds));
            values.Add($"({index}, @Id{index}, @Token{index}, @Ms{index})");
        }
        return $$"""
            DECLARE @requests TABLE (Position int PRIMARY KEY, InstanceId nvarchar(100) COLLATE Latin1_General_100_BIN2,
                Token nvarchar(100) COLLATE Latin1_General_100_BIN2, Milliseconds int);
            INSERT INTO @requests VALUES {{string.Join(",", values)}};
            -- Lock instances before leases, as claims, commits and termination do.
            DECLARE @commands TABLE (InstanceId nvarchar(100) COLLATE Latin1_General_100_BIN2 PRIMARY KEY, CancellationJson nvarchar(max));
            INSERT INTO @commands
                SELECT i.InstanceId, i.CancellationJson FROM dbo.IxIFlowWorkflowInstances i WITH (UPDLOCK, HOLDLOCK)
                JOIN @requests r ON r.InstanceId = i.InstanceId;
            DECLARE @held TABLE (Position int PRIMARY KEY, Token nvarchar(100) COLLATE Latin1_General_100_BIN2, ExpiresAtUtc datetime2);
            INSERT INTO @held
                SELECT r.Position, l.Token, l.ExpiresAtUtc FROM @requests r
                LEFT JOIN dbo.IxIFlowWorkflowLeases l WITH (UPDLOCK, HOLDLOCK) ON l.InstanceId = r.InstanceId;
            DECLARE @now datetime2 = SYSUTCDATETIME();
            UPDATE l SET ExpiresAtUtc = DATEADD(MILLISECOND, r.Milliseconds, @now)
                FROM dbo.IxIFlowWorkflowLeases l JOIN @requests r ON r.InstanceId = l.InstanceId
                JOIN @held h ON h.Position = r.Position
                LEFT JOIN @commands c ON c.InstanceId = r.InstanceId
                WHERE h.Token = r.Token AND h.ExpiresAtUtc > @now
                    AND COALESCE(JSON_VALUE(c.CancellationJson, '$.Mode'), '0') <> '1';
            SELECT CASE WHEN h.Token = r.Token AND h.ExpiresAtUtc > @now
                THEN CASE WHEN JSON_VALUE(c.CancellationJson, '$.Mode') = '1' THEN 1 ELSE 0 END ELSE 2 END AS Status,
                c.CancellationJson FROM @requests r JOIN @held h ON h.Position = r.Position
                LEFT JOIN @commands c ON c.InstanceId = r.InstanceId ORDER BY r.Position;
            """;
    }
}

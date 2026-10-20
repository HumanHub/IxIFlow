using Dapper;

namespace IxIFlow.Core;

public partial class SqlWorkflowStateRepository
{
    // Three parameters per request, leaving room below SQL Server's 2,100-parameter ceiling.
    internal override int RenewalBatchSize => 600;

    internal override string RenewalSql(IReadOnlyList<ExecutionLeaseRenewal> leases,
        IReadOnlySet<string> terminations, DynamicParameters parameters)
    {
        var values = new List<string>(leases.Count);
        foreach (var index in Enumerable.Range(0, leases.Count).OrderBy(index => leases[index].InstanceId, StringComparer.Ordinal))
        {
            parameters.Add($"Id{index}", leases[index].InstanceId);
            parameters.Add($"Token{index}", leases[index].Token);
            parameters.Add($"Ms{index}", (int)Math.Ceiling(leases[index].Duration.TotalMilliseconds));
            values.Add($"({index}, @Id{index}, @Token{index}, @Ms{index}, {(terminations.Contains(leases[index].InstanceId) ? 1 : 0)})");
        }
        return $$"""
            DECLARE @requests TABLE (Position int, InstanceId nvarchar(100) COLLATE Latin1_General_100_BIN2 PRIMARY KEY CLUSTERED,
                Token nvarchar(100) COLLATE Latin1_General_100_BIN2, Milliseconds int, Terminating bit);
            INSERT INTO @requests VALUES {{string.Join(",", values)}};
            -- Walk the clustered request keys before locking lease rows. Instance reads stay unlocked.
            DECLARE @held TABLE (Position int PRIMARY KEY, Token nvarchar(100) COLLATE Latin1_General_100_BIN2, ExpiresAtUtc datetime2);
            INSERT INTO @held
                SELECT r.Position, l.Token, l.ExpiresAtUtc FROM @requests r
                LEFT JOIN dbo.IxIFlowWorkflowLeases l WITH (UPDLOCK, HOLDLOCK) ON l.InstanceId = r.InstanceId
                ORDER BY r.InstanceId OPTION (FORCE ORDER, LOOP JOIN, MAXDOP 1);
            DECLARE @now datetime2 = SYSUTCDATETIME();
            UPDATE l SET ExpiresAtUtc = DATEADD(MILLISECOND, r.Milliseconds, @now)
                FROM dbo.IxIFlowWorkflowLeases l JOIN @requests r ON r.InstanceId = l.InstanceId
                JOIN @held h ON h.Position = r.Position
                WHERE h.Token = r.Token AND h.ExpiresAtUtc > @now
                    AND r.Terminating = 0;
            SELECT CASE WHEN h.Token = r.Token AND h.ExpiresAtUtc > @now
                THEN CASE WHEN r.Terminating = 1 THEN 1 ELSE 0 END ELSE 2 END AS Status
                FROM @requests r JOIN @held h ON h.Position = r.Position ORDER BY r.Position;
            """;
    }
}

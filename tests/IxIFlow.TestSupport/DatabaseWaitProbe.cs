using System.Data.Common;
using System.Reflection;
using Dapper;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>Observes a real database lock wait before a test makes a negative concurrency assertion.</summary>
public static class DatabaseWaitProbe
{
    public static async Task BlockedByAsync(DbConnection holder, Func<DbConnection> newConnection, string instanceId, int minimum = 1)
    {
        await using var observer = newConnection();

        var session = holder is SqlConnection sql ? sql.ServerProcessId : ((NpgsqlConnection)holder).ProcessID;

        await TestEventually.UntilAsync(
            instanceId, $"at least {minimum} sessions blocked by holder {session}", async () =>
                await observer.ExecuteScalarAsync<int>(
                    new CommandDefinition(
                        holder is SqlConnection
                            ? "WITH waits AS (SELECT session_id FROM sys.dm_exec_requests WHERE blocking_session_id=@Session " +
                            "UNION ALL SELECT request.session_id FROM sys.dm_exec_requests request " +
                            "JOIN waits ON request.blocking_session_id=waits.session_id) " +
                            "SELECT COUNT(DISTINCT session_id) FROM waits OPTION (MAXRECURSION 100)"
                            : "WITH RECURSIVE waits AS (SELECT pid FROM pg_stat_activity WHERE @Session=ANY(pg_blocking_pids(pid)) " +
                            "UNION SELECT activity.pid FROM pg_stat_activity activity " +
                            "JOIN waits ON waits.pid=ANY(pg_blocking_pids(activity.pid))) SELECT COUNT(*)::int FROM waits",
                        new { Session = session }, commandTimeout: 5)), count => count >= minimum);
    }

    public static async Task SqliteWriterQueueAsync(object repository, string instanceId, Action? afterWriterEntered = null)
    {
        var field = repository.GetType().GetField("_writer", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The SQLite writer gate was not found");

        var gate = (SemaphoreSlim)field.GetValue(repository)!;

        var head = typeof(SemaphoreSlim).GetField("m_asyncHead", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The semaphore's asynchronous waiter queue was not found");

        await TestEventually.UntilAsync(
            instanceId, "the SQLite writer gate to be held",
            () => Task.FromResult(gate.CurrentCount), count => count == 0);

        afterWriterEntered?.Invoke();

        await TestEventually.UntilAsync(
            instanceId, "a contender queued behind the SQLite writer",
            () => Task.FromResult(new { gate.CurrentCount, Queued = head.GetValue(gate) != null }),
            state => state.CurrentCount == 0 && state.Queued);
    }
}
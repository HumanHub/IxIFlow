using System.Diagnostics;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability"), Trait("Spec", "A-08"), Trait("Spec", "C-11"), Trait("Category", "SqlIntegration")]
public sealed class SqlServerReadInterleavingTests
{
    [SqlServerFact]
    public async Task ReadsRetryWhenACommitOrSameRevisionRecreationSplitsTheirStatements()
    {
        foreach (var recreate in new[] { false, true })
        foreach (var afterParts in new[] { false, true })
        foreach (var entry in new[] { "whole", "summary", "name", "correlation", "status" })
            await Verify(recreate, afterParts, entry);
    }

    private static async Task Verify(bool recreate, bool afterParts, string entry)
    {
        var cs = SqlTestDatabase<SqlServerReadInterleavingTests>.ConnectionString;
        var writer = new SqlWorkflowStateRepository(cs);
        var id = Guid.NewGuid().ToString("N");
        var original = CheckpointRaceContract.Instance(id, "old");
        original.WorkflowName = "read-" + id;
        original.CorrelationId = id;
        Assert.Equal(WorkflowCommitStatus.Applied, (await writer.CommitWorkflowInstanceAsync(CheckpointCommit.Create(original, 0, 0))).Status);
        var readerName = "interleaving-" + id;
        var reader = new SqlWorkflowStateRepository(new SqlConnectionStringBuilder(cs) { ApplicationName = readerName }.ConnectionString);
        await reader.GetWorkflowInstanceAsync(id); // Complete schema admission before command interception.
        await using var holder = new SqlConnection(cs);
        await holder.OpenAsync();
        var session = await holder.ExecuteScalarAsync<int>("SELECT @@SPID");
        await using var transaction = await holder.BeginTransactionAsync();
        var resource = "read-pause-" + id;
        await holder.ExecuteAsync("""
            DECLARE @r int;
            EXEC @r=sys.sp_getapplock @Resource=@Resource, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
            IF @r<0 THROW 51000, 'Cannot acquire read barrier', 1;
            """, new { Resource = resource }, transaction);
        using var pause = new CommandPause(readerName, resource, afterParts, entry is "whole" or "summary");
        async Task<WorkflowInstance?> Read() => entry switch
        {
            "whole" => await reader.GetWorkflowInstanceAsync(id),
            "summary" => await reader.GetWorkflowInstanceSummaryAsync(id),
            "name" => Assert.Single(await reader.GetWorkflowInstancesByNameAsync(original.WorkflowName)),
            "correlation" => Assert.Single(await reader.GetWorkflowInstancesByCorrelationIdAsync(id)),
            _ => Assert.Single((await reader.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Running)).Where(i => i.InstanceId == id))
        };
        var reading = Task.Run(Read);
        var replacement = CheckpointRaceContract.Instance(id, "new");
        replacement.WorkflowName = original.WorkflowName;
        replacement.CorrelationId = id;
        try
        {
            await using var observer = new SqlConnection(cs);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!await observer.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.dm_exec_requests WHERE blocking_session_id=@Session)
                THEN 1 ELSE 0 END AS bit)
                """, new { Session = session }, cancellationToken: timeout.Token))) await Task.Yield();
            Assert.Equal(1, pause.Hits);
            Assert.False(reading.IsCompleted);
            if (recreate)
            {
                await writer.DeleteWorkflowInstanceAsync(id);
                await observer.ExecuteAsync("DELETE FROM dbo.IxIFlowWorkflowTombstones WHERE InstanceId=@Id", new { Id = id });
            }
            Assert.Equal(WorkflowCommitStatus.Applied, (await writer.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(replacement, 0, recreate ? 0 : 1))).Status);
        }
        finally { await transaction.RollbackAsync(); }
        var saved = Assert.IsType<WorkflowInstance>(await reading.WaitAsync(TimeSpan.FromSeconds(15)));
        using var data = JsonDocument.Parse(saved.WorkflowDataJson);
        var marker = data.RootElement.GetProperty("marker").GetString();
        Assert.Contains(marker, new[] { "old", "new" });
        Assert.Equal(marker, ExecutionCheckpoint.ReadTrunk(saved.ExecutionStateJson).DefinitionFingerprint);
        Assert.Equal(recreate || marker == "old" ? 1 : 2, saved.Revision);
        var expected = marker == "old" ? original : replacement;
        Assert.Equal(expected.WorkflowDataJson, saved.WorkflowDataJson);
        if (entry != "summary") Assert.Equal(expected.ExecutionStateJson, saved.ExecutionStateJson);
        await writer.DeleteWorkflowInstanceAsync(id);
    }

    // SqlClient's public diagnostic stream lets this test add a held transaction barrier between
    // the real provider statements, without a production hook or a timing-dependent race.
    private sealed class CommandPause : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _application, _resource;
        private readonly bool _afterParts, _single;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _all;
        private int _hits;
        internal int Hits => Volatile.Read(ref _hits);
        internal CommandPause(string application, string resource, bool afterParts, bool single)
        { _application = application; _resource = resource; _afterParts = afterParts; _single = single; _all = DiagnosticListener.AllListeners.Subscribe(this); }
        public void OnNext(DiagnosticListener listener)
        { if (listener.Name == "SqlClientDiagnosticListener") _subscriptions.Add(listener.Subscribe(this)); }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (!value.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal) || value.Value == null) return;
            if (value.Value.GetType().GetProperty("Command")?.GetValue(value.Value) is not SqlCommand command ||
                command.Connection == null || new SqlConnectionStringBuilder(command.Connection.ConnectionString).ApplicationName != _application) return;
            var sql = command.CommandText;
            var parts = "SELECT InstanceId, PartKey, Json FROM dbo.IxIFlowWorkflowParts";
            var stamp = "SELECT InstanceId, Revision, Incarnation FROM dbo.IxIFlowWorkflowInstances";
            var anchor = _afterParts ? stamp : parts;
            var eligible = _single || _afterParts ? sql.Contains(anchor, StringComparison.Ordinal)
                : sql.StartsWith("SELECT InstanceId, StateJson", StringComparison.Ordinal);
            if (!eligible || Interlocked.CompareExchange(ref _hits, 1, 0) != 0) return;
            var barrier = $"""
                DECLARE @barrier int;
                EXEC @barrier=sys.sp_getapplock @Resource=N'{_resource}', @LockMode='Shared', @LockOwner='Session', @LockTimeout=20000;
                IF @barrier<0 THROW 51000, 'Read barrier timed out', 1;
                EXEC sys.sp_releaseapplock @Resource=N'{_resource}', @LockOwner='Session';
                """;
            command.CommandText = _single || _afterParts ? sql.Replace(anchor, barrier + "\n" + anchor, StringComparison.Ordinal)
                : sql + ";\n" + barrier;
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose() { _all.Dispose(); foreach (var subscription in _subscriptions) subscription.Dispose(); }
    }
}

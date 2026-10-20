using System.Data.Common;
using System.Text;
using System.Text.Json;
using Dapper;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "tracing")]
[Trait("Spec", "H-08")]
public sealed class DiagnosticStorageTests : IClassFixture<SqliteTestDatabase<DiagnosticStorageTests>>
{
    private const string Secret = "diagnostic-message-bc864631";

    [Theory, InlineData(false), InlineData(true)]
    public async Task LocalStoresKeepOpenAndFinalFailuresAndRemoveDiagnostics(bool sqlite)
    {
        var time = new FakeTimeProvider();
        await using var relational = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<DiagnosticStorageTests>.ConnectionString, time) : null;
        IWorkflowStateRepository store = relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository(time);
        await VerifyLifecycle(store, () => { time.Advance(TimeSpan.FromMinutes(2)); return Task.CompletedTask; });
        await VerifyAtomicCommits(store);
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerKeepsOpenAndFinalFailuresAndRemovesDiagnostics()
    {
        var connection = SqlTestDatabase<DiagnosticStorageTests>.ConnectionString;
        var store = new SqlWorkflowStateRepository(connection);
        await VerifyLifecycle(store, () => Age(new SqlConnection(connection)));
        await VerifyAtomicCommits(store);
    }

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlKeepsOpenAndFinalFailuresAndRemovesDiagnostics()
    {
        var connection = PostgreSqlTestDatabase<DiagnosticStorageTests>.ConnectionString;
        var store = new PostgreSqlWorkflowStateRepository(connection);
        await VerifyLifecycle(store, () => Age(new NpgsqlConnection(connection)));
        await VerifyAtomicCommits(store);
    }

    private static async Task Age(DbConnection connection)
    {
        await using (connection)
            await connection.ExecuteAsync(connection is SqlConnection
                ? "UPDATE dbo.IxIFlowWorkflowInstances SET EndedAtUtc=DATEADD(SECOND, -120, EndedAtUtc)"
                : "UPDATE ixiflow_workflow_instances SET ended_at_utc=ended_at_utc - INTERVAL '120 seconds'");
    }

    private static async Task VerifyLifecycle(IWorkflowStateRepository store, Func<Task> age)
    {
        using var services = Services(store);
        var client = services.GetRequiredService<IWorkflowClient>();
        var diagnostics = services.GetRequiredService<IWorkflowDiagnostics>();
        var definition = Workflow.Create<Data>("OpenDiagnostic")
            .Step<Noop>()
            .Parallel(p => p
                .Do(b => b.WaitFor<Notice>("a").Step<RecoveryFailure>())
                .Do(b => b.WaitFor<Notice>("b").Step<RecoveryFailure>()))
            .Build();
        var started = await client.RunAsync(definition, new Data());
        var saved = (await store.GetWorkflowInstanceAsync(started.InstanceId))!;
        var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
        var scopes = new WorkflowScopeCatalog(definition);
        // Model a host lost after the two activity Starts, so both recoveries throw and need resolution.
        var waiting = checkpoint.Continuations.Where(c => c.Status == ContinuationStatus.Waiting).ToArray();
        Assert.Equal(2, waiting.Length);
        foreach (var continuation in waiting)
        {
            var frame = continuation.Stack[^1];
            frame.NextStepIndex++;
            continuation.Status = ContinuationStatus.Active;
            continuation.PendingActivity = new ActivityInvocationState
            {
                StepId = scopes.Id(scopes.Steps(frame.ScopeId)[frame.NextStepIndex]),
                RecoveryState = SerializedValue.From("ready"), Attempts = [new ActivityAttemptState()]
            };
        }
        checkpoint.Waits.Clear();
        saved.Status = WorkflowStatus.Running;
        saved.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        await store.SeedAsync(saved);
        var recovered = await client.RecoverAsync(saved.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        var pending = await client.GetPendingActivitiesAsync(saved.InstanceId);
        Assert.Equal(2, pending.Count);
        var raw = await diagnostics.ReadAsync(saved.InstanceId);
        Assert.Equal(2, raw.Count);
        Assert.All(raw, item =>
        {
            Assert.Equal(Secret, item.Message);
            Assert.Contains(nameof(RecoveryFailure), item.StackTrace);
            Assert.Contains(pending, activity => activity.Reason!.Contains(item.DiagnosticId));
        });
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(await client.GetSnapshotAsync(saved.InstanceId)));
        var savedErrors = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(saved.InstanceId))!.ExecutionStateJson).Diagnostics;
        Assert.Equal(2, savedErrors!.Count);
        Assert.DoesNotContain(nameof(WorkflowDiagnostic.Message), JsonSerializer.Serialize(savedErrors));
        Assert.DoesNotContain(nameof(WorkflowDiagnostic.StackTrace), JsonSerializer.Serialize(savedErrors));
        var newestFailure = Assert.Single(raw, item => item.DiagnosticId == savedErrors[^1].DiagnosticId);
        // Completing one activity must preserve the other unresolved activity's original diagnostic.
        await client.ResolveAsync(saved.InstanceId, pending[0].InvocationId,
            ActivityResolution.Completed(new Dictionary<string, object?>()));
        var stillPending = Assert.Single(await client.GetPendingActivitiesAsync(saved.InstanceId));
        var remaining = await diagnostics.ReadAsync(saved.InstanceId);
        var unresolvedFailure = Assert.Single(remaining, item => stillPending.Reason!.Contains(item.DiagnosticId));
        Assert.Contains(unresolvedFailure, raw);
        Assert.Contains(newestFailure, remaining);
        var firstNote = Assert.Single(remaining, item => item.ErrorType == "OperatorNote");
        Assert.Equal("Observed completed", firstNote.Message);
        Assert.Null(firstNote.StackTrace);
        var finished = await client.ResolveAsync(saved.InstanceId, stillPending.InvocationId,
            ActivityResolution.Completed(new Dictionary<string, object?>()));
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        var retained = await diagnostics.ReadAsync(saved.InstanceId);
        Assert.Equal(newestFailure, Assert.Single(retained, item => item.ErrorType != "OperatorNote"));
        var notes = retained.Where(item => item.ErrorType == "OperatorNote").ToArray();
        Assert.Equal(2, notes.Length);
        Assert.Contains(firstNote, notes);
        var resolutions = (await client.GetTraceAsync(saved.InstanceId))
            .Where(entry => entry.EntryType == TraceEntryType.ActivityResolved).ToArray();
        Assert.Equal(2, resolutions.Length);
        Assert.All(notes, note =>
        {
            Assert.Equal("Observed completed", note.Message);
            Assert.Null(note.StackTrace);
            Assert.Contains(resolutions, entry => entry.Error?.DiagnosticId == note.DiagnosticId);
        });
        await store.DeleteWorkflowInstanceAsync(saved.InstanceId);
        Assert.Empty(await diagnostics.ReadAsync(saved.InstanceId));
        Assert.Empty(await store.GetDiagnosticsAsync(saved.InstanceId));

        var failure = await client.RunAsync(Workflow.Create<Data>("FinalDiagnostic")
            .Step<Noop>().Step<Failure>().Build(), new Data());
        Assert.Equal(WorkflowExecutionStatus.Faulted, failure.Status);
        var final = Assert.Single(await diagnostics.ReadAsync(failure.InstanceId));
        Assert.Equal(Secret, final.Message);
        Assert.Contains(nameof(Failure), final.StackTrace);
        var failedTrace = Assert.Single(await client.GetTraceAsync(failure.InstanceId),
            entry => entry.EntryType == TraceEntryType.ActivityFailed);
        Assert.Equal(failedTrace.Error!.DiagnosticId, final.DiagnosticId);
        Assert.Contains(failedTrace.Error, (await client.GetSnapshotAsync(failure.InstanceId))!.Errors);
        await age();
        Assert.Equal(1, (await store.PurgeAsync(TimeSpan.FromMinutes(1), TimeSpan.FromDays(1), 100)).InstancesRemoved);
        Assert.Empty(await diagnostics.ReadAsync(failure.InstanceId));
        Assert.Empty(await store.GetDiagnosticsAsync(failure.InstanceId));
    }

    private static async Task VerifyAtomicCommits(IWorkflowStateRepository store)
    {
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), Status = WorkflowStatus.Running,
            ExecutionStateJson = StoreTestSupport.CheckpointJson
        };
        var epoch = (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch;
        var raw = new WorkflowDiagnostic(Guid.NewGuid().ToString("N"), "Failure", Secret, "original stack");
        var first = CheckpointCommit.Create(instance, epoch, 0, diagnostics: [raw], retainedDiagnosticIds: [raw.DiagnosticId]);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(first)).Status);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(first)).Status);
        Assert.Equal(raw, Assert.Single(await store.GetDiagnosticsAsync(instance.InstanceId)));
        instance = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        var second = CheckpointCommit.Create(instance, epoch, instance.Revision,
            retainedDiagnosticIds: [raw.DiagnosticId]);
        Assert.Empty(second.Diagnostics);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(second)).Status);
        Assert.Equal(raw, Assert.Single(await store.GetDiagnosticsAsync(instance.InstanceId)));
        var rejected = raw with { DiagnosticId = Guid.NewGuid().ToString("N") };
        var stale = CheckpointCommit.Create(instance, epoch, instance.Revision,
            diagnostics: [rejected], retainedDiagnosticIds: [rejected.DiagnosticId]);
        Assert.Equal(WorkflowCommitStatus.Conflict, (await store.CommitWorkflowInstanceAsync(stale)).Status);
        Assert.Equal(raw, Assert.Single(await store.GetDiagnosticsAsync(instance.InstanceId)));
        var beforeOverwrite = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        var overwrite = CheckpointCommit.Create(beforeOverwrite, epoch, beforeOverwrite.Revision,
            diagnostics: [raw with { Message = "replacement" }], retainedDiagnosticIds: [raw.DiagnosticId]);
        await Assert.ThrowsAnyAsync<Exception>(() => store.CommitWorkflowInstanceAsync(overwrite));
        Assert.Equal(beforeOverwrite.Revision, (await store.GetWorkflowInstanceAsync(instance.InstanceId))!.Revision);
        Assert.Equal(raw, Assert.Single(await store.GetDiagnosticsAsync(instance.InstanceId)));
        // Diagnostics and retention participate in the immutable commit hash, like trace entries.
        var changed = CheckpointCommit.Create(instance, epoch, instance.Revision, commitId: second.CommitId,
            retainedDiagnosticIds: []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitWorkflowInstanceAsync(changed));
        instance = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        var prune = CheckpointCommit.Create(instance, epoch, instance.Revision, retainedDiagnosticIds: []);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(prune)).Status);
        Assert.Empty(await store.GetDiagnosticsAsync(instance.InstanceId));
        // Replaying an old receipt cannot resurrect a diagnostic deleted by a newer commit.
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(first)).Status);
        Assert.Empty(await store.GetDiagnosticsAsync(instance.InstanceId));
        await store.DeleteWorkflowInstanceAsync(instance.InstanceId);
    }

    [Fact]
    public async Task ThousandCaughtFailuresKeepCommitsFlatAndOnlyOneRawRecord()
    {
        var store = new MeasuringStore();
        using var services = Services(store);
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(
            Workflow.Create<Data>("CaughtDiagnosticLoop")
                .Step<Noop>()
                .WhileDo(c => c.WorkflowData.Count < 1000, body => body
                    .Try(attempt => attempt.Step<Failure>())
                    .Catch<InvalidOperationException>(handler => handler.Step<Count>(step => step
                        .Input(activity => activity.Value).From(c => c.WorkflowData.Count)
                        .Output(activity => activity.Value).To(c => c.WorkflowData.Count))))
                .Build(), new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1000, Assert.IsType<Data>(result.WorkflowData).Count);
        Assert.Equal(1000, store.Writes.Count);
        Assert.All(store.Writes.Values, count => Assert.Equal(1, count));
        Assert.Equal(1, store.MaxDiagnostics);
        Assert.Single(await services.GetRequiredService<IWorkflowDiagnostics>().ReadAsync(result.InstanceId));
        var early = store.Sizes.Take(200).Max();
        var late = store.Sizes.Skip(200).Max();
        Assert.True(late <= early + 128, $"Commits grew from {early} to {late} bytes");
        Assert.True(late < 6000, $"A caught-failure commit carried {late} bytes including new diagnostics");
    }

    private sealed class MeasuringStore : FaultInjectingRepository
    {
        public List<long> Sizes { get; } = [];
        public Dictionary<string, int> Writes { get; } = new(StringComparer.Ordinal);
        public int MaxDiagnostics { get; private set; }

        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var reply = await base.CommitWorkflowInstanceAsync(commit);
            if (reply.Status != WorkflowCommitStatus.Applied) return reply;
            Sizes.Add(commit.StateBytes() + commit.Diagnostics.Sum(item => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(item))));
            foreach (var diagnostic in commit.Diagnostics)
                Writes[diagnostic.DiagnosticId] = Writes.GetValueOrDefault(diagnostic.DiagnosticId) + 1;
            MaxDiagnostics = Math.Max(MaxDiagnostics, (await GetDiagnosticsForIncarnationAsync(commit.InstanceId, commit.Incarnation)).Count);
            // No raw diagnostic payload can hide in the trunk or any rewritten checkpoint part.
            var state = ExecutionCheckpoint.ReadTrunk(commit.ReadTrunk().ExecutionStateJson);
            Assert.DoesNotContain(nameof(WorkflowDiagnostic.StackTrace), JsonSerializer.Serialize(state.Diagnostics));
            Assert.True(state.Diagnostics is not { Count: > 1 });
            return reply;
        }
    }

    private static ServiceProvider Services(IWorkflowStateRepository store)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow();
        services.AddScoped<IWorkflowDiagnosticsAuthorizer>(_ => new Authorizer());
        services.AddTransient<Noop>();
        services.AddTransient<Failure>();
        services.AddTransient<RecoveryFailure>();
        services.AddTransient<Count>();
        return services.BuildServiceProvider();
    }

    private sealed class Authorizer : IWorkflowDiagnosticsAuthorizer
    {
        public ValueTask<bool> CanReadAsync(string instanceId, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    public sealed class Data { public int Count { get; set; } }
    public sealed class Notice;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Failure : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException(Secret);
    }
    public sealed class RecoveryFailure : IRecoverableActivity<string>
    {
        public string CaptureRecoveryState(IActivityContext context) => "ready";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ActivityRecoveryResult<string>> RecoverAsync(string state, IActivityContext context,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException(Secret);
    }
    public sealed class Count : IAsyncActivity
    {
        public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Value++;
            return Task.CompletedTask;
        }
    }
}

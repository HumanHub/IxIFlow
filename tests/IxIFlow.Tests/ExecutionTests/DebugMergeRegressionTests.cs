using IxIFlow.Builders;
using IxIFlow.ActivitySdk;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "H-04")]
public sealed class DebugMergeRegressionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TerminationBypassesPausedCommandsAndPayloadAssemblyAndRetentionDeletesDebugState(bool sqlite, bool claimCheck)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ixiflow-debug-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using var sqliteStore = sqlite
                ? new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "state.db"), clock) : null;
            IWorkflowStateRepository store = sqliteStore is null ? new InMemoryWorkflowStateRepository(clock) : sqliteStore;
            await using var services = Services(store, true, claimCheck ? 64 : 0);
            var definition = Workflow.Create<Data>("PausedTerminationMerge").Step<Noop>()
                .Parallel(parallel => parallel.Do(branch => branch.WaitFor<Go>("left"))
                    .Do(branch => branch.WaitFor<Go>("right"))).Build();
            var first = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition,
                new Data { Text = new string('x', 300_000) });
            var operations = services.GetRequiredService<IWorkflowOperations>();
            Assert.Equal(WorkflowExecutionStatus.Paused, (await operations.PauseAsync(first.InstanceId, "pauser")).Status);
            var before = (await store.GetWorkflowInstanceAsync(first.InstanceId))!;
            Assert.Equal(claimCheck, before.WorkflowDataJson.Reference != null);
            var generation = WorkflowDebugRules.Checkpoint(before).PauseGeneration;
            Assert.Equal(DebugCommandStatus.Accepted, (await store.RequestDebugCommandAsync(first.InstanceId,
                new("queued-continue", DebugCommandKind.Continue, generation, "continuer"))).Status);

            // A host without debugger services and with a lower checkpoint limit must still terminate it.
            await using var remote = Services(store, false, 0, 1);
            var terminated = await remote.GetRequiredService<IWorkflowOperations>()
                .TerminateAsync(first.InstanceId, "stop", actor: "terminator").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WorkflowStatus.Terminated, terminated.Status);
            var after = (await store.GetWorkflowInstanceAsync(first.InstanceId))!;
            Assert.Equal(before.WorkflowDataJson, after.WorkflowDataJson);
            Assert.Equal(before.SavedParts, after.SavedParts);
            Assert.Null(after.RunnableAtUtc);
            Assert.Empty(await store.GetOpenWaitsAsync(new WaitQuery(InstanceId: first.InstanceId)));
            Assert.Single(await store.GetTraceAsync(first.InstanceId), row => row.Entry.EntryType == TraceEntryType.WorkflowTerminated);
            Assert.Null((await store.GetDebugStatesAsync([first.InstanceId]))[first.InstanceId]
                .Commands.Single(command => command.Id == "queued-continue").AcknowledgedRevision);
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(1, (await store.PurgeAsync(TimeSpan.FromMinutes(1), TimeSpan.FromDays(1), 10)).InstancesRemoved);
            Assert.Null(await store.GetWorkflowInstanceAsync(first.InstanceId));
            Assert.DoesNotContain(first.InstanceId, await store.GetDebugStatesAsync([first.InstanceId]));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, bool debugging, long threshold,
        long maxCheckpointBytes = 1024 * 1024)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow(options =>
        {
            options.Debugging = debugging;
            options.PayloadThresholdBytes = threshold;
            options.MaxCheckpointBytes = maxCheckpointBytes;
        });
        return services.BuildServiceProvider();
    }

    public sealed class Data { public string Text { get; set; } = ""; }
    public sealed class Go;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

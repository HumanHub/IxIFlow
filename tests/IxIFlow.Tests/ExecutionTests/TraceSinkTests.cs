using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The trace is not part of the checkpoint (G-01): each commit carries the entries recorded since the last one,
/// and the store appends them, in order, to the instance's trace in the commit's own transaction.
/// </summary>
[Trait("Feature", "tracing")]
public sealed class TraceSinkTests : IClassFixture<SqliteTestDatabase<TraceSinkTests>>
{
    [Fact]
    [Trait("Spec", "G-01")]
    public async Task CommitsStoreTheTraceInOrderAndTheInstanceHoldsNone()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(TwoSteps("TracedSteps"), new TraceData());

        var resumed = await engine.ResumeAsync(started.InstanceId, "go", new Go());

        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        var trace = await engine.GetTraceAsync(started.InstanceId);
        Assert.Equal([TraceEntryType.ActivityStarted, TraceEntryType.ActivityCompleted, TraceEntryType.StepEntered,
            TraceEntryType.EventReceived, TraceEntryType.ActivityStarted, TraceEntryType.ActivityCompleted],
            trace.Select(entry => entry.EntryType));
        Assert.Equal([1L, 2, 3, 4, 5, 6], (await repository.GetTraceAsync(started.InstanceId)).Select(entry => entry.Sequence));
        Assert.DoesNotContain("ExecutionHistory",
            System.Text.Json.JsonSerializer.Serialize(await repository.GetWorkflowInstanceAsync(started.InstanceId)));
        // A result carries what its own run recorded.
        Assert.Equal(3, started.TraceEntries.Count);
        Assert.Equal(3, resumed.TraceEntries.Count);
        Assert.Equal(started.TraceEntries.Select(entry => entry.TraceId).Concat(resumed.TraceEntries.Select(entry => entry.TraceId)),
            trace.Select(entry => entry.TraceId));
    }

    [Fact]
    [Trait("Spec", "G-01")]
    public async Task AResentCommitAppendsItsTraceOnce()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var instance = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Resent" };
        var commit = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(repository, instance.InstanceId)).Epoch, 0, traceEntries:
        [
            new ExecutionTraceEntry { EntryType = TraceEntryType.ActivityStarted, ActivityName = "First" },
            new ExecutionTraceEntry { EntryType = TraceEntryType.ActivityCompleted, ActivityName = "First" }
        ]);

        Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(commit)).Status);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await repository.CommitWorkflowInstanceAsync(commit)).Status);

        var trace = await repository.GetTraceAsync(instance.InstanceId);
        Assert.Equal(["First", "First"], trace.Select(entry => entry.Entry.ActivityName));
        Assert.Equal([2L], (await repository.GetTraceAsync(instance.InstanceId, afterSequence: 1)).Select(entry => entry.Sequence));
        await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        Assert.Empty(await repository.GetTraceAsync(instance.InstanceId));
    }

    [Fact]
    [Trait("Spec", "G-01")]
    public async Task ProgressThatIsNotSavedLeavesNoTrace()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository, maxCheckpointBytes: 8_000);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<TraceData>("TraceOverLimit")
            .Step<Grow>(step => step.Output(activity => activity.Text).To(ctx => ctx.WorkflowData.Text))
            .Build();

        var result = await engine.RunAsync(definition, new TraceData());

        // The activity ran, but its end was over the size limit and not saved: neither is its completion.
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
        var trace = await engine.GetTraceAsync(result.InstanceId);
        Assert.Contains(trace, entry => entry.EntryType == TraceEntryType.ActivityStarted);
        Assert.DoesNotContain(trace, entry => entry.EntryType == TraceEntryType.ActivityCompleted);
        Assert.DoesNotContain(result.TraceEntries, entry => entry.EntryType == TraceEntryType.ActivityCompleted);
    }

    [Fact]
    [Trait("Spec", "G-01")]
    public async Task WithTracingOffARecoveryRecordsNothingEither()
    {
        var store = new FaultInjectingRepository();
        var instanceId = Guid.NewGuid().ToString("N");
        // The host dies as it saves the first step's end; the step is recovered on another host.
        store.FailCommit = instance => instance.WorkflowDataJson.Contains("\"Value\":7");
        using (var first = Services(store))
            await Assert.ThrowsAnyAsync<Exception>(() => first.GetRequiredService<IWorkflowClient>()
                .RunAsync(Recoverable("UntracedRecovery"), new TraceData(),
                    new WorkflowOptions { InstanceId = instanceId, EnableTracing = false }));
        store.FailCommit = _ => false;
        using var second = Services(store);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(Recoverable("UntracedRecovery"));

        var recovered = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Suspended, recovered.Status);
        Assert.Empty(await store.GetTraceAsync(instanceId));
        Assert.Empty(recovered.TraceEntries);
    }

    [Fact]
    [Trait("Spec", "G-01")]
    public async Task WithTracingOffAChildInvocationRecordsNothing()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var engine = services.GetRequiredService<IWorkflowClient>();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<TraceData>("UntracedChild").Step<Noop>().Build());
        var parent = Workflow.Create<TraceData>("UntracedParent")
            .Step<Noop>()
            .Invoke<TraceData>("UntracedChild", 1)
            .Build();

        var started = await engine.RunAsync(parent, new TraceData(), new WorkflowOptions { EnableTracing = false });
        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Empty(await repository.GetTraceAsync(started.InstanceId));
    }

    [Fact]
    [Trait("Spec", "G-01")]
    public async Task AResultReadFromAStoredInstanceCarriesNoTrace()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(TwoSteps("ExistingTrace"), new TraceData());

        var awaited = await engine.AwaitAsync(started.InstanceId);

        Assert.Empty(awaited.TraceEntries);
        Assert.Equal(3, (await engine.GetTraceAsync(started.InstanceId)).Count);
    }

    [SqlServerFact]
    [Trait("Spec", "G-01")]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerStoresTheTraceInOrderOncePerCommit() => VerifyTraceOrderOncePerCommit(new SqlWorkflowStateRepository(SqlTestDatabase<TraceSinkTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Spec", "G-01")]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlStoresTheTraceInOrderOncePerCommit() => VerifyTraceOrderOncePerCommit(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<TraceSinkTests>.ConnectionString));

    private static WorkflowDefinition TwoSteps(string name) =>
        Workflow.Create<TraceData>(name).Step<Noop>().WaitFor<Go>("go").Step<Noop>().Build();

    private static WorkflowDefinition Recoverable(string name) =>
        Workflow.Create<TraceData>(name)
            .Step<Count>(step => step.Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Value))
            .WaitFor<Go>("go")
            .Build();

    private static ServiceProvider Services(IWorkflowStateRepository repository, long? maxCheckpointBytes = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(repository);
        services.AddIxIFlow(options =>
        {
            if (maxCheckpointBytes is { } limit)
                options.MaxCheckpointBytes = limit;
        });
        return services.BuildServiceProvider();
    }

    public sealed class TraceData
    {
        public string Text { get; set; } = "";
        public int Value { get; set; }
    }

    public sealed class Go;

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Produces more data than the limit allows.</summary>
    public sealed class Grow : IAsyncActivity
    {
        public string Text { get; private set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Text = new string('x', 20_000);
            return Task.CompletedTask;
        }
    }

    /// <summary>Safe to run again, so a recovery runs it again rather than asking an operator.</summary>
    public sealed class Count : IRecoverableActivity<bool>
    {
        public int Value { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Value = 7;
            return Task.CompletedTask;
        }

        public bool CaptureRecoveryState(IActivityContext context) => true;

        public Task<ActivityRecoveryResult<bool>> RecoverAsync(bool state, IActivityContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Execute());
    }
    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteStoresTheTraceInOrderOncePerCommit()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<TraceSinkTests>.ConnectionString);
        await VerifyTraceOrderOncePerCommit(repository);
    }


    private static async Task VerifyTraceOrderOncePerCommit(IWorkflowStateRepository repository)
    {
        var instance = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "SqlTrace" };
        try
        {
            var first = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(repository, instance.InstanceId)).Epoch, 0, traceEntries:
                Enumerable.Range(0, 1_500).Select(index => new ExecutionTraceEntry { ActivityName = $"step-{index}" })
                    .ToArray());
            Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(first)).Status);
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await repository.CommitWorkflowInstanceAsync(first)).Status);
            instance.Revision = 1;
            var second = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(repository, instance.InstanceId)).Epoch, 1, traceEntries:
                [new ExecutionTraceEntry { ActivityName = "last" }]);
            Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(second)).Status);

            var page = await repository.GetTraceAsync(instance.InstanceId, afterSequence: 1_498, limit: 10);
            Assert.Equal([1_499L, 1_500, 1_501], page.Select(entry => entry.Sequence));
            Assert.Equal(["step-1498", "step-1499", "last"], page.Select(entry => entry.Entry.ActivityName));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
        Assert.Empty(await repository.GetTraceAsync(instance.InstanceId));
    }


}

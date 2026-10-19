using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "tracing")]
[Trait("Spec", "G-01")]
public sealed class TraceReviewTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitAndRequestTimeoutsNameTheWaitStepAndEnteredScope(bool request)
    {
        var time = new FakeTimeProvider(Start);
        using var services = Services(time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var workflow = Workflow.Create<Data>("TimeoutTrace" + request).Step<Noop>();
        var definition = request
            ? workflow.Request<Request>("go").WaitFor<Go>("go", configure: wait => wait.TimeoutAfter(TimeSpan.FromMinutes(1))
                .OnTimeout(body => body.Step<Noop>())).Build()
            : workflow.WaitFor<Go>("go", configure: wait => wait.TimeoutAfter(TimeSpan.FromMinutes(1))
                .OnTimeout(body => body.Step<Noop>())).Build();
        var started = await client.RunAsync(definition, new Data());
        var wait = Assert.Single(await services.GetRequiredService<IWorkflowStateRepository>()
            .GetOpenWaitsAsync(new WaitQuery { InstanceId = started.InstanceId }));
        time.Advance(TimeSpan.FromMinutes(2));
        // The clock also fires the host timer, so this recovery can arrive after completion.
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.RecoverAsync(started.InstanceId, patience.Token);
        var completed = await client.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished, patience.Token);
        Assert.True(completed.Status == WorkflowExecutionStatus.Success, $"{completed.Status}: {completed.ErrorMessage}");
        var trace = await client.GetTraceAsync(started.InstanceId);
        var timedOut = Assert.Single(trace, entry => entry.EntryType == TraceEntryType.WaitTimedOut);
        var payload = Assert.IsType<TimeoutTrace>(timedOut.Payload);
        Assert.Equal(WaitReference.Of(started.InstanceId, wait.WaitId), payload.WaitRef);
        Assert.Equal(request ? "root/2" : "root/1", payload.StepId);
        Assert.Equal(payload.StepId + "/timeout", payload.ScopeId);
        Assert.Equal(WorkflowStepType.SuspendResume, payload.StepKind);
        Assert.False(payload.Replayed);
        Assert.True(trace.ToList().IndexOf(timedOut) < trace.ToList().FindLastIndex(entry =>
            entry.EntryType == TraceEntryType.ActivityStarted));
    }

    [Fact]
    public async Task AKeptRequestRecordsItsTimeoutAgainWhenARetryReentersTheScope()
    {
        var time = new TimerRecordingClock(Start);
        using var services = Services(time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("ReplayedTimeoutTrace").Step<Noop>()
            .Saga(body => body.Request<Request>("go")
                .WaitFor<Go>("go", configure: wait => wait.TimeoutAfter(TimeSpan.FromMinutes(1))
                    .OnTimeout(timeout => timeout.Step<Fail>())))
            .OnError<InvalidOperationException>(handler => handler.CompensateNone().ThenRetry(1)).Build();
        var started = await client.RunAsync(definition, new Data());
        time.Advance(TimeSpan.FromMinutes(2));
        await time.WaitForTimerDueAtAsync(time.GetUtcNow() + TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(WorkflowExecutionStatus.Faulted, (await client.RecoverAsync(started.InstanceId)).Status);
        var trace = await client.GetTraceAsync(started.InstanceId);
        var timeouts = trace.Where(entry => entry.EntryType == TraceEntryType.WaitTimedOut)
            .Select(entry => Assert.IsType<TimeoutTrace>(entry.Payload)).ToArray();
        Assert.Equal(2, timeouts.Length);
        Assert.False(timeouts[0].Replayed);
        Assert.True(timeouts[1].Replayed);
        Assert.Equal(timeouts[0].WaitRef, timeouts[1].WaitRef);
        Assert.Equal(timeouts[0].ScopeId, timeouts[1].ScopeId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SagaRestartsRecordFullAndKeptRestartPoints(bool keep)
    {
        var time = new TimerRecordingClock(Start);
        using var services = Services(time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("SagaRetryTrace" + keep).Step<Noop>()
            .Saga(body => body.Step<Noop>().Step<Fail>())
            .OnError<InvalidOperationException>(handler =>
                (keep ? handler.CompensateNone() : handler.Compensate()).ThenRetry(1)).Build();
        var running = client.RunAsync(definition, new Data());
        await time.WaitForTimerDueAtAsync(time.GetUtcNow() + TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        var payload = Assert.IsType<RetryTrace>(Assert.Single(await client.GetTraceAsync(result.InstanceId),
            entry => entry.EntryType == TraceEntryType.SagaRetryStarted).Payload);
        Assert.Equal("root/1", payload.StepId);
        Assert.Equal(1, payload.Attempt);
        Assert.Equal(keep ? "root/1/sequence/1" : null, payload.RestartStepId);
        Assert.Equal("root/1/sequence", payload.ScopeId);
    }

    [Fact]
    public async Task StepRetriesRecordAnEntryBeforeTheNextAttempt()
    {
        var time = new FakeTimeProvider(Start);
        using var services = Services(time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("StepRetryTrace").Step<Noop>()
            .Saga(body => body.Step<Fail>(step => step.OnError<InvalidOperationException>(handler => handler.ThenRetry(new RetryPolicy(1)))))
            .Build();
        var result = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        var trace = await client.GetTraceAsync(result.InstanceId);
        var entry = Assert.Single(trace, entry => entry.EntryType == TraceEntryType.StepRetryScheduled);
        var payload = Assert.IsType<RetryTrace>(entry.Payload);
        Assert.Equal(1, payload.Attempt);
        Assert.Equal("root/1/sequence/0", payload.StepId);
        Assert.Equal(payload.StepId, payload.RestartStepId);
        Assert.True(trace.ToList().IndexOf(entry) < trace.ToList().FindLastIndex(entry => entry.EntryType == TraceEntryType.ActivityStarted));
    }

    [Fact]
    public async Task AFailedCompensationNeverRecordsSuccessfulCompletion()
    {
        var time = new FakeTimeProvider(Start);
        using var services = Services(time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("FailedCompensationTrace").Step<Noop>()
            .Saga(body => body.Step<Noop>(step => step.CompensateWith<Fail>()).Step<Fail>()).Build();
        var result = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        var trace = await client.GetTraceAsync(result.InstanceId);
        var started = Assert.IsType<ControlFlowTrace>(Assert.Single(trace,
            entry => entry.EntryType == TraceEntryType.CompensationStarted).Payload);
        var failed = Assert.IsType<ControlFlowTrace>(Assert.Single(trace,
            entry => entry.EntryType == TraceEntryType.CompensationFailed).Payload);
        Assert.Equal(started, failed);
        Assert.DoesNotContain(trace, entry => entry.EntryType == TraceEntryType.CompensationCompleted);
    }

    [Theory]
    [InlineData(false, false, ItemEndCause.Failed)]
    [InlineData(false, true, ItemEndCause.UnwoundByOuterHandler)]
    [InlineData(true, false, ItemEndCause.Failed)]
    [InlineData(true, true, ItemEndCause.Failed)]
    public async Task ItemEndsRetainTheirCauseThroughFailureHandling(bool parallel, bool caught, ItemEndCause cause)
    {
        var time = new FakeTimeProvider(Start);
        using var services = Services(time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var workflow = Workflow.Create<Data>("ItemFailureTrace" + parallel + caught).Step<Noop>();
        var definition = caught
            ? workflow.Try(body => body.ForEach(ctx => ctx.WorkflowData.Items, (items, _) => items.Step<Fail>(),
                setup => { if (parallel) setup.Parallel(1); }))
                .Catch<InvalidOperationException, MessageFault>(body => body.Step<Noop>()).Build()
            : workflow.ForEach(ctx => ctx.WorkflowData.Items, (items, _) => items.Step<Fail>(),
                setup => { if (parallel) setup.Parallel(1); }).Build();
        var result = await client.RunAsync(definition, new Data());
        Assert.Equal(caught ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Faulted, result.Status);
        var ended = Assert.IsType<ItemEndTrace>(Assert.Single(await client.GetTraceAsync(result.InstanceId),
            entry => entry.EntryType == TraceEntryType.ForEachItemEnded).Payload);
        Assert.Equal(cause, ended.Cause);
        Assert.Equal(0, ended.Index);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledItemsHaveTheSameCauseInSequentialAndParallelLoops(bool parallel)
    {
        var time = new FakeTimeProvider(Start);
        using var services = Services(time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("CancelledItemsTrace" + parallel).Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Items, (body, _) => body.WaitFor<Go>("go"),
                setup => { if (parallel) setup.Parallel(1); }).Build();
        var started = await client.RunAsync(definition, new Data());
        await client.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "stop" });
        var ended = Assert.IsType<ItemEndTrace>(Assert.Single(await client.GetTraceAsync(started.InstanceId),
            entry => entry.EntryType == TraceEntryType.ForEachItemEnded).Payload);
        Assert.Equal(ItemEndCause.Cancelled, ended.Cause);
    }

    [Fact]
    public async Task ActivitiesDetailRetainsActivitiesEventsAndCancellation()
    {
        var time = new FakeTimeProvider(Start);
        using var services = Services(time, options => options.TraceDetail = TraceDetail.Activities);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("ActivitiesTrace").Step<Noop>()
            .If(ctx => true, body => body.Step<Noop>()).WaitFor<Go>("go").WaitFor<Go>("again").Build();
        var started = await client.RunAsync(definition, new Data());
        await client.ResumeAsync(started.InstanceId, "go", new Go());
        await client.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "stop" });
        var trace = await client.GetTraceAsync(started.InstanceId);
        Assert.Equal([TraceEntryType.ActivityStarted, TraceEntryType.ActivityCompleted,
            TraceEntryType.ActivityStarted, TraceEntryType.ActivityCompleted, TraceEntryType.EventReceived,
            TraceEntryType.CancellationRequested, TraceEntryType.WorkflowCancelled], trace.Select(entry => entry.EntryType));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1000, true)]
    [InlineData(1000, false)]
    public async Task TheInstanceBudgetSurvivesRecoveryAndKeepsLaterActivities(int commitLimit, bool keepBudget)
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryWorkflowStateRepository(time);
        var definition = Workflow.Create<Data>("BudgetTrace" + commitLimit).Step<Noop>()
            .If(ctx => true, body => body.Step<Noop>()).WaitFor<Go>("go")
            .If(ctx => true, body => body.Step<Noop>()).Build();
        string id;
        using (var services = Services(time, options =>
               { options.MaxTraceEntriesPerInstance = 2; options.MaxTraceEntriesPerCommit = commitLimit; }, store))
            id = (await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data())).InstanceId;
        using var recovered = Services(time, options =>
            { options.MaxTraceEntriesPerInstance = keepBudget ? 2 : null; options.MaxTraceEntriesPerCommit = commitLimit; }, store);
        await recovered.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        var client = recovered.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.ResumeAsync(id, "go", new Go())).Status);
        var trace = await client.GetTraceAsync(id);
        var marker = Assert.Single(trace, entry => entry.EntryType == TraceEntryType.TraceBudgetReached);
        Assert.Equal(2, Assert.IsType<TraceBudget>(marker.Payload).Limit);
        Assert.DoesNotContain(trace.Skip(trace.ToList().IndexOf(marker) + 1), entry => TraceBuffer.IsControlFlow(entry.EntryType));
        Assert.Contains(trace.Skip(trace.ToList().IndexOf(marker) + 1), entry => entry.EntryType == TraceEntryType.ActivityCompleted);
    }

    [Fact]
    public void AcknowledgementClearsTheBufferAndTransfersTheResultWithoutCopyingIt()
    {
        var checkpoint = new ExecutionCheckpoint { ResultTrace = [] };
        var trace = checkpoint.Trace;
        var first = new ExecutionTraceEntry { EntryType = TraceEntryType.ActivityStarted };
        trace.Add(first);
        checkpoint.AcknowledgeTrace();
        Assert.Empty(trace.Pending);
        var discarded = new ExecutionTraceEntry { EntryType = TraceEntryType.ConditionTaken };
        trace.Add(discarded);
        trace.DiscardPending();
        var result = checkpoint.TakeTraceResult();
        Assert.Same(first, Assert.Single(result));
        Assert.Empty(checkpoint.TakeTraceResult());
        Assert.Empty(trace.Pending);
    }

    [Fact]
    public void SaturatedTransitionsAllocateNothingAfterTheirSingleMarker()
    {
        var definition = Workflow.Create<Data>("AllocationTrace").Step<Noop>().Build();
        var checkpoint = new ExecutionCheckpoint();
        var trace = checkpoint.Trace;
        trace.Configure(new WorkflowInstance(), checkpoint, new ExecutionLimits { MaxTraceEntriesPerCommit = 1 },
            new FakeTimeProvider(Start), new WorkflowScopeCatalog(definition));
        var continuation = new ContinuationState();
        var step = definition.Steps[0];
        trace.Record(TraceEntryType.StepEntered, step, continuation);
        trace.Record(TraceEntryType.StepEntered, step, continuation);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
            trace.Record(TraceEntryType.StepEntered, step, continuation);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 512);
        Assert.Equal(10_002, Assert.IsType<TraceTruncation>(Assert.Single(trace.Pending).Payload).DroppedCount);
    }

    [Fact]
    public void TraceOptionsDefaultToFullAndNoInstanceBudgetAndRejectInvalidValues()
    {
        Assert.Equal(TraceDetail.Full, new IxIFlowOptions().TraceDetail);
        Assert.Null(new IxIFlowOptions().MaxTraceEntriesPerInstance);
        foreach (var budget in new long[] { 0, -1 })
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection()
                .AddIxIFlow(options => { options.ReplyTokenKeys = TestReplyKeys.Ring; options.MaxTraceEntriesPerInstance = budget; }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection()
            .AddIxIFlow(options => { options.ReplyTokenKeys = TestReplyKeys.Ring; options.TraceDetail = (TraceDetail)42; }));
    }

    [Fact]
    public async Task AVerificationStopDiscardsTheAbandonedPassAndRetainsTheSavedCheckpoint()
    {
        var time = new FakeTimeProvider(Start);
        using var services = Services(time);
        var definition = Workflow.Create<Data>("VerificationTrace").Step<Noop>().Parallel(parallel => parallel
            .Do(body => body.WaitFor<Go>("go"))).Build();
        var scopes = new WorkflowScopeCatalog(definition);
        var root = new ContinuationState { Status = ContinuationStatus.Joining,
            Stack = [new ScopePosition { ScopeId = "root", NextStepIndex = 1 }] };
        var join = new ParallelJoinState { StepId = "root/1", ParentContinuationId = root.Id };
        var branch = new ContinuationState { ParentJoinId = join.Id, Status = ContinuationStatus.Waiting,
            Data = new Data(), Stack = [new ScopePosition { ScopeId = "root/1/branch/0" }] };
        join.ChildContinuationIds.Add(branch.Id);
        var data = new Data();
        var checkpoint = new ExecutionCheckpoint { StructureHash = scopes.StructureHash,
            Continuations = [root, branch], Joins = [join], RootData = data };
        var instance = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = "verification-trace", WorkflowName = definition.Name,
            WorkflowDataType = typeof(Data).AssemblyQualifiedName!, WorkflowDataJson = JsonSerializer.Serialize(data),
            Status = WorkflowStatus.Running, StartedAt = Start.UtcDateTime };
        var host = new VerificationHost();
        var commits = host.Commits;
        await ExecutionCheckpointWriter.CommitAsync(host, instance, checkpoint, data, scopes, Start.UtcDateTime);
        // Deliberately change a saved branch without its dirty mark: diagnostic verification must stop this pass.
        branch.Status = ContinuationStatus.WaitingResolution;
        checkpoint.Trace.Add(new ExecutionTraceEntry { EntryType = TraceEntryType.ConditionTaken,
            Payload = new ConditionTrace("root/0", WorkflowStepType.Conditional, root.Id, 0, "root/0/then", ConditionPath.Then) });
        checkpoint.Trace.Add(new ExecutionTraceEntry { EntryType = TraceEntryType.EventReceived,
            Payload = new ReceivedEventTrace("pending", "go", typeof(Go).AssemblyQualifiedName!, "wait", "root/0") });
        var executor = new WorkflowExecutor(services, services.GetRequiredService<IActivityExecutor>(), time);
        var result = await executor.RunAsync(definition, instance, checkpoint, data, host, CancellationToken.None);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
        Assert.Empty(result.TraceEntries);
        Assert.Equal(2, commits.Count);
        Assert.All(commits, commit => Assert.Empty(commit.TraceEntries));
        var saved = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance));
        Assert.Equal(OperatorStopKind.Verification, saved.OperatorStop!.Kind);
        Assert.Equal(ContinuationStatus.Waiting, saved.Continuations.Single(item => item.Id == branch.Id).Status);
        Assert.Empty(checkpoint.Trace.Pending);
    }

    [Fact]
    public async Task SequenceOrdersEntriesWhenStoreAndHostClocksDiffer()
    {
        var hostTime = new FakeTimeProvider(Start);
        var storeTime = new FakeTimeProvider(Start.AddHours(1));
        using var services = Services(hostTime, store: new InMemoryWorkflowStateRepository(storeTime));
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("TraceClocks").Step<Noop>().WaitFor<Go>("go").Build();
        var started = await client.RunAsync(definition, new Data());
        await client.ResumeAsync(started.InstanceId, "wrong", new Go());
        await client.ResumeAsync(started.InstanceId, "go", new Go());
        var trace = await client.GetTraceAsync(started.InstanceId);
        var refused = Assert.Single(trace, entry => entry.EntryType == TraceEntryType.EventRefused);
        var received = Assert.Single(trace, entry => entry.EntryType == TraceEntryType.EventReceived);
        Assert.Equal(TraceClock.Store, refused.Clock);
        Assert.Equal(TraceClock.Host, received.Clock);
        Assert.Equal(storeTime.GetUtcNow().UtcDateTime, refused.Timestamp);
        Assert.Equal(hostTime.GetUtcNow().UtcDateTime, received.Timestamp);
        Assert.True(trace.ToList().IndexOf(refused) < trace.ToList().IndexOf(received));
        Assert.True(refused.Timestamp > received.Timestamp);
    }

    private sealed class VerificationHost : IExecutionHost
    {
        public List<CheckpointCommit> Commits { get; } = [];
        public ExecutionLimits Limits { get; } = new() { VerifyCheckpointParts = true };
        public CancellationToken Drain => CancellationToken.None;
        public Task? OwnershipLoss => null;
        public Task<CancellationReason>? CancellationSignal => null;
        public long ObservedInboxSequence => 0;
        public Task? InboxChanged => null;
        public CancellationReason? ObservedCancellation => null;
        public long Epoch => 1;
        public bool CanPark => false;
        public void ThrowIfOwnershipLost() { }
        public void ObserveCancellation(CancellationReason reason) { }
        public Task<CheckpointReply> CommitAsync(CheckpointCommit commit)
        {
            Commits.Add(commit);
            return Task.FromResult(new CheckpointReply(CheckpointReplyKind.Acknowledged, Commits.Count));
        }
        public Task<InboxPage> ReadInboxAsync(long afterSequence, int limit) =>
            Task.FromResult(new InboxPage([], afterSequence, new Dictionary<string, long>()));
        public Task<StoredWait?> ReadWaitAsync(string waitId) => Task.FromResult<StoredWait?>(null);
        public Task<WorkflowDefinition> ResolveChildAsync(WorkflowStep step, bool starting) => throw new NotSupportedException();
        public ChildStart StartChild(WorkflowDefinition definition, object data, WorkflowInstance parent,
            string childInstanceId, string parentWaitId) => throw new NotSupportedException();
    }

    private static ServiceProvider Services(FakeTimeProvider time, Action<IxIFlowOptions>? configure = null,
        IWorkflowStateRepository? store = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IWorkflowStateRepository>(store ?? new InMemoryWorkflowStateRepository(time));
        services.AddIxIFlow(options => { options.ReplyTokenKeys = TestReplyKeys.Ring; (configure ?? (_ => { }))(options); });
        return services.BuildServiceProvider();
    }

    public sealed class Data { public List<int> Items { get; set; } = [1]; }
    public sealed class Go;
    public sealed class Request : IRequestActivity<Go>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("failed");
    }
}

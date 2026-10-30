using System.Text;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class PrereleaseSlice2RuntimeTests
{
    [Fact]
    public async Task PublicExecutorUsesAnAssignmentScopeAndDisposesItsServices()
    {
        var probe = new Probe();
        await using var services = Services(probe, validateScopes: true);
        var definition = Workflow.Create<Data>("ScopedPublicExecutor").Step<ScopedActivity>().Build();
        var host = new Host();
        var executor = new WorkflowExecutor(services, host);
        var assign = await Assignment(services, definition);
        host.Saved = assign.Instance;
        var ready = await executor.ExecuteAsync(assign, CancellationToken.None);
        Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome!.Kind);
        Assert.Equal(WorkflowStatus.Completed, host.Saved!.Status);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, probe.Disposals);
    }

    [Fact]
    public async Task MissingExecutorDependencyFailsAtResolution()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var executor = new WorkflowExecutor(services, new Host());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            new(Guid.NewGuid(), new(), null, "hash", 1, 0, ExecutionLimits.Default, false, []), CancellationToken.None));

        Assert.Contains(nameof(IActivityExecutor), error.Message);
    }

    [Fact]
    public async Task PublicExecutorRetainsItsScopeUntilTimedOutCleanupActuallyEnds()
    {
        var probe = new Probe();
        await using var services = Services(probe, validateScopes: true);

        var definition = new WorkflowBuilder<Data, object>([], "PublicScopeLateCleanup")
            .Scope<HangingScope>(null, body => body.WaitFor<Go>("go")).Build();

        var assign = await Assignment(services, definition);

        assign = assign with
        {
            Limits = ExecutionLimits.Default with
            {
                ScopeCloseTimeout = TimeSpan.FromMilliseconds(60), ReleaseGrace = TimeSpan.FromMilliseconds(60)
            }
        };

        var host = new Host { Saved = assign.Instance };

        try
        {
            var ready = await new WorkflowExecutor(services, host).ExecuteAsync(assign, CancellationToken.None);
            Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome!.Kind);
            Assert.Equal(WorkflowStatus.Suspended, host.Saved!.Status);
            Assert.Equal(0, probe.Disposals);
        }
        finally
        {
            probe.Close.TrySetResult();
        }

        await probe.DependencyDisposed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, probe.Disposals);
    }

    [Theory]
    [InlineData("activities")]
    [InlineData("checkpoint")]
    [InlineData("close")]
    [InlineData("release")]
    [InlineData("payload")]
    public async Task InvalidAssignmentLimitsAreRefusedBeforeExecution(string setting)
    {
        await using var services = Services(new());

        var limits = setting switch
        {
            "activities" => ExecutionLimits.Default with { MaxActivitiesPerRun = 0 },
            "checkpoint" => ExecutionLimits.Default with { MaxCheckpointBytes = 0 },
            "close" => ExecutionLimits.Default with { ScopeCloseTimeout = TimeSpan.Zero },
            "release" => ExecutionLimits.Default with { ReleaseGrace = TimeSpan.Zero },
            _ => ExecutionLimits.Default with { MaxPayloadBytes = 1, PayloadThresholdBytes = 2 }
        };

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => new WorkflowExecutor(services, new Host())
            .ExecuteAsync(new(Guid.NewGuid(), new(), null, "hash", 1, 0, limits, false, []), CancellationToken.None));

        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
    }

    [Fact]
    public async Task CommitResendWaitsForTheInjectedClock()
    {
        var time = new FakeTimeProvider();
        var host = new Host { RetryFirst = true };
        var definition = Workflow.Create<Data>("ClockedCommit").Step<Noop>().Build();
        var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition, new Data(), new(), time.GetUtcNow().UtcDateTime);
        var session = new ExecutionSession(host, Guid.NewGuid(), instance.InstanceId, 1, ExecutionLimits.Default, false, time);

        var pending = ExecutionCheckpointWriter.CommitAsync(session, instance, checkpoint, new Data(),
            WorkflowDefinitionValidator.ForExecution(definition), time.GetUtcNow().UtcDateTime);

        await Task.Delay(80);
        Assert.Single(host.Commits);
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(25));
        await pending.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, host.Commits.Count);
        Assert.Equal(host.Commits[0].CommitId, host.Commits[1].CommitId);
    }

    [Fact]
    public async Task HostedPauseCommitResendsUseTheInjectedClock()
    {
        var time = new FakeTimeProvider();
        var store = new PauseStore(time);
        var collection = new ServiceCollection();
        collection.AddIxIFlow(options => options.Debugging = true);
        collection.AddSingleton<IWorkflowStateRepository>(store);
        collection.AddSingleton<TimeProvider>(time);
        await using var services = collection.BuildServiceProvider();
        var definition = new WorkflowBuilder<Data, object>([], "ClockedHostPause").WaitFor<Go>("go").Build();
        var waiting = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        var pause = services.GetRequiredService<IWorkflowOperations>().PauseAsync(waiting.InstanceId, "tester");
        await store.FirstPause.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(80);
        Assert.Single(store.PauseCommits);
        Assert.False(pause.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(25));
        Assert.Equal(WorkflowExecutionStatus.Paused, (await pause.WaitAsync(TimeSpan.FromSeconds(15))).Status);
        var commits = store.PauseCommits.ToArray();
        Assert.Equal(2, commits.Length);
        Assert.Equal(commits[0], commits[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspensionCloseTimeoutKeepsTheWaitAndReleasesTheValue(bool affine)
    {
        var probe = new Probe { HoldCancellation = true };
        await using var services = Services(probe);

        var definition = new WorkflowBuilder<Data, object>([], "SuspensionCloseBudget" + affine)
            .Scope<HangingScope>(s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread),
                b => b.WaitFor<Go>("go")).Build();

        var client = services.GetRequiredService<IWorkflowClient>();

        try
        {
            var result = await client.RunAsync(definition, new Data()).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            var traces = await client.GetTraceAsync(result.InstanceId);
            Assert.Contains(traces, entry => entry.EntryType == TraceEntryType.ScopeCloseFailed);
            await probe.CancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            probe.Close.TrySetResult();
            probe.CancellationRelease.TrySetResult();
            await probe.Released.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(WorkflowExecutionStatus.Success, (await client.ResumeAsync(result.InstanceId, "go", new Go())).Status);
        }
        finally
        {
            probe.Close.TrySetResult();
            probe.CancellationRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task ResourceStopUsesOnlySavedMetadataAndPayloadPins()
    {
        await using var services = Services(new());
        var definition = Workflow.Create<Data>("StopMetadata").Step<Noop>().Build();
        var host = new Host { RefuseOrdinary = true };
        var assign = await Assignment(services, definition);
        host.Saved = assign.Instance;
        var ready = await new WorkflowExecutor(services, host).ExecuteAsync(assign, CancellationToken.None);
        Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome!.Kind);
        var stopped = Assert.Single(host.Commits, commit => commit.Stopped);
        Assert.True(stopped.PreservesPayloadPins);
        Assert.Empty(stopped.Parts);
        Assert.Empty(stopped.DeletedParts);
        Assert.Equal(WorkflowStatus.NeedsResolution, host.Saved!.Status);
    }

    [Fact]
    public async Task HostedSizeStopPreservesReferencedBranchPartsAfterTheLimitIsLowered()
    {
        var store = new InMemoryWorkflowStateRepository();

        var definition = new WorkflowBuilder<Data, object>([], "ReferencedBranchSizeStop")
            .Parallel(parallel => parallel.Do(body => body.WaitFor<Go>("left"))
                .Do(body => body.WaitFor<Go>("right"))).Build();

        string id;
        WorkflowInstance before;

        await using (var first = Provider(ExecutionLimits.DefaultMaxCheckpointBytes))
        {
            var waiting = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);
            id = waiting.InstanceId;
            before = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Contains(before.SavedParts!, part => part.Key != CheckpointParts.DataKey && part.Value.Reference != null);
        }

        await using var limited = Provider(1);
        await limited.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var result = await limited.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "left", new Go());

        Assert.True(result.Status == WorkflowExecutionStatus.NeedsResolution,
            $"{result.Status}: {result.ErrorKind}: {result.ErrorMessage}");

        var stopped = (await store.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(OperatorStopKind.ResourceLimit, ExecutionCheckpoint.ReadTrunk(stopped.ExecutionStateJson).OperatorStop!.Kind);
        Assert.Equal(before.SavedParts!.OrderBy(part => part.Key), stopped.SavedParts!.OrderBy(part => part.Key));

        foreach (var part in stopped.SavedParts!.Values.Where(part => part.Reference != null))
            Assert.False(await store.PayloadStore.CollectAsync(part.Reference!, TimeSpan.Zero));

        ServiceProvider Provider(long limit)
        {
            var services = new ServiceCollection();

            services.AddIxIFlow(options =>
            {
                options.PayloadThresholdBytes = 1;
                options.MaxCheckpointBytes = limit;
            });

            services.AddSingleton<IWorkflowStateRepository>(store);

            return services.BuildServiceProvider();
        }
    }

    [Fact]
    public async Task RefusedStopCommitStillReturnsReady()
    {
        await using var services = Services(new());
        var definition = Workflow.Create<Data>("RefusedStop").Step<Noop>().Build();
        var assign = await Assignment(services, definition);
        var ready = await new WorkflowExecutor(services, new Host { RefuseAll = true }).ExecuteAsync(assign, CancellationToken.None);
        Assert.Equal(AssignmentOutcomeKind.Interrupted, ready.Outcome!.Kind);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("engine")]
    [InlineData("verification")]
    public async Task StopCommitFailuresReturnReadyInsteadOfEscaping(string failure)
    {
        await using var services = Services(new());
        var definition = Workflow.Create<Data>("FailedStopTransport").Step<Noop>().Build();
        var assign = await Assignment(services, definition);

        var host = new Host
        {
            Saved = assign.Instance, RefuseOrdinary = true,
            StopError = failure switch
            {
                "transport" => new IOException("Stop transport failed"),
                "engine" => new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "Stop store failed"),
                _ => new CheckpointVerificationException("Stop verification failed")
            }
        };

        var ready = await new WorkflowExecutor(services, host).ExecuteAsync(assign, CancellationToken.None);
        Assert.Equal(AssignmentOutcomeKind.Interrupted, ready.Outcome!.Kind);
        Assert.Equal(typeof(WorkflowEngineException).FullName, ready.Outcome.ErrorType);
        Assert.Contains(host.Commits, commit => commit.Stopped);
    }

    [Fact]
    public async Task AStoppedCommitWithALostAcknowledgementResolvesAfterOwnershipIsReleased()
    {
        await using var services = Services(new());
        var definition = Workflow.Create<Data>("LostStopAcknowledgement").Step<Noop>().Build();
        var assign = await Assignment(services, definition);
        var host = new Host { Saved = assign.Instance, RefuseOrdinary = true, LoseFirstStopAck = true };
        var executor = new WorkflowExecutor(services, host);
        host.StopApplied = () => executor.Deliver(new Cancel(assign.AssignmentId, CancelKind.Ownership));
        var ready = await executor.ExecuteAsync(assign, CancellationToken.None);
        Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome!.Kind);
        var stops = host.Commits.Where(commit => commit.Stopped).ToArray();
        Assert.Equal(2, stops.Length);
        Assert.Equal(stops[0].CommitId, stops[1].CommitId);
        Assert.Equal(stops[0].CommitId, ready.LastAcknowledgedCommit!.CommitId);
    }

    [Fact]
    public async Task AnEngineFailureDuringWaitMatchingLeavesTheEventPendingForAnotherPass()
    {
        await using var services = Services(new());
        var definition = new WorkflowBuilder<Data, object>([], "MatchingEngineFailure").WaitFor<Go>("go").Build();
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);

        var branch = new ContinuationState
        {
            Status = ContinuationStatus.Waiting, Stack = [new ScopePosition { ScopeId = "root" }],
            Previous = new SerializedValue { Type = "missing-type", Json = StoredJson.Inline("{}") }
        };

        var wait = new WaitState
            { ContinuationId = branch.Id, StepId = "root/0", Key = "go", EventType = WorkflowTypeIdentity.SavedName(typeof(Go)) };

        var checkpoint = new ExecutionCheckpoint
            { StructureHash = scopes.StructureHash, Continuations = [branch], Waits = [wait], RootData = new Data() };

        var instance = new WorkflowInstance { InstanceId = "matching-failure" };

        var pending = new StoredWorkflowEvent(instance.InstanceId, "event", 1, wait.EventType, "go", null,
            StoredJson.Inline("{}"), DateTime.UtcNow, EventOutcome.Pending);

        var host = new Host { Inbox = new([pending], 1, new Dictionary<string, long> { [wait.Id] = 0 }) };
        var session = new ExecutionSession(host, Guid.NewGuid(), instance.InstanceId, 1, ExecutionLimits.Default, false);
        var intake = new EventIntake(services, TimeProvider.System);
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => intake.TakeAsync(scopes, checkpoint, session, instance));
        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
        Assert.Empty(checkpoint.Inbox.Settlements);
        Assert.Null(checkpoint.Inbox.ReadThrough);
        branch.Previous = null;
        await intake.TakeAsync(scopes, checkpoint, session, instance);
        Assert.Equal(EventOutcome.Consumed, Assert.Single(checkpoint.Inbox.Settlements).Outcome);
    }

    [Fact]
    public async Task InlineStartDoesNotReturnAnIdThatWasNeverSaved()
    {
        var store = new InMemoryWorkflowStateRepository();
        var collection = new ServiceCollection();
        collection.AddIxIFlow();
        collection.AddSingleton<IWorkflowStateRepository>(store);
        await using var services = collection.BuildServiceProvider();
        var id = Guid.NewGuid().ToString("N");
        await StoreTestSupport.LeaseAsync(store, id);
        var definition = Workflow.Create<Data>("UnsavedInlineStart").Step<Noop>().Build();

        try
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => services.GetRequiredService<WorkflowHost>()
                .RunAsync("outer-slot", (_, _, token) => services.GetRequiredService<IWorkflowClient>()
                    .StartAsync(definition, new Data(), new() { InstanceId = id }, token), CancellationToken.None));

            Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
            Assert.Contains("start was not saved", error.Message);
            Assert.Null(await store.GetWorkflowInstanceAsync(id));
        }
        finally
        {
            await StoreTestSupport.ReleaseAsync(store, id);
        }
    }

    [Fact]
    public async Task ABlockingConditionPausesWithConditionEvaluationFailed()
    {
        using var compiler = new BlockingCompiler();
        var collection = new ServiceCollection();
        collection.AddIxIFlow(options => options.Debugging = true);
        collection.AddSingleton<IBreakpointConditionCompiler>(compiler);
        await using var services = collection.BuildServiceProvider();

        var definition = new WorkflowBuilder<Data, object>([], "ConditionTimeBudget").WaitFor<Go>("go")
            .Step<Noop>().WithId("target").Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());

        await services.GetRequiredService<IWorkflowOperations>().SetBreakpointsAsync(idle.InstanceId,
            [new("blocked", "target", Condition: "true")], "tester");

        try
        {
            var result = await client.ResumeAsync(idle.InstanceId, "go", new Go()).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
            var saved = (await services.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(idle.InstanceId))!;
            Assert.Equal("ConditionEvaluationFailed", ExecutionCheckpoint.Read(saved.ExecutionStateJson).Debug.Stop!.Reason);
        }
        finally
        {
            compiler.Gate.Set();
        }

        await compiler.Finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task MemoryPayloadDigestLocksRetireAfterUseIncludingCancelledWaiters()
    {
        var store = new InMemoryWorkflowStateRepository().PayloadStore;

        for (var index = 0; index < 100; index++)
        {
            var reference = await store.PutAsync(Encoding.UTF8.GetBytes(index.ToString()));
            Assert.True(await store.CollectAsync(reference, TimeSpan.Zero));
        }

        var gates = (System.Collections.IDictionary)store.GetType().GetField("_gates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;

        Assert.Empty(gates.Keys.Cast<object>());
        var value = await store.PutAsync("{}"u8.ToArray());
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var owner = store.InTransactionAsync(async (transaction, token) =>
        {
            await transaction.PinAsync(new("test", "owner"), value, token);
            held.SetResult();
            await release.Task;
        }).AsTask();

        await held.Task;
        using var cancellation = new CancellationTokenSource();
        var waiter = store.GetAsync(value, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.SetResult();
        await owner;
        Assert.Empty(gates.Keys.Cast<object>());
        Assert.NotNull(await store.GetAsync(value));
    }

    private static ServiceProvider Services(Probe probe, bool validateScopes = false)
    {
        var services = new ServiceCollection().AddSingleton(probe);
        services.AddIxIFlow();
        services.AddScoped<Dependency>();
        services.AddTransient<ScopedActivity>();

        services.AddSingleton(ExecutionLimits.Default with
        {
            ScopeCloseTimeout = TimeSpan.FromMilliseconds(60), ReleaseGrace = TimeSpan.FromMilliseconds(60)
        });

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = validateScopes });
    }

    internal static async Task<Assign> Assignment(IServiceProvider services, WorkflowDefinition definition)
    {
        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
        instance.ExecutionStateJson = WorkflowJson.Serialize(checkpoint);
        instance = CheckpointCommit.Create(instance, 1, 0).ReadInstance();

        return new(Guid.NewGuid(), instance, instance.SavedParts, checkpoint.StructureHash, 1, 0, ExecutionLimits.Default, false, []);
    }

    public sealed class Data
    {
        public int Count { get; set; }
    }

    public sealed class Go;

    public sealed class Probe
    {
        public int Calls;
        public int Disposals;
        public bool HoldCancellation;
        public TaskCompletionSource Close { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DependencyDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class Dependency(Probe probe) : IDisposable
    {
        public void Dispose()
        {
            probe.Disposals++;
            probe.DependencyDisposed.TrySetResult();
        }
    }

    public sealed class ScopedActivity(Dependency dependency, Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken token)
        {
            GC.KeepAlive(dependency);
            probe.Calls++;

            return Task.CompletedTask;
        }
    }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken token) => Task.CompletedTask;
    }

    public sealed class Value(Probe probe) : IDisposable
    {
        public void Dispose() => probe.Released.TrySetResult();
    }

    public sealed class HangingScope(Probe probe, Dependency dependency) : IScopeActivity<Value>
    {
        public Task<Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token)
        {
            GC.KeepAlive(dependency);

            return Task.FromResult(new Value(probe));
        }

        public async Task CloseAsync(Value value, ScopeClosing closing, IScopeContext context, CancellationToken token)
        {
            if (closing.Reason != ScopeCloseReason.Suspended)
                return;

            using var registration = probe.HoldCancellation
                ? token.Register(() =>
                {
                    probe.CancellationEntered.TrySetResult();
                    probe.CancellationRelease.Task.GetAwaiter().GetResult();
                })
                : default;

            await probe.Close.Task;
        }
    }

    private sealed class BlockingCompiler : IBreakpointConditionCompiler, IDisposable
    {
        public ManualResetEventSlim Gate { get; } = new();
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BreakpointConditionCompilation Compile(BreakpointConditionRequest request) => new((_, _, _, _, _) =>
        {
            try
            {
                Gate.Wait();

                return true;
            }
            finally
            {
                Finished.TrySetResult();
            }
        }, []);

        public void Dispose()
        {
            Gate.Set();
            Gate.Dispose();
        }
    }

    private sealed class PauseStore(TimeProvider time) : TestWorkflowStateRepository(time)
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> PauseCommits { get; } = new();
        public TaskCompletionSource FirstPause { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;

        public override Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit,
            CancellationToken cancellationToken = default)
        {
            if (commit.ReadTrunk().Status == WorkflowStatus.Paused)
            {
                PauseCommits.Enqueue(commit.CommitId);

                if (Interlocked.Increment(ref _attempts) == 1)
                {
                    FirstPause.TrySetResult();

                    throw new IOException("Transient pause commit failure");
                }
            }

            return base.CommitWorkflowInstanceAsync(commit, cancellationToken: cancellationToken);
        }
    }

    internal sealed class Host : IExecutionHost
    {
        public WorkflowInstance? Saved;
        public List<CheckpointCommit> Commits { get; } = [];
        public InboxPage? Inbox;
        public Exception? StopError;
        public bool LoseFirstStopAck;
        public Action? StopApplied;
        public bool RetryFirst;
        public bool RefuseOrdinary;
        public bool RefuseAll;

        public Task<CheckpointReply> CheckpointAsync(Checkpoint request)
        {
            Commits.Add(request.Commit);

            if (request.Commit.Stopped && StopError != null)
                throw StopError;

            if (RefuseAll || RefuseOrdinary && !request.Commit.Stopped)
                throw new CheckpointLimitExceededException(100, 1);

            if (RetryFirst && Commits.Count == 1)
                return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Retry));

            var parts = request.Commit.ApplyParts(Saved?.SavedParts);
            Saved = request.Commit.ReadTrunk();
            Saved.Saved(parts, request.Commit.ExpectedRevision + 1);

            if (request.Commit.Stopped && LoseFirstStopAck)
            {
                LoseFirstStopAck = false;
                StopApplied?.Invoke();

                return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Unknown));
            }

            return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Ack, request.Commit.ExpectedRevision + 1));
        }

        public Task<ReadPayloadReply> ReadPayloadAsync(ReadPayload request) =>
            Task.FromResult(new ReadPayloadReply(request.AssignmentId, Refusal: WorkflowErrorKind.PayloadUnavailable));

        public Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) => Task.FromResult(new ReadInboxReply(request.AssignmentId,
            Inbox ?? new InboxPage([], request.AfterSequence, new Dictionary<string, long>())));

        public Task<ReadWaitReply> ReadWaitAsync(ReadWait request) => Task.FromResult(new ReadWaitReply(request.AssignmentId));
        public Task<StartChildReply> StartChildAsync(StartChild request) => throw new NotSupportedException();
    }
}
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.Infrastructure.CancellationMixedCleanupScenario;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "W-06")]
public sealed class CancellationScopeMixedCleanupTests
{
    [Theory, InlineData("handler"), InlineData("finally"), InlineData("compensation")]
    public void CleanupClassificationUsesTheNearestAbandonedRetryBody(string kind)
    {
        var outer = new ScopePosition { TryState = new() { Phase = TryPhase.Finally } };

        var retry = new ScopePosition
        {
            SagaState = new()
            {
                Phase = SagaPhase.Compensating, ErrorAction = SagaContinuationAction.Retry,
                ErrorHandlerStarted = true, ErrorHandlerIndex = 0, CompensationFloor = 2, CompensationCursor = 1
            }
        };

        var nested = new ScopePosition
        {
            CancellationState = kind == "handler" ? new() { Phase = CancellationScopePhase.Handler } : null,
            TryState = kind == "finally" ? new() { Phase = TryPhase.Finally } : null,
            SagaState = kind == "compensation" ? new() { Phase = SagaPhase.Compensating } : null
        };

        var continuation = new ContinuationState { Status = ContinuationStatus.Waiting, Stack = [outer, retry, nested] };
        var checkpoint = new ExecutionCheckpoint();
        checkpoint.Continuations.Add(continuation);
        ContinuationTransitions.CancelTree(checkpoint, continuation);
        Assert.True(ContinuationTransitions.IsCleanup(continuation));
        Assert.Equal(ContinuationStatus.Waiting, continuation.Status);
        Assert.True(outer.TryState!.IsCancellationCleanup);

        Assert.True(nested.CancellationState?.IsCancellationCleanup ??
            nested.TryState?.IsCancellationCleanup ?? nested.SagaState!.IsCancellationCleanup);

        continuation.Stack.RemoveAt(2);
        Assert.False(ContinuationTransitions.IsCleanup(continuation));
    }

    public static IEnumerable<object[]> NestedRetryCases() =>
        from kind in new[] { "handler", "finally" }
        from running in new[] { false, true }
        from failure in new[] { "none", "handled", "unhandled" }
        select new object[] { kind, running, failure };

    [Theory, MemberData(nameof(NestedRetryCases))]
    public async Task NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestart(string kind, bool running, string failure)
    {
        var effects = new Effects();
        var repository = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();

        ServiceProvider Open(IWorkflowStateRepository store) => new ServiceCollection().AddIxIFlow(o => o.VerifyCheckpointParts = true)
            .Services
            .AddSingleton(store).AddSingleton<IWorkflowCatalog>(catalog).AddSingleton(effects).BuildServiceProvider();

        await ExerciseNestedRetryCleanupAsync(kind, running, failure, false, () => repository,
            store => Task.FromResult(Open(store)), _ => Task.CompletedTask, effects, catalog).WaitAsync(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RetriedForwardWorkStillBelongsToItsEnclosingCleanup()
    {
        var effects = new Effects();
        var repository = new InMemoryWorkflowStateRepository();

        await using var services = new ServiceCollection().AddIxIFlow(o => o.VerifyCheckpointParts = true).Services
            .AddSingleton<IWorkflowStateRepository>(repository).AddSingleton(effects).BuildServiceProvider();

        var definition = new WorkflowBuilder<Data, object>([], "CancellationRetriedForwardCleanup").Try(t =>
        {
        }).Finally(f => f
            .Saga(s => s.Step<Seed>(a => a.CompensateWith<UndoA>()).Step<FailForward>().WaitFor<Go>("forward"))
            .OnError<InvalidOperationException>(e => e.WaitFor<Go>("retry").CompensateNone().ThenRetry(1))
            .Step<OuterTail>()).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.ResumeAsync(idle.InstanceId, "retry", new Go())).Status);
        var state = await Read(repository, idle.InstanceId);
        Assert.Equal(SagaPhase.Forward, state.Continuations[0].Stack.Single(f => f.SagaState != null).SagaState!.Phase);
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.CancelAsync(idle.InstanceId, new() { ReasonCode = "test" })).Status);
        Assert.Equal("forward", Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId))).Key);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await client.ResumeAsync(idle.InstanceId, "forward", new Go())).Status);
        Assert.Equal(new[] { "outer-tail" }, effects.Events);
        Assert.Equal(1, effects.Seeds);
        Assert.Equal(2, effects.Attempts);
    }

    [Theory, InlineData(false, "wait"), InlineData(true, "wait"), InlineData(false, "activity"), InlineData(true, "activity"),
     InlineData(false, "child"), InlineData(true, "child")]
    public async Task ActiveRetryWorkIsCancelledBeforeRetainedUndoAndEnclosingTail(bool finallyOutside, string work)
    {
        var effects = new Effects();
        var repository = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();

        ServiceProvider Open(IWorkflowStateRepository store) => new ServiceCollection().AddIxIFlow(o =>
            {
                o.VerifyCheckpointParts = true;
                o.PayloadThresholdBytes = 1;
            }).Services
            .AddSingleton(store).AddSingleton<IWorkflowCatalog>(catalog).AddSingleton(effects).BuildServiceProvider();

        await ExerciseRetryAsync(finallyOutside, work, false, () => repository, store => Task.FromResult(Open(store)),
            _ => Task.CompletedTask, effects, catalog).WaitAsync(TimeSpan.FromMinutes(2));
    }

    public static IEnumerable<object[]> RecoveryCases() =>
        from finallyOutside in new[] { false, true }
        from failure in new[] { false, true }
        from boundary in RecoveryBoundaries
        from lost in new[] { false, true }
        where !failure || boundary is not ("boundary/resolution" or "boundary/start")
        select new object[] { finallyOutside, failure, boundary, lost };

    [Theory, MemberData(nameof(RecoveryCases))]
    public async Task EveryMixedCleanupCommitRecoversItsOwnerAndCursor(bool finallyOutside, bool failure, string boundary, bool lost)
    {
        var effects = new Effects();
        var repository = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();

        ServiceProvider Open(IWorkflowStateRepository store) => new ServiceCollection().AddIxIFlow(o =>
            {
                o.VerifyCheckpointParts = true;
                o.PayloadThresholdBytes = 1;
            }).Services
            .AddSingleton(store).AddSingleton<IWorkflowCatalog>(catalog).AddSingleton(effects).BuildServiceProvider();

        await ExerciseRecoveryAsync(finallyOutside, failure, boundary, lost, false, () => repository, store => Task.FromResult(Open(store)),
            _ => Task.CompletedTask, effects, catalog).WaitAsync(TimeSpan.FromMinutes(2));
    }

    public static IEnumerable<object[]> Cases() =>
        from finallyOutside in new[] { false, true }
        from compensation in new[] { false, true }
        from unresolved in new[] { false, true }
        from restart in new[] { false, true }
        select new object[] { finallyOutside, compensation, unresolved, restart };

    [Theory, MemberData(nameof(Cases))]
    public async Task CancellationPreservesEveryMixedCleanupTail(bool finallyOutside, bool compensation, bool unresolved, bool restart)
    {
        var effects = new Effects();
        var store = new InterruptedStore(effects, unresolved);
        var catalog = new WorkflowCatalog();
        var definition = Definition(finallyOutside, compensation, unresolved);
        await catalog.RegisterWorkflowAsync(definition);

        ServiceProvider Open() => new ServiceCollection().AddIxIFlow(o =>
            {
                o.VerifyCheckpointParts = true;
                o.PayloadThresholdBytes = 1;
            }).Services
            .AddSingleton<IWorkflowStateRepository>(store).AddSingleton<IWorkflowCatalog>(catalog).AddSingleton(effects)
            .BuildServiceProvider();

        var services = Open();
        var id = Guid.NewGuid().ToString("N");

        try
        {
            var client = services.GetRequiredService<IWorkflowClient>();

            if (unresolved)
            {
                await Assert.ThrowsAsync<WorkflowEngineException>(() => client.RunAsync(definition, new Data(), new() { InstanceId = id }));
                store.Interrupt = false;
                await services.DisposeAsync();
                services = Open();
                client = services.GetRequiredService<IWorkflowClient>();
                Assert.Equal(WorkflowExecutionStatus.NeedsResolution, (await client.RecoverAsync(id)).Status);
            }
            else
                Assert.Equal(WorkflowExecutionStatus.Suspended,
                    (await client.RunAsync(definition, new Data(), new() { InstanceId = id })).Status);

            Assert.Equal(unresolved ? WorkflowExecutionStatus.NeedsResolution : WorkflowExecutionStatus.Suspended,
                (await client.CancelAsync(id, new() { ReasonCode = "test" })).Status);

            AssertOwner(await Read(store, id), finallyOutside, compensation && unresolved);

            if (restart)
            {
                await services.DisposeAsync();
                services = Open();
                client = services.GetRequiredService<IWorkflowClient>();
                AssertOwner(await Read(store, id), finallyOutside, compensation && unresolved);
            }

            var done = unresolved
                ? await client.ResolveAsync(id, Assert.Single(await client.GetPendingActivitiesAsync(id)).InvocationId,
                    ActivityResolution.Completed(new Dictionary<string, object?>()))
                : await client.ResumeAsync(id, "cleanup", new Go());

            Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
            AssertEffects(effects, compensation);
            var trace = await client.GetTraceAsync(id);
            Assert.Single(trace, e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
            Assert.Single(trace, e => e.EntryType == TraceEntryType.CancellationHandlerCompleted);
        }
        finally
        {
            await services.DisposeAsync();
        }
    }

    public static IEnumerable<object[]> OwnershipOrders() =>
        from order in new[] { "HFS", "HSF", "FHS", "FSH", "SHF", "SFH" }
        from unresolved in new[] { false, true }
        select new object[] { order, unresolved };

    [Theory, MemberData(nameof(OwnershipOrders))]
    public void CancellationSelectsOneOwnerAcrossTheCompleteStack(string order, bool unresolved)
    {
        var checkpoint = new ExecutionCheckpoint();

        var continuation = new ContinuationState
        {
            Status = unresolved ? ContinuationStatus.WaitingResolution : ContinuationStatus.Waiting,
            PendingActivity = unresolved ? new ActivityInvocationState() : null,
            Stack = order.Select(kind => new ScopePosition
            {
                CancellationState = kind == 'H' ? new() { Phase = CancellationScopePhase.Handler, IsCancellationCleanup = true } : null,
                TryState = kind == 'F' ? new() { Phase = TryPhase.Finally, IsCancellationCleanup = true } : null,
                SagaState = kind == 'S' ? new() { Phase = SagaPhase.Compensating, IsCancellationCleanup = true } : null
            }).ToList()
        };

        checkpoint.Continuations.Add(continuation);
        ContinuationTransitions.CancelTree(checkpoint, continuation);

        var owners = continuation.Stack.Select(f => f.CancellationState?.IsCancellationCleanup
            ?? f.TryState?.IsCancellationCleanup ?? f.SagaState!.IsCancellationCleanup).ToArray();

        Assert.Equal(new[] { true, false, false }, owners);
        Assert.True(continuation.Stack.Single(f => f.SagaState != null).SagaState!.CancellationRequested);
    }

    private sealed class InterruptedStore(Effects effects, bool interrupt) : TestWorkflowStateRepository
    {
        public bool Interrupt { get; set; } = interrupt;

        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit,
            CancellationToken cancellationToken = default)
        {
            var instance = commit.ReadInstance(commit.ReplacesParts ? null : await GetWorkflowInstanceAsync(commit.InstanceId));

            if (Interrupt && effects.Events.Contains("boundary") &&
                (await Read(this, instance)).Continuations[0].PendingActivity == null)
                throw new IOException("Interrupted cleanup activity completion");

            return await base.CommitWorkflowInstanceAsync(commit, cancellationToken: cancellationToken);
        }
    }
}
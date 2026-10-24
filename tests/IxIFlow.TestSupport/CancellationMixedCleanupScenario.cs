using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Reflection;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

[assembly: InternalsVisibleTo("IxIFlow.Tests")]
[assembly: InternalsVisibleTo("IxIFlow.Conformance")]

namespace IxIFlow.Tests.Infrastructure;

/// <summary>The same mixed-cleanup definitions and durable assertions for engine and store conformance.</summary>
public static class CancellationMixedCleanupScenario
{
    public static WorkflowDefinition RetryDefinition(bool finallyOutside, string work)
    {
        void Cleanup(IWorkflowBuilder<Data, object> h) => h.Saga(s => s
                .Step<Seed>(a => a.CompensateWith<UndoA>())
                .Step<SeedB>(a => a.CompensateWith<UndoB>())
                .Step<FailForward>())
            .OnError<InvalidOperationException>(e =>
            {
                if (work == "activity") e.CancellationScope(b => b.Request<RetryWork>("retry").WaitFor<Go>("retry"), h => { }).CompensateNone().ThenRetry(1);
                else if (work == "child") e.CancellationScope(b => b.Invoke<Data>("CancellationRetryChild", 1), h => { }).CompensateNone().ThenRetry(1);
                else e.WaitFor<Go>("retry").CompensateNone().ThenRetry(1);
            })
            .WaitFor<Go>("tail").Step<OuterTail>();
        var b = new WorkflowBuilder<Data, object>([], "CancellationActiveRetryCleanup");
        return finallyOutside ? b.Try(t => { }).Finally(Cleanup).Build()
            : b.Try(t => t.CancellationScope(x => x.Throw<InvalidOperationException>(c => new("business")), Cleanup))
                .Catch<InvalidOperationException>(c => { }).Build();
    }

    internal static async Task ExerciseNestedRetryCleanupAsync(string kind, bool alreadyRunning, string failure, bool takeover,
        Func<IWorkflowStateRepository> repository, Func<IWorkflowStateRepository, Task<ServiceProvider>> open,
        Func<string, Task> expire, Effects effects, IWorkflowCatalog catalog)
    {
        void Steps(ICatchWorkflowBuilder<Data, EmptyFault, object> h)
        {
            h.WaitFor<Go>("nested");
            if (failure == "handled") h.Try(t => t.Step<NestedFirst>()).Catch<ArgumentException>(c => c.Step<Mark>(a => a.Input(x => x.Text).From(c => "nested-caught")));
            else h.Step<NestedFirst>();
            h.Step<Mark>(a => a.Input(x => x.Text).From(c => "nested-second"));
        }
        void Retry(ICatchWorkflowBuilder<Data, EmptyFault, object> h)
        {
            if (kind == "handler") h.Try(t => t.CancellationScope(b =>
            {
                if (alreadyRunning) b.Throw<ArgumentException>(c => new("local"));
                else b.WaitFor<Go>("inner-body");
            }, Steps)).Catch<ArgumentException>(c => { });
            else h.Try(b => { if (!alreadyRunning) b.WaitFor<Go>("inner-body"); }).Finally(Steps);
            h.Step<Mark>(a => a.Input(x => x.Text).From(c => "abandoned-retry-tail"));
        }
        var definition = new WorkflowBuilder<Data, object>([], "CancellationNestedRetryCleanup").Try(t => { }).Finally(f => f
            .Saga(s => s.Step<Seed>(a => a.CompensateWith<UndoA>()).Step<SeedB>(a => a.CompensateWith<UndoB>()).Step<FailForward>())
            .OnError<InvalidOperationException>(e => e.CancellationScope(Retry, h => { }).CompensateNone().ThenRetry(1))
            .Step<OuterTail>()).Build();
        await catalog.RegisterWorkflowAsync(definition);
        effects.FailNested = failure != "none";
        var inner = repository(); var proxy = BoundaryFault.Wrap(inner); var fault = (BoundaryFault)proxy;
        var id = Guid.NewGuid().ToString("N"); fault.InstanceId = id;
        await using (var first = await open(proxy))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.RunAsync(definition, new Data(), new() { InstanceId = id })).Status);
            Assert.Equal(alreadyRunning ? "nested" : "inner-body", Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: id))).Key);
        }
        await DisposeRepository(inner);
        inner = repository(); proxy = BoundaryFault.Wrap(inner); fault = (BoundaryFault)proxy;
        fault.InstanceId = id; fault.RetainLease = takeover;
        await using (var second = await open(proxy))
        {
            var client = second.GetRequiredService<IWorkflowClient>();
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.CancelAsync(id, new() { ReasonCode = "test" })).Status);
            Assert.Equal("nested", Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: id))).Key);
            var state = await Read(inner, id);
            Assert.True(state.Continuations[0].Stack.First(f => f.TryState != null).TryState!.IsCancellationCleanup);
            Assert.True(state.Continuations[0].Stack.First(f => f.SagaState != null).SagaState!.CancellationRequested);
        }
        if (takeover) await expire(id);
        await DisposeRepository(inner);
        inner = repository(); proxy = BoundaryFault.Wrap(inner);
        await using (var third = await open(proxy))
        {
            var client = third.GetRequiredService<IWorkflowClient>();
            var done = await client.ResumeAsync(id, "nested", new Go());
            Assert.Equal(failure == "unhandled" ? WorkflowExecutionStatus.Faulted : WorkflowExecutionStatus.Cancelled, done.Status);
            if (failure == "unhandled") Assert.Contains("nested failure", done.ErrorMessage);
            var expected = new List<string> { "nested-first" };
            if (failure == "handled") expected.Add("nested-caught");
            if (failure != "unhandled") expected.Add("nested-second");
            expected.AddRange(["undo-B", "undo-A", "outer-tail"]);
            Assert.Equal(expected, effects.Events);
            Assert.Equal(1, effects.Seeds); Assert.Equal(1, effects.Attempts);
            Assert.DoesNotContain(await client.GetTraceAsync(id), e => e.EntryType == TraceEntryType.SagaRetryStarted);
            if (takeover) Assert.True(((BoundaryFault)proxy).Grant!.Epoch > fault.Grant!.Epoch);
        }
        await DisposeRepository(inner);
    }

    public static WorkflowDefinition RecoveryDefinition(bool finallyOutside, bool compensationFailure)
    {
        void Inner(IWorkflowBuilder<Data, object> h)
        {
            h.Try(t => t.Saga(s =>
                {
                    s.Step<Seed>(a => a.CompensateWith<UndoA>())
                        .Step<SeedB>(a => a.CompensateWith<UndoB>())
                        .Step<SeedC>(a => a.CompensateWith<Boundary>()
                            .CompensateWith<UndoCTail>());
                    if (compensationFailure) s.CancellationScope(b => b.Throw<InvalidOperationException>(c => new("saga")), h => { });
                    else s.Throw<InvalidOperationException>(c => new("saga"));
                }).OnError<InvalidOperationException>(e => e.CompensateUpTo<SeedC>().ThenRetry(1)))
                .Catch<SagaTerminatedException>(c => { })
                .Step<InnerTail>();
        }
        var b = new WorkflowBuilder<Data, object>([], "CancellationMixedRecovery");
        return finallyOutside
            ? b.Try(t => { }).Finally(f => f.Try(t => t.CancellationScope(
                    b => b.Throw<InvalidOperationException>(c => new("business")), Inner))
                .Catch<InvalidOperationException>(c => { })
                .Step<OuterTail>()).Build()
            : b.Try(t => t.CancellationScope(b => b.Throw<InvalidOperationException>(c => new("business")),
                    h => h.Try(t => { }).Finally(Inner).Step<OuterTail>()))
                .Catch<InvalidOperationException>(c => { }).Build();
    }

    public static readonly string[] RecoveryBoundaries = ["owner", "boundary/resolution", "boundary/start", "boundary/end", "undo-C-tail/start", "undo-C-tail/end", "undo-B/start", "undo-B/end",
        "undo-A/start", "undo-A/end", "inner-tail/start", "inner-tail/end", "outer-tail/start", "outer-tail/end", "terminal"];

    // Every recovered commit is compared with its durable cursor, not just its final result.
    internal static bool AtBoundary(ExecutionCheckpoint state, WorkflowInstance instance, WorkflowScopeCatalog scopes, string boundary)
    {
        if (boundary == "terminal") return WorkflowStoreRules.IsTerminal(instance.Status);
        if (!state.Continuations[0].CancellationUnwind) return false;
        if (boundary == "owner") return true;
        var parts = boundary.Split('/');
        var pending = state.Continuations[0].PendingActivity;
        var id = parts[0] == "boundary" ? scopes.AllSteps.Single(s => s.Value.ActivityType == typeof(Boundary)).Key : FindMark(scopes, parts[0]);
        if (parts[1] == "resolution") return pending?.Resolution != null && pending.Attempts.Count == 1;
        if (parts[0] == "boundary" && parts[1] == "start") return pending?.StepId == id && pending.Attempts.Count > 1;
        if (parts[1] == "start") return pending?.StepId == id;
        var frame = state.Continuations[0].Stack.LastOrDefault();
        return pending == null && frame is { NextStepIndex: > 0 } &&
            scopes.Id(scopes.Steps(frame.ScopeId)[frame.NextStepIndex - 1]) == id;
    }

    private static string FindMark(WorkflowScopeCatalog scopes, string text) =>
        scopes.AllSteps.Single(s => s.Value.ActivityType == (text switch { "undo-A" => typeof(UndoA), "undo-B" => typeof(UndoB), "undo-C-tail" => typeof(UndoCTail), "inner-tail" => typeof(InnerTail), _ => typeof(OuterTail) })).Key;

    internal static async Task ExerciseRetryAsync(bool finallyOutside, string work, bool takeover,
        Func<IWorkflowStateRepository> repository, Func<IWorkflowStateRepository, Task<ServiceProvider>> open,
        Func<string, Task> expire, Effects effects, IWorkflowCatalog catalog)
    {
        var definition = RetryDefinition(finallyOutside, work); await catalog.RegisterWorkflowAsync(definition);
        if (work == "child") await catalog.RegisterWorkflowAsync(new WorkflowBuilder<Data, object>([], "CancellationRetryChild").WaitFor<Go>("child").Build());
        var inner = repository(); var proxy = BoundaryFault.Wrap(inner); var fault = (BoundaryFault)proxy;
        if (work == "activity") fault.Match = (state, _) => effects.Events.Contains("retry-work") && state.Continuations[0].PendingActivity == null;
        var id = Guid.NewGuid().ToString("N"); string waitId; string? childId = null;
        fault.InstanceId = id;
        await using (var first = await open(proxy))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var start = client.RunAsync(definition, new Data(), new() { InstanceId = id });
            if (work == "activity") await Assert.ThrowsAsync<WorkflowEngineException>(() => start);
            else Assert.Equal(WorkflowExecutionStatus.Suspended, (await start).Status);
            var before = await Read(inner, id);
            waitId = Assert.Single(before.Waits).Id;
            if (work == "child")
            {
                childId = Assert.Single(before.Waits).ChildWorkflowId;
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!(await client.GetOpenWaitsAsync(new(InstanceId: childId))).Any(w => w.Key == "child"))
                    await Task.Delay(10, patience.Token);
            }
            var saga = Assert.Single(before.Continuations[0].Stack, f => f.SagaState != null).SagaState!;
            Assert.Equal(2, saga.CompletedSteps.Count); Assert.Equal(2, saga.CompensationFloor); Assert.Equal(1, saga.CompensationCursor);
        }
        await DisposeRepository(inner);
        inner = repository(); proxy = BoundaryFault.Wrap(inner); fault = (BoundaryFault)proxy; fault.RetainLease = takeover;
        fault.InstanceId = id;
        await using (var second = await open(proxy))
        {
            var client = second.GetRequiredService<IWorkflowClient>();
            if (work == "activity") Assert.Equal(WorkflowExecutionStatus.NeedsResolution, (await client.RecoverAsync(id)).Status);
            var cancelled = await client.CancelAsync(id, new() { ReasonCode = "test" });
            Assert.Equal(work == "activity" ? WorkflowExecutionStatus.NeedsResolution : WorkflowExecutionStatus.Suspended, cancelled.Status);
            if (work == "child")
            {
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!(await Read(inner, id)).Waits.Any(w => w.Key == "tail")) await Task.Delay(10, patience.Token);
                Assert.Equal(WorkflowStatus.Cancelled, (await inner.GetWorkflowInstanceAsync(childId!))?.Status);
            }
            var closed = (await inner.GetWaitAsync(id, waitId))!;
            Assert.NotNull(closed.ClosedAtUtc);
            Assert.Equal(work == "child" ? WaitCloseReason.Answered : WaitCloseReason.Cancelled, closed.CloseReason);
            var state = await Read(inner, id);
            if (work == "activity")
            {
                var saga = Assert.Single(state.Continuations[0].Stack, f => f.SagaState != null).SagaState!;
                Assert.True(saga.CancellationRequested); Assert.False(saga.IsCancellationCleanup);
                Assert.Equal(2, saga.CompensationFloor); Assert.Equal(1, saga.CompensationCursor);
            }
        }
        if (takeover) await expire(id);
        await DisposeRepository(inner);
        inner = repository(); var successor = BoundaryFault.Wrap(inner);
        await using (var third = await open(successor))
        {
            var client = third.GetRequiredService<IWorkflowClient>();
            if (work == "activity")
                Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.ResolveAsync(id,
                    Assert.Single(await client.GetPendingActivitiesAsync(id)).InvocationId,
                    ActivityResolution.Completed(new Dictionary<string, object?>()))).Status);
            var done = await client.ResumeAsync(id, "tail", new Go());
            Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
            Assert.Empty(await client.GetOpenWaitsAsync(new(InstanceId: id)));
            Assert.Equal(work == "activity" ? ["retry-work", "undo-B", "undo-A", "outer-tail"] : new[] { "undo-B", "undo-A", "outer-tail" }, effects.Events);
            Assert.Equal(1, effects.Seeds); Assert.Equal(1, effects.Attempts);
            Assert.DoesNotContain(await client.GetTraceAsync(id), e => e.EntryType == TraceEntryType.SagaRetryStarted);
            if (takeover) Assert.True(((BoundaryFault)successor).Grant!.Epoch > fault.Grant!.Epoch);
        }
        await DisposeRepository(inner);
    }

    internal static async Task ExerciseRecoveryAsync(bool finallyOutside, bool compensationFailure, string boundary, bool lost, bool takeover,
        Func<IWorkflowStateRepository> repository, Func<IWorkflowStateRepository, Task<ServiceProvider>> open,
        Func<string, Task> expire, Effects effects, IWorkflowCatalog catalog)
    {
        var definition = RecoveryDefinition(finallyOutside, compensationFailure); await catalog.RegisterWorkflowAsync(definition);
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var inner = repository(); var proxy = BoundaryFault.Wrap(inner); var fault = (BoundaryFault)proxy;
        fault.Match = (state, _) => effects.Events.Contains("boundary") && state.Continuations[0].PendingActivity == null;
        var id = Guid.NewGuid().ToString("N");
        await using (var first = await open(proxy))
            await Assert.ThrowsAsync<WorkflowEngineException>(() => first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data(), new() { InstanceId = id }));
        await DisposeRepository(inner);
        inner = repository(); proxy = BoundaryFault.Wrap(inner); fault = (BoundaryFault)proxy;
        fault.RetainLease = takeover;
        await using (var second = await open(proxy))
        {
            var client = second.GetRequiredService<IWorkflowClient>();
            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, (await client.RecoverAsync(id)).Status);
            var before = await Read(inner, id);
            var saga = Assert.Single(before.Continuations[0].Stack, f => f.SagaState != null).SagaState!;
            Assert.Equal(2, saga.CompensationFloor); Assert.Equal(2, saga.CompensationCursor); Assert.Equal(3, saga.CompletedSteps.Count);
            fault.Match = (state, instance) => AtBoundary(state, instance, scopes, boundary);
            fault.Lost = lost;
            Task<WorkflowExecutionResult> Continue() => client.ResolveAsync(id, before.Continuations[0].PendingActivity!.Id,
                compensationFailure ? ActivityResolution.Failed(new InvalidOperationException("undo failed")) : ActivityResolution.Completed(new Dictionary<string, object?>()));
            if (compensationFailure)
                await Assert.ThrowsAsync<WorkflowEngineException>(() => Continue());
            else
            {
                if (boundary == "owner") await Assert.ThrowsAsync<WorkflowEngineException>(() => client.CancelAsync(id, new() { ReasonCode = "test" }));
                else
                {
                    Assert.Equal(WorkflowExecutionStatus.NeedsResolution, (await client.CancelAsync(id, new() { ReasonCode = "test" })).Status);
                    AssertOwner(await Read(inner, id), finallyOutside, true);
                    Assert.Equal(0, (await Read(inner, id)).Continuations[0].Stack.Single(f => f.SagaState != null).SagaState!.CompensationFloor);
                    await Assert.ThrowsAsync<WorkflowEngineException>(() => Continue());
                }
            }
            Assert.True(fault.Hit, $"Missed {boundary}");
            AssertRecoveryPosition(fault.Candidate!, finallyOutside, boundary);
            if (lost) Assert.Equal(Signature(fault.Candidate!), Signature(await Read(inner, id)));
        }
        if (takeover) await expire(id);
        await DisposeRepository(inner);
        inner = repository(); var successor = BoundaryFault.Wrap(inner);
        await using (var third = await open(successor))
        {
            var client = third.GetRequiredService<IWorkflowClient>();
            var done = await client.RecoverAsync(id);
            if (done.Status == WorkflowExecutionStatus.NeedsResolution)
            {
                AssertOwner(await Read(inner, id), finallyOutside, true);
                done = await client.ResolveAsync(id, Assert.Single(await client.GetPendingActivitiesAsync(id)).InvocationId,
                    ActivityResolution.Completed(new Dictionary<string, object?>()));
            }
            Assert.Equal(compensationFailure ? WorkflowExecutionStatus.Faulted : WorkflowExecutionStatus.Cancelled, done.Status);
            Assert.Equal(new[] { "boundary", "undo-C-tail", "undo-B", "undo-A", "inner-tail", "outer-tail" }, effects.Events);
            Assert.Equal(1, effects.Seeds);
            var trace = await client.GetTraceAsync(id);
            Assert.DoesNotContain(trace, e => e.EntryType == TraceEntryType.SagaRetryStarted);
            Assert.Equal(3, trace.Count(e => e.EntryType == TraceEntryType.CompensationStarted));
            Assert.Equal(compensationFailure ? 2 : 3, trace.Count(e => e.EntryType == TraceEntryType.CompensationCompleted));
            Assert.Equal(compensationFailure ? 1 : 0, trace.Count(e => e.EntryType == TraceEntryType.CompensationFailed));
            Assert.Equal(compensationFailure ? 2 : 1, trace.Count(e => e.EntryType == TraceEntryType.CancellationHandlerEntered));
            Assert.Equal(compensationFailure ? 2 : 1, trace.Count(e => e.EntryType == TraceEntryType.CancellationHandlerCompleted));
            if (takeover) Assert.True(((BoundaryFault)successor).Grant!.Epoch > fault.Grant!.Epoch);
        }
        await DisposeRepository(inner);
    }

    private static string Signature(ExecutionCheckpoint state) => System.Text.Json.JsonSerializer.Serialize(state.Continuations.Select(c => new
    {
        c.Status, c.CancellationUnwind, Frames = c.Stack.Select(f => new { f.ScopeId, f.NextStepIndex,
            Handler = f.CancellationState?.IsCancellationCleanup, Finally = f.TryState?.IsCancellationCleanup,
            Saga = f.SagaState == null ? null : new { f.SagaState.IsCancellationCleanup, f.SagaState.CancellationRequested, f.SagaState.CompensationFloor, f.SagaState.CompensationCursor } })
    }));

    private static void AssertRecoveryPosition(ExecutionCheckpoint state, bool finallyOutside, string boundary)
    {
        var c = Assert.Single(state.Continuations);
        var owners = c.Stack.Where(f => f.CancellationState?.IsCancellationCleanup == true || f.TryState?.IsCancellationCleanup == true || f.SagaState?.IsCancellationCleanup == true).ToArray();
        if (boundary == "terminal") { Assert.Empty(owners); Assert.Empty(c.Stack); return; }
        var owner = Assert.Single(owners);
        Assert.True(finallyOutside ? owner.TryState?.Phase == TryPhase.Finally : owner.CancellationState?.Phase == CancellationScopePhase.Handler);
        var saga = c.Stack.SingleOrDefault(f => f.SagaState != null)?.SagaState;
        if (boundary.StartsWith("inner-", StringComparison.Ordinal) || boundary.StartsWith("outer-", StringComparison.Ordinal)) Assert.Null(saga);
        else
        {
            Assert.NotNull(saga); Assert.True(saga.CancellationRequested); Assert.False(saga.IsCancellationCleanup);
            Assert.Equal(0, saga.CompensationFloor);
            Assert.Equal(boundary.StartsWith("undo-A", StringComparison.Ordinal) ? 0 : boundary.StartsWith("undo-B", StringComparison.Ordinal) ? 1 : 2, saga.CompensationCursor);
        }
        if (boundary.StartsWith("outer-", StringComparison.Ordinal)) Assert.Single(c.Stack.Where(f => f.CancellationState != null || f.TryState?.Phase == TryPhase.Finally));
    }

    private static async Task DisposeRepository(IWorkflowStateRepository repository)
    { if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync(); }

    public class BoundaryFault : DispatchProxy
    {
        private IWorkflowStateRepository _inner = null!;
        internal Func<ExecutionCheckpoint, WorkflowInstance, bool>? Match { get; set; }
        internal bool Lost { get; set; }
        internal bool Hit { get; private set; }
        internal ExecutionCheckpoint? Candidate { get; private set; }
        internal bool RetainLease { get; set; }
        internal string? InstanceId { get; set; }
        internal ExecutionLeaseGrant? Grant { get; private set; }
        public static IWorkflowStateRepository Wrap(IWorkflowStateRepository inner)
        { var proxy = Create<IWorkflowStateRepository, BoundaryFault>(); ((BoundaryFault)proxy)._inner = inner; return proxy; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IWorkflowStateRepository.CommitWorkflowInstanceAsync)) return Commit((CheckpointCommit)args![0]!);
            if (targetMethod.Name == nameof(IWorkflowStateRepository.ReleaseExecutionLeaseAsync) && RetainLease) return Task.FromResult(new ExecutionLeaseReleaseResult(false));
            var result = targetMethod.Invoke(_inner, args);
            return targetMethod.Name == nameof(IWorkflowStateRepository.TryAcquireExecutionLeaseAsync) ? Acquired((Task<ExecutionLeaseGrant?>)result!) : result;
        }
        private async Task<ExecutionLeaseGrant?> Acquired(Task<ExecutionLeaseGrant?> task)
        { var grant = await task; if (grant != null) Grant = grant; return grant; }
        private async Task<WorkflowCommitResult> Commit(CheckpointCommit commit)
        {
            if (Match == null || InstanceId != null && commit.InstanceId != InstanceId) return await _inner.CommitWorkflowInstanceAsync(commit);
            if (Hit) throw new IOException("Injected interrupted cleanup boundary");
            var instance = commit.ReadInstance(commit.ReplacesParts ? null : await _inner.GetWorkflowInstanceAsync(commit.InstanceId));
            var candidate = await Read(_inner, instance);
            if (Match?.Invoke(candidate, instance) != true) return await _inner.CommitWorkflowInstanceAsync(commit);
            Hit = true; Candidate = candidate;
            if (Lost) await _inner.CommitWorkflowInstanceAsync(commit);
            throw new IOException("Injected interrupted cleanup boundary");
        }
    }

    public static WorkflowDefinition Definition(bool finallyOutside, bool compensation, bool unresolved)
    {
        void Inner(IWorkflowBuilder<Data, object> body)
        {
            if (!unresolved) body.WaitFor<Go>("cleanup");
            if (compensation)
            {
                body.Saga(s => s.Step<Seed>(a => a
                        .CompensateWith<Boundary>()
                        .CompensateWith<Mark>(a => a.Input(x => x.Text).From(c => "undo-tail")))
                    .Throw<InvalidOperationException>(c => new("compensate")))
                    .OnError<InvalidOperationException>(e =>
                    {
                        // Cancellation of an already active compensation must suppress its retry,
                        // even when a containing handler/Finally owns the cancellation walk.
                        if (unresolved) e.Compensate().ThenRetry(1);
                        else e.Compensate().ThenContinue();
                    });
            }
            else body.Step<Boundary>();
            body.Step<Mark>(a => a.Input(x => x.Text).From(c => "inner-tail"));
        }

        var builder = new WorkflowBuilder<Data, object>([], "CancellationMixedCleanup");
        return finallyOutside
            ? builder.Try(b => { }).Finally(f => f.Try(t => t.CancellationScope(
                    b => b.Throw<InvalidOperationException>(c => new("business")), h => Inner(h)))
                .Catch<InvalidOperationException>(c => { })
                .Step<Mark>(a => a.Input(x => x.Text).From(c => "outer-tail"))).Build()
            : builder.Try(t => t.CancellationScope(b => b.Throw<InvalidOperationException>(c => new("business")),
                    h => h.Try(b => { }).Finally(f => Inner(f))
                        .Step<Mark>(a => a.Input(x => x.Text).From(c => "outer-tail"))))
                .Catch<InvalidOperationException>(c => { }).Build();
    }

    internal static async Task<ExecutionCheckpoint> Read(IWorkflowStateRepository repository, string id) =>
        await Read(repository, (await repository.GetWorkflowInstanceAsync(id))!);

    internal static async Task<ExecutionCheckpoint> Read(IWorkflowStateRepository repository, WorkflowInstance instance)
    {
        var reader = new PayloadReader(async reference => await repository.PayloadStore.GetAsync(reference)
            ?? throw new InvalidDataException("Missing checkpoint payload"), new PayloadBudget(100_000_000));
        return ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(instance, reader));
    }

    internal static void AssertOwner(ExecutionCheckpoint state, bool finallyOutside, bool activeCompensation)
    {
        var continuation = Assert.Single(state.Continuations);
        Assert.True(continuation.CancellationUnwind);
        var handler = Assert.Single(continuation.Stack, f => f.CancellationState?.Phase == CancellationScopePhase.Handler).CancellationState!;
        var finallyState = Assert.Single(continuation.Stack, f => f.TryState?.Phase == TryPhase.Finally).TryState!;
        Assert.Equal(!finallyOutside, handler.IsCancellationCleanup);
        Assert.Equal(finallyOutside, finallyState.IsCancellationCleanup);
        if (activeCompensation)
        {
            var saga = Assert.Single(continuation.Stack, f => f.SagaState?.Phase == SagaPhase.Compensating).SagaState!;
            Assert.False(saga.IsCancellationCleanup);
            Assert.True(saga.CancellationRequested);
        }
    }

    public static void AssertEffects(Effects effects, bool compensation)
    {
        Assert.Equal(compensation ? ["boundary", "undo-tail", "inner-tail", "outer-tail"]
            : new[] { "boundary", "inner-tail", "outer-tail" }, effects.Events);
        Assert.Equal(compensation ? 1 : 0, effects.Seeds);
    }

    public sealed class Data;
    public sealed class Go;
    public sealed class Effects
    {
        public ConcurrentQueue<string> Events { get; } = new();
        public ConcurrentDictionary<string, bool> Invocations { get; } = new();
        public bool FailNested;
        public int Seeds;
        public int Attempts;
    }
    public sealed class NestedFirst(Effects effects) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            effects.Events.Enqueue("nested-first");
            return effects.FailNested ? Task.FromException(new ArgumentException("nested failure")) : Task.CompletedTask;
        }
    }
    public sealed class Seed(Effects effects) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref effects.Seeds); return Task.CompletedTask; }
    }
    public sealed class SeedB : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    public sealed class SeedC : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    public sealed class FailForward(Effects effects) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref effects.Attempts) == 1 ? Task.FromException(new InvalidOperationException("retry")) : Task.CompletedTask;
    }
    public sealed class RetryWork(Effects effects) : IRequestActivity<Go>
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) { effects.Events.Enqueue("retry-work"); return Task.CompletedTask; } }
    public class RecoveryMark(Effects effects) : IRecoverableActivity<string>
    {
        private string Label() => GetType().Name switch { nameof(UndoA) => "undo-A", nameof(UndoB) => "undo-B", nameof(UndoCTail) => "undo-C-tail", nameof(InnerTail) => "inner-tail", _ => "outer-tail" };
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Assert.True(effects.Invocations.TryAdd(context.InvocationId, true)); effects.Events.Enqueue(Label()); return Task.CompletedTask; }
        public string CaptureRecoveryState(IActivityContext context) => context.InvocationId;
        public Task<ActivityRecoveryResult<string>> RecoverAsync(string state, IActivityContext context, CancellationToken cancellationToken = default)
        { Assert.Equal(state, context.InvocationId); return Task.FromResult<ActivityRecoveryResult<string>>(effects.Invocations.ContainsKey(state)
                ? new ActivityRecoveryResult<string>.Completed() : new ActivityRecoveryResult<string>.Execute()); }
    }
    public sealed class UndoA(Effects effects) : RecoveryMark(effects) { }
    public sealed class UndoB(Effects effects) : RecoveryMark(effects) { }
    public sealed class UndoCTail(Effects effects) : RecoveryMark(effects) { }
    public sealed class InnerTail(Effects effects) : RecoveryMark(effects) { }
    public sealed class OuterTail(Effects effects) : RecoveryMark(effects) { }
    public sealed class Boundary(Effects effects) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { effects.Events.Enqueue("boundary"); return Task.CompletedTask; }
    }
    public sealed class Mark(Effects effects) : IAsyncActivity
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { effects.Events.Enqueue(Text); return Task.CompletedTask; }
    }
}

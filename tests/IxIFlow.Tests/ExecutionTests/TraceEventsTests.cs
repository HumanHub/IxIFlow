using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Data.Sqlite;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "tracing")]
[Trait("Spec", "G-01")]
public sealed class TraceEventsTests : IClassFixture<SqliteTestDatabase<TraceEventsTests>>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EveryControlFlowKindCarriesOnlyIdentifiersAndIndices()
    {
        var time = new FakeTimeProvider(Start);
        var store = new TraceStore(time);
        using var services = Services(store, time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("ControlTrace")
            .Step<Noop>()
            .Sequence(sequence => sequence.If(ctx => true, then => then.Step<Noop>()))
            .WhileDo(ctx => ctx.WorkflowData.Count < 2, body => body.Step<Increment>(step => step
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Count)
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Count)))
            .DoWhile(body => body.If(ctx => false, then => then.Step<Noop>(), otherwise => otherwise.Step<Noop>()),
                ctx => false)
            .Parallel(parallel => parallel.Do(branch => branch.Step<Noop>()).Do(branch => branch.Step<Noop>()))
            .Try(body => body.Saga(saga => saga
                .Step<Noop>(step => step.CompensateWith<Noop>())
                .Step<Fail>()))
            .Catch<InvalidOperationException, MessageFault>(body => body.Step<Noop>())
            .Finally(body => body.Step<Noop>())
            .ForEach(ctx => ctx.WorkflowData.Items, (body, item) => body.Result(ctx => ctx.Item(item)),
                setup => setup.Into(ctx => ctx.WorkflowData.Results))
            .ForEach(ctx => ctx.WorkflowData.Items, (body, item) => body.Result(ctx => ctx.Item(item)),
                setup => setup.Parallel(1).Into(ctx => ctx.WorkflowData.ParallelResults))
            .Build();

        var result = await client.RunAsync(definition, new Data());

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var trace = await client.GetTraceAsync(result.InstanceId);
        var control = trace.Where(entry => TraceBuffer.IsControlFlow(entry.EntryType)).ToArray();
        Assert.All(control, entry =>
        {
            Assert.Equal(Start.UtcDateTime, entry.Timestamp);
            if (entry.Payload is ReplayOriginTrace origin)
            {
                Assert.Equal(result.InstanceId, origin.InstanceId);
                Assert.Equal(new WorkflowScopeCatalog(definition).StructureHash, origin.StructureHash);
                Assert.Equal(TraceDetail.Full, origin.Detail);
                Assert.NotEmpty(origin.Threads);
            }
            else if (entry.Payload is ThreadPositionTrace position)
            {
                Assert.False(string.IsNullOrEmpty(position.ContinuationId));
                Assert.Equal(entry.ContinuationId, position.ContinuationId);
                if (position.Thread is { } thread)
                {
                    Assert.Equal(position.ContinuationId, thread.ContinuationId);
                    Assert.All(thread.Frames, frame =>
                    {
                        Assert.False(string.IsNullOrEmpty(frame.ActivationId));
                        Assert.False(string.IsNullOrEmpty(frame.ScopeId));
                    });
                }
            }
            else
            {
                var payload = JsonSerializer.SerializeToElement(entry.Payload, typeof(TracePayload));
                Assert.Equal(new WorkflowScopeCatalog(definition).Step(payload.GetProperty("StepId").GetString()!).StepType,
                    (WorkflowStepType)payload.GetProperty("StepKind").GetInt32());
                Assert.False(string.IsNullOrEmpty(payload.GetProperty("ContinuationId").GetString()));
            }
            Assert.Empty(entry.Metadata);
            Assert.DoesNotContain("secret", JsonSerializer.Serialize(entry));
        });
        foreach (var kind in new[] { TraceEntryType.StepEntered, TraceEntryType.ConditionTaken,
                     TraceEntryType.LoopIteration, TraceEntryType.LoopCompleted, TraceEntryType.BranchStarted,
                     TraceEntryType.JoinCompleted, TraceEntryType.CatchEntered, TraceEntryType.FinallyEntered,
                     TraceEntryType.CompensationStarted, TraceEntryType.CompensationCompleted,
                     TraceEntryType.ForEachItemStarted, TraceEntryType.ForEachItemEnded })
            Assert.Contains(control, entry => entry.EntryType == kind);
        Assert.Equal([1, 2, 1], control.Where(entry => entry.EntryType == TraceEntryType.LoopIteration)
            .Select(entry => ((ControlFlowTrace)entry.Payload!).Index));
        Assert.Equal([2, 1], control.Where(entry => entry.EntryType == TraceEntryType.LoopCompleted)
            .Select(entry => ((ControlFlowTrace)entry.Payload!).Index));
        Assert.Equal([ConditionPath.Then, ConditionPath.Else], control.Where(entry => entry.EntryType == TraceEntryType.ConditionTaken)
            .Select(entry => ((ConditionTrace)entry.Payload!).Path));
        Assert.Equal([0, 1, 0, 1], control.Where(entry => entry.EntryType == TraceEntryType.ForEachItemStarted)
            .Select(entry => ((ControlFlowTrace)entry.Payload!).Index));
        Assert.Equal([0, 1, 0, 1], control.Where(entry => entry.EntryType == TraceEntryType.ForEachItemEnded)
            .Select(entry => Assert.IsType<ItemEndTrace>(entry.Payload).Index));
        Assert.All(control.Where(entry => entry.EntryType == TraceEntryType.ForEachItemEnded),
            entry => Assert.Equal(ItemEndCause.Completed, Assert.IsType<ItemEndTrace>(entry.Payload).Cause));
        Assert.Equal(2, control.Count(entry => entry.EntryType == TraceEntryType.JoinCompleted));
        var compensation = Array.FindIndex(control, entry => entry.EntryType == TraceEntryType.CompensationStarted);
        var compensated = Array.FindIndex(control, entry => entry.EntryType == TraceEntryType.CompensationCompleted);
        var caught = Array.FindIndex(control, entry => entry.EntryType == TraceEntryType.CatchEntered);
        var final = Array.FindIndex(control, entry => entry.EntryType == TraceEntryType.FinallyEntered);
        Assert.True(compensation < compensated && compensated < caught && caught < final);
        Assert.Equal(trace.Select(entry => entry.TraceId), result.TraceEntries.Select(entry => entry.TraceId));
        Assert.DoesNotContain("ControlFlow", JsonSerializer.Serialize(await store.GetWorkflowInstanceAsync(result.InstanceId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboxDecisionsAreTracedOnceAndRoundTripThroughTheStore(bool sqlite)
    {
        var time = new FakeTimeProvider(Start);
        await using var relational = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<TraceEventsTests>.ConnectionString, time) : null;
        IWorkflowStateRepository store = relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository(time);
        using var services = Services(store, time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("InboxTrace" + sqlite).Step<Noop>().WaitFor<Go>("go")
            .Delay(TimeSpan.FromMinutes(5)).Build();
        var started = await client.RunAsync(definition, new Data());
        var wait = Assert.Single(await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));
        EventAssert.Rejected(await client.ResumeAsync(started.InstanceId, "other", new Go(), "wrong-key"),
            EventRejection.NoOpenWait);
        EventAssert.Rejected(await client.ResumeAsync(started.InstanceId, "go", new Wrong(), "wrong-type"),
            EventRejection.NotMatched);
        EventAssert.Rejected(await client.ResumeAsync(started.InstanceId, "other", new Go(), "wrong-key"),
            EventRejection.NoOpenWait);
        var received = await client.ResumeAsync(started.InstanceId, "go", new Go { Secret = "secret" }, "accepted");
        EventAssert.Consumed(received);
        EventAssert.Consumed(await client.ResumeAsync(started.InstanceId, "go", new Go { Secret = "secret" }, "accepted"));
        time.Advance(TimeSpan.FromMinutes(6));
        // The clock also fires the host timer, so this recovery can arrive after completion.
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.RecoverAsync(started.InstanceId, patience.Token);
        var completed = await client.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished, patience.Token);
        Assert.True(completed.Status == WorkflowExecutionStatus.Success, $"{completed.Status}: {completed.ErrorMessage}");

        var trace = await client.GetTraceAsync(started.InstanceId);
        Assert.Equal([TraceEntryType.ReplayOrigin, TraceEntryType.ActivityStarted, TraceEntryType.ThreadPosition,
            TraceEntryType.ActivityCompleted, TraceEntryType.StepEntered, TraceEntryType.ThreadPosition,
            TraceEntryType.EventRefused, TraceEntryType.EventRefused, TraceEntryType.ThreadPosition,
            TraceEntryType.EventReceived, TraceEntryType.ThreadPosition, TraceEntryType.StepEntered,
            TraceEntryType.ThreadPosition, TraceEntryType.ThreadPosition, TraceEntryType.DelayFired,
            TraceEntryType.ThreadPosition, TraceEntryType.ThreadPosition], trace.Select(entry => entry.EntryType));
        var decisions = trace.Where(entry => entry.Payload is ReceivedEventTrace).ToArray();
        Assert.Equal(["wrong-key", "wrong-type", "accepted"], decisions.Select(entry => ((ReceivedEventTrace)entry.Payload!)!.EventId));
        Assert.Equal([EventRejection.NoOpenWait, EventRejection.NotMatched, null],
            decisions.Select(entry => ((ReceivedEventTrace)entry.Payload!)!.RefusalReason));
        var taken = decisions[^1];
        Assert.Equal("go", ((ReceivedEventTrace)taken.Payload!)!.Key);
        Assert.Equal(typeof(Go).AssemblyQualifiedName, ((ReceivedEventTrace)taken.Payload!).EventType);
        Assert.Equal("root/1", ((ReceivedEventTrace)taken.Payload!).StepId);
        Assert.Equal(WaitReference.Of(started.InstanceId, wait.WaitId), ((ReceivedEventTrace)taken.Payload!).WaitRef);
        Assert.All(decisions, entry => Assert.Equal(Start.UtcDateTime, entry.Timestamp));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(decisions));
        Assert.Equal([4L, 5], (await store.GetTraceAsync(started.InstanceId, 3, 2)).Select(entry => entry.Sequence));
        Assert.Equal(Enumerable.Range(1, trace.Count).Select(number => (long)number),
            (await store.GetTraceAsync(started.InstanceId)).Select(entry => entry.Sequence));
        var saved = JsonSerializer.Serialize(await store.GetWorkflowInstanceAsync(started.InstanceId));
        Assert.DoesNotContain("ControlFlow", saved);
        Assert.DoesNotContain("RefusalReason", saved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Spec", "G-01")]
    public async Task TraceJsonNeverContainsAnyInstanceWaitId(bool sqlite)
    {
        var time = new FakeTimeProvider(Start);
        await using var relational = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<TraceEventsTests>.ConnectionString, time) : null;
        IWorkflowStateRepository store = relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository(time);
        using var services = Services(store, time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("WaitReferenceTrace" + sqlite).Step<Noop>()
            .WaitFor<Go>("taken", (reply, ctx) => reply.Index == 1)
            .Sequence(sequence => sequence.WaitFor<Go>("timeout", configure: wait =>
                wait.TimeoutAfter(TimeSpan.FromMinutes(1)).OnTimeout(body => body.Step<Noop>())))
            .Delay(TimeSpan.FromMinutes(1)).Build();
        var started = await client.RunAsync(definition, new Data());
        var takenWait = Assert.Single(await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));

        // This refusal is decided by the executor, after the store accepts the targeted reply.
        var refused = await client.ReplyAsync(takenWait.Token, new Go(), "refused");
        EventAssert.Rejected(refused, EventRejection.NotMatched);
        var taken = await client.ReplyAsync(takenWait.Token, new Go { Index = 1 }, "taken");
        EventAssert.Consumed(taken);
        var timeoutWait = Assert.Single(await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));
        // A closed target is refused by the store, without an executor pass.
        var closed = await client.ReplyAsync(takenWait.Token, new Go { Index = 1 }, "closed");
        EventAssert.Rejected(closed, EventRejection.WaitAnswered);
        var waitingSnapshot = await client.GetSnapshotAsync(started.InstanceId);

        time.Advance(TimeSpan.FromMinutes(2));
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timedOut = await client.RecoverAsync(started.InstanceId, patience.Token);
        await client.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Idle, patience.Token);
        // Delays are not discoverable reply waits; their IDs are only in the internal checkpoint.
        var delayed = (await store.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.True(delayed.Status == WorkflowStatus.Suspended,
            $"{delayed.Status}; recovery: {timedOut.Status}: {timedOut.ErrorMessage}");
        var delayWait = Assert.Single(ExecutionCheckpoint.Read(delayed.ExecutionStateJson).Waits);
        Assert.Equal(WaitKind.Delay, delayWait.TableKind);
        var expired = await client.ReplyAsync(timeoutWait.Token, new Go(), "expired");
        EventAssert.Rejected(expired, EventRejection.WaitTimedOut);

        time.Advance(TimeSpan.FromMinutes(2));
        var recovered = await client.RecoverAsync(started.InstanceId, patience.Token);
        var finished = await client.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished, patience.Token);
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        var trace = await client.GetTraceAsync(started.InstanceId);
        var takenRef = WaitReference.Of(started.InstanceId, takenWait.WaitId);
        var timeoutRef = WaitReference.Of(started.InstanceId, timeoutWait.WaitId);
        Assert.Equal(takenRef, Assert.IsType<ReceivedEventTrace>(Assert.Single(trace,
            entry => entry.EntryType == TraceEntryType.EventReceived).Payload).WaitRef);
        var refusals = trace.Where(entry => entry.EntryType == TraceEntryType.EventRefused).ToArray();
        Assert.Equal(3, refusals.Length);
        Assert.Equal(["refused", "closed", "expired"],
            refusals.Select(entry => Assert.IsType<ReceivedEventTrace>(entry.Payload).EventId));
        Assert.Equal([takenRef, takenRef, timeoutRef],
            refusals.Select(entry => Assert.IsType<ReceivedEventTrace>(entry.Payload).WaitRef));
        Assert.Equal([TraceClock.Host, TraceClock.Store, TraceClock.Store], refusals.Select(entry => entry.Clock));
        Assert.Equal(timeoutRef, Assert.IsType<TimeoutTrace>(Assert.Single(trace,
            entry => entry.EntryType == TraceEntryType.WaitTimedOut).Payload).WaitRef);
        Assert.Equal(WaitReference.Of(started.InstanceId, delayWait.Id),
            Assert.IsType<TimeoutTrace>(Assert.Single(trace,
                entry => entry.EntryType == TraceEntryType.DelayFired).Payload).WaitRef);

        var stored = await store.GetTraceAsync(started.InstanceId);
        var serializationStore = new InMemoryWorkflowStateRepository();
        var commit = CheckpointCommit.Create(new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = started.InstanceId },
            (await StoreTestSupport.LeaseAsync(serializationStore, started.InstanceId)).Epoch, 0,
            traceEntries: trace);
        var results = new[] { started, refused, taken, closed, timedOut, expired, recovered, finished };
        var copies = trace.Concat(stored.Select(entry => entry.Entry)).Concat(commit.ReadTraceEntries())
            .Concat(results.SelectMany(result => result.TraceEntries)).ToArray();
        Assert.Equal(trace.Count, stored.Count);
        var jsonCopies = copies.Select(entry => JsonSerializer.Serialize(entry)).Concat(commit.TraceEntries)
            .Append(JsonSerializer.Serialize(waitingSnapshot))
            .Append(JsonSerializer.Serialize(await client.GetSnapshotAsync(started.InstanceId))).ToArray();
        foreach (var waitId in new[] { takenWait.WaitId, timeoutWait.WaitId, delayWait.Id })
            Assert.All(jsonCopies, json => Assert.DoesNotContain(waitId, json));
        Assert.All(copies, entry =>
        {
            var json = JsonSerializer.SerializeToElement(entry);
            Assert.False(json.TryGetProperty("WaitId", out _));
            if (json.TryGetProperty("Payload", out var payload))
                Assert.False(payload.TryGetProperty("WaitId", out _));
            Assert.False(entry.Metadata.ContainsKey("WaitId"));
            Assert.False(entry.Metadata.ContainsKey("TargetWaitId"));
        });
    }

    [Theory]
    [InlineData(true, null, "service")]
    [InlineData(false, "joe@ops", "joe@ops")]
    public async Task ResolutionEntriesRecordTheDecisionAndActorWithoutDecisionContents(
        bool completed, string? actor, string expectedActor)
    {
        var time = new FakeTimeProvider(Start);
        var store = new TraceStore(time) { FailCompletions = true };
        using var services = Services(store, time, actor: "service");
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("ResolutionTrace").Step<Noop>().Build();
        await EngineAssert.StoreFailureAsync(() => client.RunAsync(definition, new Data()));
        store.FailCompletions = false;
        var saved = Assert.Single(await store.GetWorkflowInstancesByNameAsync(definition.Name));
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, (await client.RecoverAsync(saved.InstanceId)).Status);
        var pending = Assert.Single(await client.GetPendingActivitiesAsync(saved.InstanceId));
        time.Advance(TimeSpan.FromMinutes(2));

        var result = await client.ResolveAsync(saved.InstanceId, pending.InvocationId, completed
            ? ActivityResolution.Completed(new Dictionary<string, object?>(), "secret note", actor)
            : ActivityResolution.Failed("secret error", actor));

        Assert.Equal(completed ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Faulted, result.Status);
        var entry = Assert.Single(await client.GetTraceAsync(saved.InstanceId),
            entry => entry.EntryType == TraceEntryType.ActivityResolved);
        Assert.Equal(new ActivityResolutionTrace(pending.InvocationId, pending.StepId,
            completed ? ActivityResolutionKind.Completed : ActivityResolutionKind.Failed, expectedActor), entry.Payload);
        Assert.Equal(time.GetUtcNow().UtcDateTime, entry.Timestamp);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(entry));
    }

    [Fact]
    public async Task CancellationRecordsRequestAndCompletionTimesAndUnwindsItemsThroughFinally()
    {
        var time = new FakeTimeProvider(Start);
        using var services = Services(new InMemoryWorkflowStateRepository(time), time, actor: "service");
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("CancelTrace").Step<Noop>()
            .Try(body => body.ForEach(ctx => ctx.WorkflowData.Items, (items, item) => items.WaitFor<Go>("go")))
            .Finally(body => body.Step<AdvanceClock>()).Build();
        var started = await client.RunAsync(definition, new Data());
        time.Advance(TimeSpan.FromMinutes(1));
        var requestedAt = time.GetUtcNow().UtcDateTime;

        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await client.CancelAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "withdrawn", Description = "secret", RequestedBy = "joe@ops" })).Status);

        var trace = await client.GetTraceAsync(started.InstanceId);
        var request = Assert.Single(trace, entry => entry.EntryType == TraceEntryType.CancellationRequested);
        var completed = Assert.Single(trace, entry => entry.EntryType == TraceEntryType.WorkflowCancelled);
        Assert.Equal(new CancellationTrace("joe@ops", requestedAt, "withdrawn"), request.Payload);
        Assert.Equal(request.Payload, completed.Payload);
        Assert.Equal(requestedAt, request.Timestamp);
        Assert.Equal(requestedAt.AddSeconds(1), completed.Timestamp);
        Assert.Equal("joe@ops", Assert.IsType<CancellationTrace>(completed.Payload).Actor);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(request));
        var ended = Assert.Single(trace, entry => entry.EntryType == TraceEntryType.ForEachItemEnded);
        Assert.Equal(0, Assert.IsType<ItemEndTrace>(ended.Payload).Index);
        Assert.Equal(ItemEndCause.Cancelled, Assert.IsType<ItemEndTrace>(ended.Payload).Cause);
        Assert.Single(trace, entry => entry.EntryType == TraceEntryType.FinallyEntered);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelWithParentTracesTheChildCommandAndPreservesTheActor(bool cancelWithParent)
    {
        var time = new FakeTimeProvider(Start);
        var store = new InMemoryWorkflowStateRepository(time);
        using var services = Services(store, time);
        var client = services.GetRequiredService<IWorkflowClient>();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<Data>("TraceChild").Step<Noop>().WaitFor<Go>("go").Build());
        var definition = Workflow.Create<Data>("TraceParent").Step<Noop>()
            .Invoke<Data>("TraceChild", 1, step => step.CancelWithParent(cancelWithParent)).Build();
        var started = await client.RunAsync(definition, new Data());
        var child = Assert.Single(await store.GetWorkflowInstancesByNameAsync("TraceChild"));
        var parentWaitId = child.ParentWaitId!;
        await client.AwaitAsync(child.InstanceId, WorkflowAwaitCondition.Idle).WaitAsync(TimeSpan.FromSeconds(10));
        var requestedAt = time.GetUtcNow().UtcDateTime;
        time.Advance(TimeSpan.FromMinutes(1));

        await client.CancelAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "withdrawn", RequestedAt = requestedAt, RequestedBy = "joe@ops" });
        var finished = await client.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, finished.Status);
        var trace = await client.GetTraceAsync(started.InstanceId);
        if (cancelWithParent)
        {
            var command = Assert.Single(trace, entry => entry.EntryType == TraceEntryType.ChildCancellationRequested);
            Assert.Equal(child.InstanceId, ((CancellationTrace)command.Payload!).ChildInstanceId);
            Assert.Equal(WaitReference.Of(started.InstanceId, parentWaitId), ((CancellationTrace)command.Payload!).WaitRef);
            Assert.DoesNotContain(parentWaitId, JsonSerializer.Serialize(trace));
            Assert.Equal("joe@ops", ((CancellationTrace)command.Payload!).Actor);
            Assert.Equal(requestedAt, ((CancellationTrace)command.Payload!).RequestedAt);
            Assert.Equal(requestedAt.AddMinutes(1), command.Timestamp);
            var childFinished = await client.AwaitAsync(child.InstanceId, WorkflowAwaitCondition.Finished)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WorkflowExecutionStatus.Cancelled, childFinished.Status);
            var childTrace = await client.GetTraceAsync(child.InstanceId);
            var childRequest = Assert.Single(childTrace, entry => entry.EntryType == TraceEntryType.CancellationRequested);
            Assert.Equal("joe@ops", ((CancellationTrace)childRequest.Payload!).Actor);
            Assert.Equal("parent_branch_cancelled", ((CancellationTrace)childRequest.Payload!).ReasonCode);
            Assert.Equal(requestedAt, ((CancellationTrace)childRequest.Payload!).RequestedAt);
            Assert.Single(childTrace, entry => entry.EntryType == TraceEntryType.WorkflowCancelled);
        }
        else
        {
            Assert.DoesNotContain(trace, entry => entry.EntryType == TraceEntryType.ChildCancellationRequested);
            Assert.DoesNotContain(await client.GetTraceAsync(child.InstanceId),
                entry => entry.EntryType is TraceEntryType.CancellationRequested or TraceEntryType.WorkflowCancelled);
            EventAssert.Consumed(await client.ResumeAsync(child.InstanceId, "go", new Go()));
        }
    }

    [Fact]
    public async Task TruncationBoundsPureTransitionsAndCountsEveryDroppedEntry()
    {
        var time = new FakeTimeProvider(Start);
        var store = new TraceStore(time);
        using var services = Services(store, time, limit: 5);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("TruncatedTrace").Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Items, (body, item) => body.If(ctx => true, then => { })).Build();

        var result = await client.RunAsync(definition, new Data { Items = Enumerable.Range(0, 20).ToList() });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var trace = await client.GetTraceAsync(result.InstanceId);
        Assert.Equal([TraceEntryType.ReplayOrigin, TraceEntryType.ActivityStarted, TraceEntryType.ThreadPosition,
            TraceEntryType.ActivityCompleted, TraceEntryType.StepEntered, TraceEntryType.ThreadPosition,
            TraceEntryType.ForEachItemStarted, TraceEntryType.ConditionTaken, TraceEntryType.Truncated], trace.Select(entry => entry.EntryType));
        var marker = Assert.Single(trace, entry => entry.EntryType == TraceEntryType.Truncated);
        Assert.Equal(60, Assert.IsType<TraceTruncation>(marker.Payload).DroppedCount); // Includes attempted position changes; the per-commit bound stays 5.
        Assert.Equal(Start.UtcDateTime, marker.Timestamp);
        Assert.All(store.Commits, commit => Assert.InRange(commit.TraceEntries.Count, 0, 5));
        Assert.Equal(Enumerable.Range(1, 9).Select(number => (long)number),
            (await store.GetTraceAsync(result.InstanceId)).Select(entry => entry.Sequence));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void TheBoundResetsOnlyOnAcknowledgementAndDiscardDropsItsMarker(int limit)
    {
        var time = new FakeTimeProvider(Start);
        var trace = new TraceBuffer();
        trace.Configure(limit);
        for (var index = 0; index < 10_000; index++)
            trace.Add(new ExecutionTraceEntry { EntryType = TraceEntryType.LoopIteration,
                Timestamp = time.GetUtcNow().UtcDateTime,
                Payload = new ControlFlowTrace("root/1", WorkflowStepType.Loop, "root", index + 1) });
        Assert.Equal(limit, trace.Pending.Count);
        Assert.Equal(10_000 - limit + 1, ((TraceTruncation)trace.Pending[^1].Payload!).DroppedCount);
        Assert.Single(trace.Pending, entry => entry.EntryType == TraceEntryType.Truncated);
        Assert.Equal(Enumerable.Range(1, limit - 1).Select(number => (int?)number),
            trace.Pending.Take(limit - 1).Select(entry => ((ControlFlowTrace)entry.Payload!).Index));
        trace.DiscardPending();
        Assert.Empty(trace.Pending);
        trace.Add(new ExecutionTraceEntry { EntryType = TraceEntryType.LoopCompleted });
        trace.Committed();
        Assert.Empty(trace.Pending);
        trace.Add(new ExecutionTraceEntry { EntryType = TraceEntryType.ConditionTaken });
        Assert.Equal(TraceEntryType.ConditionTaken, Assert.Single(trace.Pending).EntryType);
        Assert.Single(trace.Pending);
    }

    [Fact]
    public async Task AnUncommittedControlFlowPassLeavesNoEntriesAndRecoveryRecordsItOnce()
    {
        var time = new FakeTimeProvider(Start);
        var store = new TraceStore(time) { FailControlStarts = true };
        using var services = Services(store, time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("LostControlTrace").Step<Noop>()
            .If(ctx => true, then => then.Step<Noop>()).Build();
        await EngineAssert.StoreFailureAsync(() => client.RunAsync(definition, new Data()));
        var saved = Assert.Single(await store.GetWorkflowInstancesByNameAsync(definition.Name));
        Assert.Equal([TraceEntryType.ReplayOrigin, TraceEntryType.ActivityStarted, TraceEntryType.ThreadPosition,
            TraceEntryType.ActivityCompleted],
            (await client.GetTraceAsync(saved.InstanceId)).Select(entry => entry.EntryType));
        store.FailControlStarts = false;
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.RecoverAsync(saved.InstanceId)).Status);
        Assert.Equal([TraceEntryType.ReplayOrigin, TraceEntryType.ActivityStarted, TraceEntryType.ThreadPosition,
            TraceEntryType.ActivityCompleted, TraceEntryType.ConditionTaken, TraceEntryType.ThreadPosition,
            TraceEntryType.ActivityStarted, TraceEntryType.ThreadPosition, TraceEntryType.ActivityCompleted,
            TraceEntryType.ThreadPosition, TraceEntryType.ThreadPosition],
            (await client.GetTraceAsync(saved.InstanceId)).Select(entry => entry.EntryType));
        var boundary = Assert.Single(store.Commits, commit => commit.ReadTraceEntries()
            .Any(entry => entry.EntryType == TraceEntryType.ConditionTaken));
        Assert.Equal([TraceEntryType.ConditionTaken, TraceEntryType.ThreadPosition, TraceEntryType.ActivityStarted],
            boundary.ReadTraceEntries().Select(entry => entry.EntryType));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumedForEachItemsKeepTheirEndsWithoutRepeatingTheirStarts(bool parallel)
    {
        var time = new FakeTimeProvider(Start);
        using var services = Services(new InMemoryWorkflowStateRepository(time), time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("ResumedItemTrace" + parallel).Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Items, (body, item) => body
                .WaitFor<Go>("go", (reply, ctx) => reply.Index == ctx.Index(item))
                .Result(ctx => ctx.Item(item)), setup =>
                {
                    setup.Into(ctx => ctx.WorkflowData.Results);
                    if (parallel)
                        setup.Parallel(1);
                }).Build();
        var started = await client.RunAsync(definition, new Data());
        time.Advance(TimeSpan.FromMinutes(1));
        EventAssert.Consumed(await client.ResumeAsync(started.InstanceId, "go", new Go { Index = 0 }));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await client.ResumeAsync(started.InstanceId, "go", new Go { Index = 1 })).Status);
        var items = (await client.GetTraceAsync(started.InstanceId)).Where(entry =>
            entry.EntryType is TraceEntryType.ForEachItemStarted or TraceEntryType.ForEachItemEnded).ToArray();
        Assert.Equal([TraceEntryType.ForEachItemStarted, TraceEntryType.ForEachItemEnded,
            TraceEntryType.ForEachItemStarted, TraceEntryType.ForEachItemEnded], items.Select(entry => entry.EntryType));
        Assert.Equal([0, 0, 1, 1], items.Select(entry => entry.Payload is ItemEndTrace end ? end.Index : ((ControlFlowTrace)entry.Payload!).Index));
        Assert.Equal([Start.UtcDateTime, Start.AddMinutes(1).UtcDateTime,
            Start.AddMinutes(1).UtcDateTime, Start.AddMinutes(2).UtcDateTime], items.Select(entry => entry.Timestamp));
        Assert.All(items.Where(entry => entry.EntryType == TraceEntryType.ForEachItemEnded),
            entry => Assert.Equal(ItemEndCause.Completed, Assert.IsType<ItemEndTrace>(entry.Payload).Cause));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TracingDisabledAlsoSuppressesArrivalRefusalsAndCancellation(bool sqlite)
    {
        var time = new FakeTimeProvider(Start);
        await using var relational = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<TraceEventsTests>.ConnectionString, time) : null;
        IWorkflowStateRepository store = relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository(time);
        using var services = Services(store, time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("DisabledTrace" + sqlite).Step<Noop>()
            .If(ctx => true, then => then.WaitFor<Go>("go")).Build();
        var started = await client.RunAsync(definition, new Data(), new WorkflowOptions { EnableTracing = false });
        EventAssert.Rejected(await client.ResumeAsync(started.InstanceId, "wrong", new Go()), EventRejection.NoOpenWait);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await client.CancelAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "withdrawn", RequestedBy = "joe@ops" })).Status);
        Assert.Empty(await store.GetTraceAsync(started.InstanceId));
        Assert.Empty(started.TraceEntries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFinishedInstanceTracesItsRefusalWithTheReceiptReason(bool sqlite)
    {
        var time = new FakeTimeProvider(Start);
        await using var relational = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<TraceEventsTests>.ConnectionString, time) : null;
        IWorkflowStateRepository store = relational ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository(time);
        using var services = Services(store, time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var finished = await client.RunAsync(Workflow.Create<Data>("FinishedTrace" + sqlite).Step<Noop>().Build(), new Data());
        time.Advance(TimeSpan.FromMinutes(1));
        var refused = await client.ResumeAsync(finished.InstanceId, "late", new Go { Secret = "secret" }, "late-event");
        Assert.Equal(EventOutcome.Closed, refused.Event!.Outcome);
        Assert.Equal(EventRejection.InstanceFinished, refused.Event.Rejection);
        var entry = Assert.Single(await client.GetTraceAsync(finished.InstanceId),
            entry => entry.EntryType == TraceEntryType.EventRefused);
        Assert.Equal(new ReceivedEventTrace("late-event", "late", typeof(Go).AssemblyQualifiedName!, null, null,
            EventRejection.InstanceFinished), ((ReceivedEventTrace)entry.Payload!));
        Assert.Equal(time.GetUtcNow().UtcDateTime, entry.Timestamp);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(entry));
    }

    [Fact]
    public async Task TheExactBoundFitsAndACommitFreezesItsTruncationMarker()
    {
        var time = new FakeTimeProvider(Start);
        var trace = new TraceBuffer();
        trace.Configure(2);
        trace.Add(new ExecutionTraceEntry { Timestamp = Start.UtcDateTime, EntryType = TraceEntryType.LoopIteration });
        trace.Add(new ExecutionTraceEntry { Timestamp = Start.UtcDateTime, EntryType = TraceEntryType.LoopIteration });
        Assert.Equal([TraceEntryType.LoopIteration, TraceEntryType.LoopIteration], trace.Pending.Select(entry => entry.EntryType));
        time.Advance(TimeSpan.FromSeconds(1));
        trace.Add(new ExecutionTraceEntry { Timestamp = time.GetUtcNow().UtcDateTime, EntryType = TraceEntryType.LoopIteration });
        var store = new InMemoryWorkflowStateRepository();
        var instance = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = "frozen-truncation", WorkflowName = "trace" };
        var commit = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, 0, traceEntries: trace.Pending);
        var markerId = trace.Pending[^1].TraceId;
        trace.Add(new ExecutionTraceEntry { EntryType = TraceEntryType.LoopCompleted });
        Assert.Equal(3, ((TraceTruncation)trace.Pending[^1].Payload!).DroppedCount);
        Assert.Equal(markerId, trace.Pending[^1].TraceId);
        Assert.Equal(Start.UtcDateTime, trace.Pending[^1].Timestamp);
        Assert.Equal(2, ((TraceTruncation)commit.ReadTraceEntries()[^1].Payload!).DroppedCount);
        Assert.Equal(markerId, commit.ReadTraceEntries()[^1].TraceId);
    }

    [Fact]
    public void TheDefaultBoundIsOneThousandAndNonpositiveBoundsAreRejected()
    {
        Assert.Equal(1_000, new IxIFlowOptions().MaxTraceEntriesPerCommit);
        foreach (var limit in new[] { 0, -1 })
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection()
                .AddIxIFlow(options => options.MaxTraceEntriesPerCommit = limit));
    }

    [Fact]
    public async Task ArrivalRefusalsReadTheLockedFlagWithoutReadingTheDataPart()
    {
        var time = new FakeTimeProvider(Start);
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<TraceEventsTests>.ConnectionString, time);
        using var services = Services(store, time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(Workflow.Create<Data>("RefusalWithoutData")
            .Step<Noop>().WaitFor<Go>("go").Build(), new Data());
        await using (var connection = new SqliteConnection(SqliteTestDatabase<TraceEventsTests>.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ixiflow_workflow_parts WHERE instance_id = $instance AND part_key = $part";
            command.Parameters.AddWithValue("$instance", started.InstanceId);
            command.Parameters.AddWithValue("$part", CheckpointParts.DataKey);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var refused = await store.AppendEventAsync(new WorkflowEvent(started.InstanceId, "wrong", typeof(Go).AssemblyQualifiedName!,
            "{}", Key: "wrong"));
        Assert.Equal(EventAppendStatus.Rejected, refused.Status);
        var entry = Assert.Single((await store.GetTraceAsync(started.InstanceId)).Select(item => item.Entry),
            entry => entry.EntryType == TraceEntryType.EventRefused);
        Assert.Equal(EventRejection.NoOpenWait, Assert.IsType<ReceivedEventTrace>(entry.Payload).RefusalReason);
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, FakeTimeProvider time,
        int limit = 1_000, string? actor = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(store);
        services.AddTransient<AdvanceClock>();
        services.AddIxIFlow(options => { options.MaxTraceEntriesPerCommit = limit; options.Actor = actor; });
        return services.BuildServiceProvider();
    }

    private sealed class TraceStore(TimeProvider time) : FaultInjectingRepository(time)
    {
        public List<CheckpointCommit> Commits { get; } = [];
        public bool FailCompletions { get; set; }
        public bool FailControlStarts { get; set; }
        public override Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var entries = commit.ReadTraceEntries();
            if (FailCompletions && entries.Any(entry => entry.EntryType == TraceEntryType.ActivityCompleted) ||
                FailControlStarts && entries.Any(entry => entry.EntryType == TraceEntryType.ConditionTaken))
                throw new IOException("Injected trace commit failure");
            Commits.Add(commit);
            return base.CommitWorkflowInstanceAsync(commit);
        }
    }

    public sealed class Data
    {
        public int Count { get; set; }
        public List<int> Items { get; set; } = [10, 20];
        public List<ItemResult<int>> Results { get; set; } = [];
        public List<ItemResult<int>> ParallelResults { get; set; } = [];
        public string Secret { get; set; } = "secret";
    }
    public sealed class Go
    {
        public int Index { get; set; }
        public string Secret { get; set; } = "";
    }
    public sealed class Wrong;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Increment : IAsyncActivity
    {
        public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Value++;
            return Task.CompletedTask;
        }
    }
    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secret");
    }
    public sealed class AdvanceClock(TimeProvider time) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ((FakeTimeProvider)time).Advance(TimeSpan.FromSeconds(1));
            return Task.CompletedTask;
        }
    }
}

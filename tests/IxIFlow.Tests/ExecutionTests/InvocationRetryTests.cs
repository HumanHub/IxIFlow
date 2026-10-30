using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "D-18")]
[Trait("Feature", "children")]
public sealed class InvocationRetryTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static WorkflowBuilder<Data, object> Parent(string name = "RetryParent") => new([], name);
    private static RetryPolicy Immediate(int retries) => new(TimeSpan.Zero, TimeSpan.Zero, retries, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvocationInsideSwitchCaseRetainsItsErrorPolicy(bool faultScope)
    {
        var definition = faultScope
            ? Parent("FaultSwitchInvocationRetry")
                .Try(body => body.Step<CommitBudgetTests.FailActivity>())
                .Catch<InvalidOperationException>(body => body
                    .Switch(ctx => ctx.WorkflowData.Value, cases => cases.Case("selected", arm => arm
                        .Invoke<Child, ChildData>(setup => setup
                            .Input(data => data.Value)
                            .OnError<InvalidOperationException>(error => error.ThenRetry(Immediate(1)))
                            .From(ctx => ctx.WorkflowData.Value!)
                            .Output(data => data.Result).OnError(error => error.ThenTerminate())
                            .To(ctx => ctx.WorkflowData.Result)))))
                .Build()
            : Parent("SwitchInvocationRetry")
                .Switch(ctx => ctx.WorkflowData.Value, cases => cases.Case("selected", body => body
                    .Invoke<Child, ChildData>(setup => setup
                        .Input(data => data.Value).From(ctx => ctx.WorkflowData.Value!)
                        .OnError<InvalidOperationException>(error => error.ThenRetry(Immediate(1)))
                        .Output(data => data.Result).To(ctx => ctx.WorkflowData.Result))))
                .Build();

        await AssertNestedInvocationRetry(definition, "/case/");
    }

    [Fact]
    public async Task InvocationInsideInlineBodyRetainsItsErrorPolicy()
    {
        var definition = Parent("InlineInvocationRetry")
            .Use<RetryingInline, Data>(setup => setup
                .Input(data => data.Value).From(ctx => ctx.WorkflowData.Value)
                .Output(data => data.Result).To(ctx => ctx.WorkflowData.Result))
            .Build();

        await AssertNestedInvocationRetry(definition, "/inline/");
    }

    private static async Task AssertNestedInvocationRetry(WorkflowDefinition definition, string scope)
    {
        var ledger = new Ledger();
        await using var services = Services(ledger: ledger, payloadThreshold: 128);

        var result = await services.GetRequiredService<IWorkflowClient>().RunToEndAsync(definition,
            new Data { Value = "selected" }, new() { EnableTracing = true });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("selected", Assert.IsType<Data>(result.WorkflowData).Result);
        Assert.Equal(2, ledger.Calls);
        var store = services.GetRequiredService<IWorkflowStateRepository>();
        var saved = (await store.GetWorkflowInstanceAsync(result.InstanceId))!;
        var checkpoint = ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(saved, Reader(store)));
        Assert.Empty(checkpoint.InvocationRecords);
        var attempts = await InvocationTraceAssertions.AttemptsAsync(store, result.InstanceId, definition);
        Assert.Equal(2, attempts.Length);
        Assert.Contains(scope, attempts[0].StepId);
        Assert.Equal(0, attempts[0].PolicyIndex);
        Assert.Equal(StepErrorAction.Retry, attempts[0].Action);
        Assert.Equal(WorkflowStatus.Failed, attempts[0].ChildStatus);
        Assert.Equal(WorkflowStatus.Completed, attempts[1].ChildStatus);
        Assert.NotEqual(attempts[0].ChildInstanceId, attempts[1].ChildInstanceId);
        Assert.Single(attempts.Select(attempt => attempt.InvocationId).Distinct());
        var inspection = (await services.GetRequiredService<IWorkflowOperations>().InspectAsync(result.InstanceId))!;
        Assert.Empty(inspection.Invocations);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task FailedChildGetsANewChildAndRetainedHistoryWithTracingOptional(bool tracing, bool externalRecords)
    {
        var ledger = new Ledger();
        await using var services = Services(ledger: ledger, payloadThreshold: externalRecords ? 128 : 0);

        var definition = Parent().Invoke<Child, ChildData>(setup => setup
            .Input(data => data.Value).From(ctx => ctx.WorkflowData.Value)
            .OnError<InvalidOperationException>(error => error.ThenRetry(Immediate(2)))
            .Output(data => data.Result).To(ctx => ctx.WorkflowData.Result)).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunToEndAsync(definition, new Data { Value = "fresh" }, new() { EnableTracing = tracing });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("fresh", Assert.IsType<Data>(result.WorkflowData).Result);
        var store = services.GetRequiredService<IWorkflowStateRepository>();
        var saved = (await store.GetWorkflowInstanceAsync(result.InstanceId))!;
        var checkpoint = ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(saved, Reader(store)));
        Assert.Empty(checkpoint.InvocationRecords);
        Assert.DoesNotContain(saved.SavedParts!.Keys, key => key.StartsWith("v/", StringComparison.Ordinal));
        var children = (await store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: result.InstanceId))).Items;
        Assert.Equal(2, children.Count);
        Assert.Equal(2, children.Select(child => child.InstanceId).Distinct().Count());
        Assert.Contains(children, child => child.Status == WorkflowStatus.Failed);
        Assert.Contains(children, child => child.Status == WorkflowStatus.Completed);
        Assert.Equal(2, ledger.Calls);
        var inspection = (await services.GetRequiredService<IWorkflowOperations>().InspectAsync(result.InstanceId))!;
        Assert.Empty(inspection.Invocations);

        if (tracing)
        {
            var traces = await client.GetTraceAsync(result.InstanceId);

            var starts = traces.Where(entry => entry.EntryType == TraceEntryType.ActivityStarted)
                .Select(entry => entry.Payload).OfType<InvocationTrace>().ToArray();

            Assert.Equal(2, starts.Length);
            Assert.All(starts, start => Assert.NotNull(start.ChildInstanceId));
            Assert.Contains(traces, entry => entry.Payload is RetryTrace { Invocation.AttemptNumber: 1 });
            var history = await store.GetTraceAsync(result.InstanceId, 0, 1000);
            var replay = TraceReplay.Load(definition, history);

            while (replay.StepForward())
            {
            }

            Assert.Equal(2, replay.Current.Invocations.Count);
            Assert.DoesNotContain("failure-text", JsonSerializer.Serialize(replay.Current));
        }
        else
            Assert.Empty(await client.GetTraceAsync(result.InstanceId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RequiredValueFailureParksWithoutAChildAndDebuggerEditIsMappedOnRetry(string? value)
    {
        var clock = new FakeTimeProvider(Origin);
        var store = new InMemoryWorkflowStateRepository(clock);

        var definition = Parent("FreshInput").Invoke<RequiredChild, ChildData>(setup => setup
            .Input(data => data.Value).From(ctx => ctx.WorkflowData.Value!)
            .OnError<WorkflowEngineException>(error => error.ThenRetry(new RetryPolicy(
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 1, 1)))
            .Output(data => data.Result).To(ctx => ctx.WorkflowData.Result)).Build();

        await using var services = Services(store, clock);
        var client = services.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync(definition, new Data { Value = value });
        var parked = await UntilAsync(store, id, instance => instance.RunnableAtUtc > Origin.UtcDateTime.AddSeconds(30));
        var before = ExecutionCheckpoint.Read(parked.ExecutionStateJson);
        var record = Assert.Single(before.InvocationRecords.Values);
        var failed = Assert.Single(record.Attempts);
        Assert.Null(failed.ChildInstanceId);
        Assert.Equal("Retry", failed.Disposition);
        Assert.Empty((await store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items);
        var deadline = record.RetryAfterUtc;
        Assert.Equal(Origin.UtcDateTime.AddMinutes(1), deadline);
        var operations = services.GetRequiredService<IWorkflowOperations>();
        await operations.PauseAsync(id, "editor");
        await UntilAsync(store, id, instance => instance.Status == WorkflowStatus.Paused);
        var inspection = (await operations.InspectAsync(id))!;
        Assert.Equal(deadline, Assert.Single(inspection.Invocations).RetryAfterUtc);

        await operations.EditDataAsync(id, "correct-input", inspection.Revision,
            new(inspection.DebugState.PauseGeneration),
            [new DataPatch([nameof(Data.Value)], JsonSerializer.SerializeToElement("corrected"))], "editor");

        await operations.ContinueAsync(id, "editor");
        clock.Advance(TimeSpan.FromMinutes(2));
        await client.RecoverAsync(id);
        var completed = await client.AwaitAsync(id, WorkflowAwaitCondition.Finished).WaitAsync(ChildRuns.Patience);
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal("corrected", Assert.IsType<Data>(completed.WorkflowData).Result);
        var final = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);
        Assert.Empty(final.InvocationRecords);
        var attempts = await InvocationTraceAssertions.AttemptsAsync(store, id, definition);
        Assert.Equal(2, attempts.Length);
        Assert.Null(attempts[0].ChildInstanceId);
        Assert.Equal(WorkflowStatus.Completed, attempts[1].ChildStatus);
    }

    [Theory]
    [InlineData("ignore", 0, true)]
    [InlineData("retry", 0, false)]
    [InlineData("retry", 2, false)]
    [InlineData("terminate", 0, false)]
    public async Task FirstMatchingPolicyNeverFallsThroughAndTerminatePropagates(string action, int retries, bool ignored)
    {
        await using var services = Services(ledger: new Ledger { Failures = int.MaxValue });

        var definition = Parent("PolicyOrder").Try(body => body.Invoke<Child, ChildData>(setup => setup
                .Input(data => data.Value).From(_ => "input")
                .OnError<Exception>(error =>
                {
                    if (action == "retry")
                        error.ThenRetry(Immediate(retries));
                    else if (action == "ignore")
                        error.ThenIgnore();
                    else
                        error.ThenTerminate();
                })
                .OnError<InvalidOperationException>(error => error.ThenIgnore())
                .Output(data => data.Result).To(ctx => ctx.WorkflowData.Result)))
            .Catch<InvalidOperationException>(body => body.Step<Caught>(setup => setup
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Caught)))
            .Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunToEndAsync(definition, new Data { Result = "untouched" });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(!ignored, data.Caught);
        Assert.Equal("untouched", data.Result);
        var store = services.GetRequiredService<IWorkflowStateRepository>();

        Assert.Empty(ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(result.InstanceId))!.ExecutionStateJson)
            .InvocationRecords);

        var attempts = await InvocationTraceAssertions.AttemptsAsync(store, result.InstanceId, definition);
        Assert.Equal(action == "retry" ? retries + 1 : 1, attempts.Length);
        Assert.All(attempts, attempt => Assert.Equal(0, attempt.PolicyIndex));
    }

    [Fact]
    public async Task StaleAndMissingOperatorTargetsCannotSettleANewerRefusedAttempt()
    {
        await using var services = Services(maxBytes: 18_000);

        var definition = Parent("RefusedRetry").Invoke<RequiredChild, ChildData>(setup => setup
            .Input(data => data.Value).From(_ => new string('x', 100_000))
            .OnError(error => error.ThenRetry(Immediate(1)))
            .Output(data => data.Result).To(ctx => ctx.WorkflowData.Result)).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var stopped = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
        var first = Assert.Single(await client.GetPendingActivitiesAsync(stopped.InstanceId));
        Assert.NotNull(first.PolicyAttemptId);

        var missing = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.ResolveAsync(stopped.InstanceId,
            first.InvocationId, ActivityResolution.Failed("observed")));

        Assert.Equal(WorkflowErrorKind.InvalidRequest, missing.Kind);

        var secondStop = await client.ResolveAsync(stopped.InstanceId, first.InvocationId,
            ActivityResolution.Failed("observed", expectedPolicyAttemptId: first.PolicyAttemptId));

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, secondStop.Status);
        var second = Assert.Single(await client.GetPendingActivitiesAsync(stopped.InstanceId));
        Assert.Equal(first.InvocationId, second.InvocationId);
        Assert.NotEqual(first.PolicyAttemptId, second.PolicyAttemptId);
        var store = services.GetRequiredService<IWorkflowStateRepository>();
        var before = (await store.GetWorkflowInstanceAsync(stopped.InstanceId))!;

        foreach (var stale in new[]
                 {
                     ActivityResolution.Failed("duplicate", expectedPolicyAttemptId: first.PolicyAttemptId),
                     ActivityResolution.CompletedInvocation(new ChildData { Result = "stale" },
                         expectedPolicyAttemptId: first.PolicyAttemptId)
                 })
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                client.ResolveAsync(stopped.InstanceId, first.InvocationId, stale));

            Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
            Assert.Equal(before.Revision, (await store.GetWorkflowInstanceAsync(stopped.InstanceId))!.Revision);
        }

        var completed = await client.ResolveAsync(stopped.InstanceId, second.InvocationId,
            ActivityResolution.CompletedInvocation(new ChildData { Result = "observed" }, expectedPolicyAttemptId: second.PolicyAttemptId));

        Assert.Equal("observed", Assert.IsType<Data>(completed.WorkflowData).Result);
        var trace = await client.GetTraceAsync(stopped.InstanceId);

        Assert.Equal(new[] { first.PolicyAttemptId, second.PolicyAttemptId }, trace.Select(entry => entry.Payload)
            .OfType<ActivityResolutionTrace>().Select(entry => entry.PolicyAttemptId));
    }

    [Theory]
    [InlineData(36500, 0, 1, true)]
    [InlineData(36500, 1, 1, false)]
    [InlineData(36501, 0, 1, false)]
    [InlineData(36501, 0, 0, true)]
    public void ReachableBackoffUsesTheInclusiveParkingLimit(int days, int extraMilliseconds, int retries, bool valid)
    {
        var interval = TimeSpan.FromDays(days) + TimeSpan.FromMilliseconds(extraMilliseconds);

        Action build = () => Parent().Invoke<RequiredChild, ChildData>(setup => setup
            .Input(data => data.Value).From(_ => "input")
            .OnError(error => error.ThenRetry(new RetryPolicy(interval, interval, retries, 1)))).Build();

        if (valid)
            build();
        else
            Assert.Contains("parking duration", Assert.Throws<WorkflowEngineException>(build).Message);
    }

    [Fact]
    public void ReachableCappedBackoffsAreValidatedWithoutWalkingTheBudget()
    {
        Assert.Null(RetryProblem(1, TimeSpan.FromSeconds(1), TimeSpan.MaxValue, 2));
        Assert.Null(RetryProblem(int.MaxValue, TimeSpan.Zero, TimeSpan.MaxValue, 2));
        Assert.NotNull(RetryProblem(2, TimeSpan.FromDays(20000), TimeSpan.MaxValue, 2));
        Assert.Null(RetryProblem(int.MaxValue, TimeSpan.FromDays(20000), TimeSpan.Zero, 2));

        foreach (var coefficient in new[] { double.NaN, double.PositiveInfinity, 0.5 })
            Assert.NotNull(RetryProblem(0, TimeSpan.Zero, TimeSpan.Zero, coefficient));
    }

    [Fact]
    public void AttemptIdentityIncludesIncarnationFrameAndLoopOccurrence()
    {
        var instance = new WorkflowInstance { InstanceId = "a|b:c", Incarnation = Guid.NewGuid() };
        var continuation = new ContinuationState { Stack = [new ScopePosition { ActivationId = "frame" }] };
        var first = InvocationRecords.LogicalId(instance, continuation, "step");
        Assert.Equal(first, InvocationRecords.LogicalId(instance, continuation, "step"));
        continuation.Stack[0].LoopIterationCount++;
        Assert.NotEqual(first, InvocationRecords.LogicalId(instance, continuation, "step"));
        continuation.Stack[0].LoopIterationCount = 0;
        instance.Incarnation = Guid.NewGuid();
        Assert.NotEqual(first, InvocationRecords.LogicalId(instance, continuation, "step"));
        Assert.NotEqual(InvocationRecords.Identity("x", "a|b", "c"), InvocationRecords.Identity("x", "a", "b|c"));
        Assert.NotEqual(InvocationRecords.Identity("x", "\ud800"), InvocationRecords.Identity("x", "\ud801"));
    }

    [Fact]
    public void FluentPoliciesRetainPreviousContextAcrossEveryMappingSubBuilder()
    {
        Parent().Step<Caught>().Invoke<RequiredChild, ChildData>(setup => setup
                .OnError(error => error.ThenIgnore())
                .Output(data => data.Result).OnError(error => error.ThenIgnore()).To(ctx => ctx.WorkflowData.Result)
                .Input(data => data.Value).OnError<Exception>(error => error.ThenIgnore()).From(ctx => ctx.PreviousStep.Value.ToString()))
            .Build();
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedRecordsOutliveForEachItemFrames(bool parallel)
    {
        await using var services = Services();

        var definition = Parent("ItemInvocations").ForEach(itemsExpression: ctx => ctx.WorkflowData.Values,
            (body, item) => body.Invoke<RequiredChild, ChildData>(setup => setup
                .Input(data => data.Value).From(ctx => ctx.Item(item).ToString())
                .OnError(error => error.ThenRetry(Immediate(1)))),
            setup =>
            {
                if (parallel)
                    setup.Parallel(2);
            }).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunToEndAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var saved = (await services.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(result.InstanceId))!;
        var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
        Assert.Empty(checkpoint.InvocationRecords);

        var attempts = await InvocationTraceAssertions.AttemptsAsync(services.GetRequiredService<IWorkflowStateRepository>(),
            result.InstanceId, definition);

        Assert.Equal(3, attempts.Length);
        Assert.Equal(3, attempts.Select(attempt => attempt.InvocationId).Distinct().Count());
        Assert.Equal(3, attempts.Select(attempt => attempt.ChildInstanceId).Distinct().Count());
        Assert.All(attempts, attempt => Assert.Equal("Returned", attempt.Disposition));
        Assert.DoesNotContain(checkpoint.Continuations, continuation => continuation.ParentJoinId != null);
    }


    [Fact]
    public async Task EachSequentialLoopOccurrenceHasItsOwnLogicalInvocation()
    {
        await using var services = Services();

        var definition = Parent("LoopInvocations").WhileDo(conditionExpression: ctx => ctx.WorkflowData.Iteration < 2,
            configure: body => body.Invoke<RequiredChild, ChildData>(setup => setup
                    .Input(data => data.Value).From(ctx => ctx.WorkflowData.Iteration.ToString()))
                .Step<Increment>()).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunToEndAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var store = services.GetRequiredService<IWorkflowStateRepository>();

        Assert.Empty(ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(result.InstanceId))!.ExecutionStateJson)
            .InvocationRecords);

        var attempts = await InvocationTraceAssertions.AttemptsAsync(store, result.InstanceId, definition);
        Assert.Equal(2, attempts.Length);
        Assert.Equal(2, attempts.Select(attempt => attempt.InvocationId).Distinct().Count());
        Assert.Equal(2, attempts.Select(attempt => attempt.ChildInstanceId).Distinct().Count());
    }

    [Fact]
    public async Task OutputMappingFailureDoesNotInvokeAReplacementChild()
    {
        await using var services = Services();

        var definition = Parent("FailedInvocationOutput").Try(body => body.Invoke<RequiredChild, ChildData>(setup => setup
                .Input(data => data.Value).From(_ => "input")
                .OnError(error => error.ThenRetry(Immediate(3)))
                .Output(data => data.Result).To(ctx => ctx.WorkflowData.ThrowingResult)))
            .Catch<InvalidOperationException>(body => body.Step<Caught>()).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunToEndAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var store = services.GetRequiredService<IWorkflowStateRepository>();

        Assert.Empty(ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(result.InstanceId))!.ExecutionStateJson)
            .InvocationRecords);

        var attempt = Assert.Single(await InvocationTraceAssertions.AttemptsAsync(store, result.InstanceId, definition));
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal("OutputFailed", attempt.Disposition);
        Assert.Equal(WorkflowStatus.Completed, attempt.ChildStatus);

        Assert.DoesNotContain(await services.GetRequiredService<IWorkflowClient>().GetTraceAsync(result.InstanceId),
            entry => entry.Payload is RetryTrace);
    }

    [Fact]
    public async Task BreakpointHitsEachNewAttemptAndDoesNotHitAChildCompletion()
    {
        await using var services = Services(ledger: new Ledger());

        var definition = Parent("RetryBreakpoints").WaitFor<Go>("go")
            .Invoke<Child, ChildData>(setup => setup.Input(data => data.Value).From(_ => "input")
                .OnError(error => error.ThenRetry(Immediate(1)))).WithId("invoke").Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var first = await client.RunAsync(definition, new Data());
        await operations.SetBreakpointsAsync(first.InstanceId, [new("attempt", "invoke")], "debugger");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(first.InstanceId, "go", new Go())).Status);
        await operations.ContinueAsync(first.InstanceId, "debugger");
        var store = services.GetRequiredService<IWorkflowStateRepository>();

        var next = await UntilAsync(store, first.InstanceId, instance => instance.Status == WorkflowStatus.Paused &&
            ExecutionCheckpoint.Read(instance.ExecutionStateJson).InvocationRecords.Values.Any(record => record.Attempts.Count == 1));

        Assert.Equal(2, Assert.Single(await operations.GetBreakpointsAsync(first.InstanceId)).HitCount);
        await operations.ContinueAsync(first.InstanceId, "debugger");

        Assert.Equal(WorkflowExecutionStatus.Success,
            (await client.AwaitAsync(first.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(ChildRuns.Patience)).Status);

        Assert.Equal(2, Assert.Single(await operations.GetBreakpointsAsync(first.InstanceId)).HitCount);
    }

    [Fact]
    public void SagaStepPolicyStillMatchesTheDurablyCarriedOriginalException()
    {
        var definition = Parent("SharedStepPolicy").Step<Caught>().Saga(saga => saga.Step<Caught>(setup => setup
            .OnError<NonRestorable>(error => error.ThenIgnore()))).Build();

        var scopes = new WorkflowScopeCatalog(definition);

        var continuation = new ContinuationState
        {
            Stack =
            [
                new ScopePosition
                {
                    ScopeId = "root", NextStepIndex = 1,
                    SagaState = new SagaScopeState()
                },
                new ScopePosition { ScopeId = scopes.SagaScope(definition.Steps[1]) }
            ]
        };

        var saved = SerializedException.Capture(new NonRestorable(42), new WorkflowScopeCatalog(new WorkflowDefinition()),
            new ExecutionCheckpoint(), null, newOccurrence: true);

        Assert.False(saved.IsRestorable);
        saved = JsonSerializer.Deserialize<SerializedException>(JsonSerializer.Serialize(saved))!;
        var error = Assert.IsType<DurableWorkflowException>(saved.ForPropagation(new ExecutionCheckpoint()));
        var decision = StepErrorPolicies.Handle(scopes, new ExecutionCheckpoint(), continuation, error, Origin.UtcDateTime, null);
        Assert.Equal(StepFailureAction.Ignore, decision.Action);
        Assert.Equal(1, continuation.Stack[^1].NextStepIndex);
    }

    public sealed class Go;

    public sealed class NonRestorable(int code) : Exception("non-restorable-" + code);

    private static string? RetryProblem(int retries, TimeSpan initial, TimeSpan maximum, double coefficient)
    {
        try
        {
            RetrySchedule.NextAttemptUtc(new RetryPolicy(initial, maximum, retries, coefficient), 1, Origin.UtcDateTime);

            return null;
        }
        catch (WorkflowEngineException error)
        {
            return error.Message;
        }
    }

    private static PayloadReader Reader(IWorkflowStateRepository store) => new(async reference =>
            await store.PayloadStore.GetAsync(reference) ?? throw new InvalidOperationException(),
        new PayloadBudget(long.MaxValue));

    private static async Task<WorkflowInstance> UntilAsync(IWorkflowStateRepository store, string id,
        Func<WorkflowInstance, bool> predicate)
    {
        return (await TestEventually.UntilAsync(id, "the invocation retry predicate",
            () => store.GetWorkflowInstanceAsync(id), saved => saved != null && predicate(saved), ChildRuns.Patience))!;
    }

    internal static ServiceProvider Services(InMemoryWorkflowStateRepository? store = null, TimeProvider? clock = null,
        Ledger? ledger = null, long? maxBytes = null, long payloadThreshold = 0)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddIxIFlow(options =>
        {
            options.VerifyCheckpointParts = true;
            options.Debugging = true;
            options.PayloadThresholdBytes = payloadThreshold;

            if (maxBytes != null)
                options.MaxCheckpointBytes = maxBytes.Value;
        });

        if (store != null)
            services.AddSingleton<IWorkflowStateRepository>(store);

        if (clock != null)
            services.AddSingleton(clock);

        services.AddSingleton(ledger ?? new Ledger { Failures = 0 });
        services.RegisterWorkflow<Child>();
        services.RegisterWorkflow<RequiredChild>();

        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        public string? Value { get; set; }
        public string? Result { get; set; }
        public bool Caught { get; set; }
        public int Iteration { get; set; }
        public List<int> Values { get; set; } = [1, 2, 3];

        [System.Text.Json.Serialization.JsonIgnore]
        public string? ThrowingResult
        {
            get => null;
            set => throw new InvalidOperationException("output failure");
        }
    }

    public sealed class ChildData
    {
        [WorkflowInput, Required] public string Value { get; set; } = "";
        [WorkflowOutput] public string? Result { get; set; }
    }

    public sealed class Ledger
    {
        public int Calls;
        public int Failures = 1;
    }

    [Workflow("RetryingInline", Version = 1)]
    public sealed class RetryingInline : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> builder) => builder.Step<Echo>().Invoke<Child, ChildData>(setup => setup
            .Input(data => data.Value).From(ctx => ctx.WorkflowData.Value!)
            .OnError<InvalidOperationException>(error => error.ThenRetry(Immediate(1)))
            .Output(data => data.Result).To(ctx => ctx.WorkflowData.Result));
    }

    [Workflow("RetryChild", Version = 1)]
    public sealed class Child : IWorkflow<ChildData>
    {
        public void Build(IWorkflowBuilder<ChildData> builder) => builder.Step<Flaky>(setup => setup
            .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Value)
            .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Result));
    }

    [Workflow("RequiredRetryChild", Version = 1)]
    public sealed class RequiredChild : IWorkflow<ChildData>
    {
        public void Build(IWorkflowBuilder<ChildData> builder) => builder.Step<Echo>(setup => setup
            .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Value)
            .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Result));
    }

    public sealed class Flaky(Ledger ledger) : IAsyncActivity
    {
        public string Value { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref ledger.Calls) <= ledger.Failures)
                throw new InvalidOperationException("failure-text");

            return Task.CompletedTask;
        }
    }

    public sealed class Increment : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ((Data)context.WorkflowData).Iteration++;

            return Task.CompletedTask;
        }
    }

    public sealed class Echo : IAsyncActivity
    {
        public string Value { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Caught : IAsyncActivity
    {
        public bool Value { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Value = true;

            return Task.CompletedTask;
        }
    }
}
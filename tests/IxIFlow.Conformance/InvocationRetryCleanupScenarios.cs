using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using static IxIFlow.Tests.Conformance.InvocationRetryRecoveryScenarios;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryCleanupScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CancellingInvocationRetryInFinallyContinuesCleanupAfterReopening(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        fixture.Ledger.Failures = int.MaxValue;
        var first = await fixture.Open(holdRetries: true);
        var child = Workflow.Create<Data>("FinallyRetryChild").Step<FailOnce>().Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<Data, object>([], "FinallyRetryInvocation")
            .Try(body => body.Step<Cleanup>())
            .Finally(body => body.Invoke<Data>(child.Name, 1, setup => setup.OnError(error => error.ThenRetry(
                    new RetryPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(1), 2, 1))))
                .Step<Cleanup>()).Build();

        await fixture.Catalog.RegisterWorkflowAsync(parent);
        var id = await first.Client.StartAsync(parent, new Data());
        var parked = await first.Parked(id);
        var logicalId = Record(parked).Id;
        await first.DisposeAsync();
        var second = await fixture.Open();
        await second.Client.CancelAsync(id, new CancellationReason { ReasonCode = "cancel-cleanup-retry" });
        var result = await InvocationRetryProfile.Finished(second.Client, id);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.Equal(2, Assert.IsType<Data>(result.WorkflowData).Cleanup);
        await InvocationTraceAssertions.PrunedAsync(second.Store, id);
        var attempts = await InvocationTraceAssertions.AttemptsAsync(second.Store, id, parent);
        var attempt = Assert.Single(attempts);
        Assert.Equal(logicalId, attempt.InvocationId);
        Assert.Equal("Cancelled", attempt.Disposition);
        Assert.Null(attempt.RetryAfterUtc);
        Assert.DoesNotContain(await second.Store.GetOpenWaitsAsync(new(InstanceId: id)), wait => wait.Kind == WaitKind.Child);
        Assert.Equal(1, fixture.Ledger.Calls);
        var replay = TraceReplay.Load(parent, await second.Store.GetTraceAsync(id));

        while (replay.StepForward())
        {
        }

        Assert.Equal("Cancelled", Assert.Single(replay.Current.Invocations).Disposition);
    }

    public static IEnumerable<object[]> TraceProfiles => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "full", "disabled", "truncated" }.Select(mode => new object[] { profile, mode }));

    [Theory, MemberData(nameof(TraceProfiles))]
    public async Task DetachedInvocationClosureSurvivesOptionalAndTruncatedTracing(string profile, string mode)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);

        var host = await fixture.Open(services => services.AddIxIFlow(options =>
        {
            options.Debugging = true;
            options.VerifyCheckpointParts = true;

            if (mode == "truncated")
                options.MaxTraceEntriesPerCommit = 1;
        }));

        var child = new WorkflowBuilder<Data, object>([], "DetachedRetryChild").WaitFor<Signal>("finish").Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<Data, object>([], "DetachedRetryInvocation")
            .Invoke<Data>(child.Name, 1, setup => setup.CancelWithParent(false).OnError(error => error.ThenRetry(new RetryPolicy(2))))
            .Build();

        var started = await host.Client.RunAsync(parent, new Data(), new() { EnableTracing = mode != "disabled" });
        var active = Assert.Single((await host.Operations.InspectAsync(started.InstanceId))!.Invocations);
        var activeAttempt = Assert.Single(active.Attempts);
        await host.Client.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "detach" });
        await InvocationRetryProfile.Finished(host.Client, started.InstanceId);
        var inspection = (await host.Operations.InspectAsync(started.InstanceId))!;
        Assert.Empty(inspection.Invocations);
        await InvocationTraceAssertions.PrunedAsync(host.Store, started.InstanceId);
        var ended = (await host.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal(WorkflowStatus.Cancelled, ended.Status);
        Assert.NotNull(ended.CompletedAt);
        var childRow = (await host.Store.GetWorkflowInstanceAsync(activeAttempt.ChildInstanceId!))!;
        var closed = (await host.Store.GetWaitAsync(started.InstanceId, childRow.ParentWaitId!))!;
        Assert.NotNull(closed.ClosedAtUtc);
        Assert.Equal(WaitCloseReason.Cancelled, closed.CloseReason);
        Assert.Null(await host.Store.GetCancellationRequestAsync(childRow.InstanceId));
        Assert.Equal(0, active.ScheduledRetries);
        Assert.Single((await host.Store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: started.InstanceId))).Items);
        var traces = await host.Store.GetTraceAsync(started.InstanceId);

        if (mode == "disabled")
            Assert.Empty(traces);
        else
        {
            var replay = TraceReplay.Load(parent, traces);

            while (replay.StepForward())
            {
            }

            if (mode == "full")
            {
                var fact = Assert.Single(replay.Current.Invocations);
                Assert.Equal("Detached", fact.Disposition);
                Assert.Equal(activeAttempt.ChildInstanceId, fact.ChildInstanceId);
            }
            else
            {
                Assert.Contains(traces, trace => trace.Entry.EntryType == TraceEntryType.Truncated);
                Assert.False(replay.Current.IsComplete);
                Assert.Contains(replay.Current.IncompleteReasons, reason => reason.Contains("omitted"));
            }
        }

        await host.Client.CancelAsync(activeAttempt.ChildInstanceId!, new CancellationReason { ReasonCode = "cleanup" });
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task InvocationRetryStepOverKeepsSiblingHeldAndBreakpointCountsSurviveAHostChange(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var first = await fixture.Open();
        var child = Workflow.Create<Data>("DebugRetryChild").Step<FailOnce>().Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<Data, object>([], "DebugRetryInvocation").Parallel(paths => paths
            .Do(body => body.WaitFor<Signal>("held").Step<Noop>())
            .Do(body => body.WaitFor<Signal>("go").Invoke<Data>(child.Name, 1,
                    setup => setup.OnError(error => error.ThenRetry(new RetryPolicy(1))))
                .WithId("invoke").Step<Noop>().WithId("after"))).Build();

        await fixture.Catalog.RegisterWorkflowAsync(parent);
        var started = await first.Client.RunAsync(parent, new Data());
        await first.Operations.SetBreakpointsAsync(started.InstanceId, [new("attempt", "invoke")], "debugger");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await first.Client.ResumeAsync(started.InstanceId, "go", new Signal())).Status);
        await first.Operations.ContinueAsync(started.InstanceId, "debugger");

        await InvocationRetryProfile.Until(first.Store, started.InstanceId, saved => saved.Status == WorkflowStatus.Paused &&
            ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords.Values.Any(r => r.ScheduledRetries == 1));

        Assert.Equal(2, Assert.Single(await first.Operations.GetBreakpointsAsync(started.InstanceId)).HitCount);
        await first.DisposeAsync();
        var second = await fixture.Open();
        Assert.Equal(2, Assert.Single(await second.Operations.GetBreakpointsAsync(started.InstanceId)).HitCount);
        var before = ExecutionCheckpoint.Read((await second.Store.GetWorkflowInstanceAsync(started.InstanceId))!.ExecutionStateJson);
        var selected = before.Debug.Stop!.BranchId;
        var held = before.Continuations.Where(c => c.Id != selected).Select(c => JsonSerializer.Serialize(c)).ToArray();

        // A second event remains pending while only the invocation's branch is stepped.
        await second.Store.AppendEventAsync(new(started.InstanceId, "held-again", WorkflowTypeIdentity.SavedName(typeof(Signal)), "{}",
            Key: "held"));

        await second.Operations.SetBreakpointsAsync(started.InstanceId, [], "step");

        Assert.Equal(WorkflowExecutionStatus.Paused,
            (await second.Operations.StepAsync(started.InstanceId, selected, DebugStepKind.Over, "step")).Status);

        var after = ExecutionCheckpoint.Read((await second.Store.GetWorkflowInstanceAsync(started.InstanceId))!.ExecutionStateJson);
        Assert.Equal(held, after.Continuations.Where(c => c.Id != selected).Select(c => JsonSerializer.Serialize(c)).ToArray());
        Assert.Equal(EventOutcome.Pending, (await second.Store.GetEventAsync(started.InstanceId, "held-again"))!.Outcome);
        Assert.Equal(2, Record((await second.Store.GetWorkflowInstanceAsync(started.InstanceId))!).Attempts.Count);
        Assert.Equal(1, Record((await second.Store.GetWorkflowInstanceAsync(started.InstanceId))!).ScheduledRetries);
        var continued = await second.Operations.ContinueAsync(started.InstanceId, "continue");

        try
        {
            await Success(second.Client, started.InstanceId);
        }
        catch (TimeoutException error)
        {
            // Capture evidence before fixture disposal drains the host and changes the checkpoint.
            // Diagnostic reads have their own bound; the completion timeout stays unchanged.
            string state;

            try
            {
                state = await SteppingHangState(second, started.InstanceId).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception diagnosticError)
            {
                state = $"State capture failed: {diagnosticError}";
            }

            throw new TimeoutException($"{error.Message}\nProfile: {profile}; Continue returned: {continued.Status}; " +
                $"error: {continued.ErrorKind}: {continued.ErrorMessage}\n{state}", error);
        }
    }

    private static async Task<string> SteppingHangState(InvocationRetryProfile.Host host, string id)
    {
        var saved = (await host.Store.GetWorkflowInstanceAsync(id))!;
        var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);

        var children = await Task.WhenAll(checkpoint.InvocationRecords.Values.SelectMany(record => record.Attempts)
            .Where(attempt => attempt.ChildInstanceId != null).Select(async attempt =>
            {
                var child = await host.Store.GetWorkflowInstanceAsync(attempt.ChildInstanceId!);

                return new { attempt.Number, attempt.ChildInstanceId, child?.Status, child?.Revision, child?.RunnableAtUtc };
            }));

        var debug = (await host.Store.GetDebugStatesAsync([id])).GetValueOrDefault(id);
        var inbox = await host.Store.GetPendingEventsAsync(id, 0, 100);
        var held = await host.Store.GetEventAsync(id, "held-again");
        var trace = await host.Store.GetTraceAsync(id);

        return $"State: {JsonSerializer.Serialize(new
        { saved.Status, saved.Revision, saved.RunnableAtUtc,
            Debug = debug, Inbox = inbox, Held = held, Children = children, Trace = trace.TakeLast(20) })}\n" +
            $"Checkpoint: {saved.ExecutionStateJson}";
    }

    public sealed class Cleanup : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ((Data)context.WorkflowData).Cleanup++;

            return Task.CompletedTask;
        }
    }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
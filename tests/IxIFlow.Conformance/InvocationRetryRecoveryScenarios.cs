using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryRecoveryScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FailedInvocationScheduleCommitRecoversTheOldOutcomeAndSpendsOneRetry(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var fault = new InvocationRetryFault { Before = InvocationRetryFault.Schedule, Remaining = 3 };
        var first = await fixture.Open(fault: fault);
        var parent = await Definitions(fixture, new RetryPolicy(1));
        var id = await first.Client.StartAsync(parent, new Data());
        await fault.Exhausted.Task.WaitAsync(InvocationRetryProfile.Patience());
        await first.DisposeAsync();
        var second = await fixture.Open();
        var before = Record((await second.Store.GetWorkflowInstanceAsync(id))!);
        Assert.Single(before.Attempts);
        Assert.Equal(0, before.ScheduledRetries);
        var failed = (await second.Store.GetWorkflowInstanceAsync(before.Attempts[0].ChildInstanceId!))!;
        Assert.NotNull(await second.Store.GetWaitAsync(id, failed.ParentWaitId!));
        Assert.Equal(WorkflowStatus.Failed, failed.Status);
        await second.Client.RecoverAsync(id);
        await Success(second.Client, id);
        await InvocationTraceAssertions.PrunedAsync(second.Store, id);
        var attempts = await InvocationTraceAssertions.AttemptsAsync(second.Store, id, parent);
        Assert.Equal(before.Id, Assert.Single(attempts.Select(attempt => attempt.InvocationId).Distinct()));
        Assert.Equal(before.Attempts[0].Id, attempts[0].PolicyAttemptId);
        Assert.Equal(2, attempts.Length);
        Assert.Single(await InvocationTraceAssertions.RetriesAsync(second.Store, id));
        Assert.Equal(2, fixture.Ledger.Calls);
        Assert.Single((await second.Client.GetTraceAsync(id)).Select(e => e.Payload).OfType<RetryTrace>());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task LostInvocationReplacementAcknowledgementKeepsTheAtomicStartAndReceipt(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var fault = new InvocationRetryFault { After = InvocationRetryFault.Replacement, Remaining = 3 };
        var first = await fixture.Open(fault: fault);
        var parent = await Definitions(fixture, new RetryPolicy(1));
        var id = await first.Client.StartAsync(parent, new Data());
        await fault.Exhausted.Task.WaitAsync(InvocationRetryProfile.Patience());
        await first.DisposeAsync();
        var second = await fixture.Open();
        var record = Record((await second.Store.GetWorkflowInstanceAsync(id))!);
        var attempt = record.Attempts[1];
        Assert.Equal(2, record.Attempts.Count);
        var wait = Assert.Single(await second.Store.GetOpenWaitsAsync(new(InstanceId: id, Kind: WaitKind.Child)));
        Assert.Equal(attempt.ChildInstanceId, (await second.Store.GetWorkflowInstanceAsync(attempt.ChildInstanceId!))!.InstanceId);
        Assert.Equal(Assert.Single(fault.Captured!.ChildStarts).ParentWaitId, wait.WaitId);
        await second.Client.RecoverAsync(attempt.ChildInstanceId!);
        await second.Client.RecoverAsync(id);
        await Success(second.Client, id);
        Assert.Equal(2, (await second.Store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items.Count);
        await InvocationTraceAssertions.PrunedAsync(second.Store, id);
        Assert.Single(await InvocationTraceAssertions.RetriesAsync(second.Store, id));
        await second.Store.DeleteWorkflowInstanceAsync(attempt.ChildInstanceId!);
        var repeated = await second.Store.CommitWorkflowInstanceAsync(fault.Captured);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, repeated.Status);
        Assert.Equal(fault.Receipt!.Revision, repeated.Revision);
        Assert.Equal(fault.Receipt.MadeRunnable, repeated.MadeRunnable);
        Assert.Null(await second.Store.GetWorkflowInstanceAsync(attempt.ChildInstanceId!));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FreshHostKeepsInvocationDeadlineBudgetAndIdentityAndRejectsDuplicateOldCompletions(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var fault = new InvocationRetryFault { Observe = InvocationRetryFault.Replacement };
        var first = await fixture.Open(fault: fault, holdRetries: true);
        var parent = await Definitions(fixture, new RetryPolicy(fixture.Delay, fixture.Delay, 1, 1), waitOnSuccess: true);
        var id = await first.Client.StartAsync(parent, new Data());
        var parked = await first.Parked(id);
        var old = Record(parked);
        var deadline = old.RetryAfterUtc!.Value;
        var wakeAt = parked.RunnableAtUtc!.Value;
        var failedChild = (await first.Store.GetWorkflowInstanceAsync(old.Attempts[0].ChildInstanceId!))!;
        var notice = ChildCompleted.NoticeOf(failedChild, ExecutionCheckpoint.Read(failedChild.ExecutionStateJson))!;
        await first.DisposeAsync();
        var second = await fixture.Open(fault: fault);
        var saved = (await second.Store.GetWorkflowInstanceAsync(id))!;
        var reopened = Record(saved);
        Assert.Equal(wakeAt, saved.RunnableAtUtc);
        Assert.Equal(deadline, reopened.RetryAfterUtc);
        Assert.Equal(old.Id, reopened.Id);
        Assert.Equal(old.Attempts[0].Id, Assert.Single(reopened.Attempts).Id);
        Assert.Equal(1, reopened.ScheduledRetries);
        await fixture.Due(second.Store, id, InvocationRetryProfile.RetryDeadline(parked));
        await second.Client.RecoverAsync(id);
        var started = await InvocationRetryProfile.Until(second.Store, id, saved => RecordOrNull(saved)?.Attempts.Count == 2);
        var replacement = Record(started).Attempts[1];
        await InvocationRetryProfile.Until(second.Store, replacement.ChildInstanceId!, saved => saved.Status == WorkflowStatus.Suspended);
        Assert.NotEqual(old.Attempts[0].ChildInstanceId, replacement.ChildInstanceId);
        Assert.Equal(WorkflowStatus.Failed, (await second.Store.GetWorkflowInstanceAsync(failedChild.InstanceId))!.Status);
        var revision = (await second.Store.GetWorkflowInstanceAsync(id))!.Revision;
        var duplicate = new WorkflowEvent(id, notice.EventId, notice.EventType, notice.PayloadJson, TargetWaitId: notice.ParentWaitId);
        Assert.Equal(EventAppendStatus.Duplicate, (await second.Store.AppendEventAsync(duplicate)).Status);
        Assert.Equal(revision, (await second.Store.GetWorkflowInstanceAsync(id))!.Revision);
        await second.Store.AppendEventAsync(duplicate with { EventId = "duplicate-old-notice" });
        await second.Client.RecoverAsync(id);
        var stillWaiting = Record((await second.Store.GetWorkflowInstanceAsync(id))!);
        Assert.Equal(2, stillWaiting.Attempts.Count);
        Assert.Equal(replacement.Id, stillWaiting.Attempts[1].Id);
        Assert.Equal(1, stillWaiting.ScheduledRetries);
        Assert.NotNull(await second.Store.GetWaitAsync(id, Assert.Single(fault.Captured!.ChildStarts).ParentWaitId));
        var receipt = await second.Store.CommitWorkflowInstanceAsync(fault.Captured);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, receipt.Status);
        Assert.Equal(fault.Receipt!.Revision, receipt.Revision);
        await second.Client.ResumeAsync(replacement.ChildInstanceId!, "finish", new Signal());
        await Success(second.Client, id);
        Assert.Equal(2, fixture.Ledger.Calls);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task SavedInvocationResolutionRecoversItsTargetAfterSettlementCommitFailure(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var fault = new InvocationRetryFault();

        var first = await fixture.Open(
            services => services.AddSingleton(new ExecutionLimits { MaxCheckpointBytes = 18_000, VerifyCheckpointParts = true }), fault);

        var child = Workflow.Create<Data>("ResolutionRetryChild").Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<Data, object>([], "SavedResolutionInvocation")
            .Invoke<Data>(child.Name, 1, setup => setup.Input(data => data.Value).From(_ => new string('x', 100_000))
                .OnError(error => error.ThenRetry(new RetryPolicy(1)))).Build();

        var stopped = await first.Client.RunAsync(parent, new Data());
        var target = Assert.Single(await first.Client.GetPendingActivitiesAsync(stopped.InstanceId));
        fault.Before = InvocationRetryFault.Schedule;
        fault.Remaining = 3;

        await Assert.ThrowsAsync<WorkflowEngineException>(() => first.Client.ResolveAsync(stopped.InstanceId, target.InvocationId,
            ActivityResolution.Failed("observed", expectedPolicyAttemptId: target.PolicyAttemptId)));

        await first.DisposeAsync();

        var second = await fixture.Open(services =>
            services.AddSingleton(new ExecutionLimits { MaxCheckpointBytes = 18_000, VerifyCheckpointParts = true }));

        var saved = Record((await second.Store.GetWorkflowInstanceAsync(stopped.InstanceId))!);
        Assert.Equal(target.PolicyAttemptId, Assert.Single(saved.Attempts).Decision!.PolicyAttemptId);
        Assert.Equal(0, saved.ScheduledRetries);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, (await second.Client.RecoverAsync(stopped.InstanceId)).Status);
        var next = Assert.Single(await second.Client.GetPendingActivitiesAsync(stopped.InstanceId));
        Assert.NotEqual(target.PolicyAttemptId, next.PolicyAttemptId);
        Assert.Equal(1, Record((await second.Store.GetWorkflowInstanceAsync(stopped.InstanceId))!).ScheduledRetries);

        foreach (var decision in new[]
                 {
                     ActivityResolution.Failed("stale", expectedPolicyAttemptId: target.PolicyAttemptId),
                     ActivityResolution.CompletedInvocation(new Data(), expectedPolicyAttemptId: target.PolicyAttemptId)
                 })
            Assert.Equal(WorkflowErrorKind.InvalidState, (await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                second.Client.ResolveAsync(stopped.InstanceId, target.InvocationId, decision))).Kind);

        Assert.Single((await second.Client.GetTraceAsync(stopped.InstanceId)).Select(e => e.Payload).OfType<ActivityResolutionTrace>());
        Assert.Empty((await second.Store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: stopped.InstanceId))).Items);

        await second.Client.ResolveAsync(stopped.InstanceId, next.InvocationId,
            ActivityResolution.CompletedInvocation(new Data(), expectedPolicyAttemptId: next.PolicyAttemptId));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task InvocationReplacementPayloadAdmissionRollsBackRecordChildWaitBodyAndReceipt(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var body = System.Text.Encoding.UTF8.GetBytes("\"new-invocation-body\"");
        var reference = PayloadReference.Of(body);
        var missing = PayloadReference.Of("missing-dependency"u8.ToArray());

        var fault = new InvocationRetryFault
        {
            Remaining = 3,
            RejectPayload = commit => commit.WithPayloadBodies([
                new(reference, body, []),
                new(PayloadReference.Of("dependent-body"u8.ToArray()), "dependent-body"u8.ToArray(), [missing])
            ])
        };

        var first = await fixture.Open(fault: fault);
        var parent = await Definitions(fixture, new RetryPolicy(1));
        var id = await first.Client.StartAsync(parent, new Data());
        await fault.Exhausted.Task.WaitAsync(InvocationRetryProfile.Patience());
        await first.DisposeAsync();
        var second = await fixture.Open();
        var record = Record((await second.Store.GetWorkflowInstanceAsync(id))!);
        Assert.Single(record.Attempts);
        Assert.Equal(1, record.ScheduledRetries);
        var proposed = Assert.Single(fault.Captured!.ChildStarts);
        Assert.Null(await second.Store.GetWorkflowInstanceAsync(proposed.ChildInstanceId));
        Assert.Null(await second.Store.GetWaitAsync(id, proposed.ParentWaitId));
        Assert.Null(await second.Store.PayloadStore.GetAsync(reference));
        Assert.DoesNotContain(await second.Client.GetTraceAsync(id), entry => entry.Payload is InvocationTrace { AttemptNumber: 2 });
        await second.Client.RecoverAsync(id);
        await Success(second.Client, id);
        await InvocationTraceAssertions.PrunedAsync(second.Store, id);
        var final = await InvocationTraceAssertions.AttemptsAsync(second.Store, id, parent);
        Assert.Equal(proposed.ChildInstanceId, final[1].ChildInstanceId);
        Assert.Equal(2, final.Length);
        Assert.Single(await InvocationTraceAssertions.RetriesAsync(second.Store, id));
        var child = (await second.Store.GetWorkflowInstanceAsync(proposed.ChildInstanceId))!;
        Assert.NotEqual(proposed.ParentWaitId, child.ParentWaitId);
        Assert.NotNull(await second.Store.GetWaitAsync(id, child.ParentWaitId!));
    }

    internal static InvocationRecordState Record(WorkflowInstance saved) =>
        Assert.Single(ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords.Values);

    private static InvocationRecordState? RecordOrNull(WorkflowInstance saved) =>
        ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords.Values.SingleOrDefault();

    internal static async Task Success(IWorkflowClient client, string id, TimeSpan delay = default) => Assert.Equal(
        WorkflowExecutionStatus.Success,
        (await InvocationRetryProfile.Finished(client, id, delay)).Status);

    private static async Task<WorkflowDefinition> Definitions(InvocationRetryProfile fixture, RetryPolicy policy,
        bool waitOnSuccess = false)
    {
        var childBuilder = Workflow.Create<Data>("RecoveredRetryChild").Step<FailOnce>();
        var child = waitOnSuccess ? childBuilder.WaitFor<Signal>("finish").Build() : childBuilder.Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<Data, object>([], "RecoveredRetryInvocation")
            .Invoke<Data>(child.Name, 1, setup => setup.OnError(error => error.ThenRetry(policy))).Build();

        await fixture.Catalog.RegisterWorkflowAsync(parent);

        return parent;
    }

    public sealed class Data
    {
        public string Value { get; set; } = "";
        public int Cleanup { get; set; }
    }

    public sealed class Signal;

    public sealed class Ledger
    {
        public int Calls;
        public int Failures = 1;
    }

    public sealed class FailOnce(Ledger ledger) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref ledger.Calls) <= ledger.Failures)
                throw new InvalidOperationException("first child failed");

            return Task.CompletedTask;
        }
    }
}
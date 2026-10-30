using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task NewChildrenAndRetainedAttemptsSurviveStoreRoundTrips(string profile)
    {
        var ledger = new Ledger();
        await using var cluster = await ConformanceCluster.CreateAsync(profile, services => services.AddSingleton(ledger));
        var client = cluster.AddHost();
        var child = Workflow.Create<Data>("InvocationRetryChild").Step<FailOnce>().Build();
        await cluster.Registry.RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<Data, object>([], "InvocationRetryParent")
            .Invoke<Data>(child.Name, 1, WorkflowDefinitionValidator.GetContract(child), setup => setup
                .OnError<InvalidOperationException>(error => error.ThenRetry(new RetryPolicy(1)))).Build();

        var started = await client.RunAsync(parent, new Data());
        var completed = await InvocationRetryProfile.Finished(client, started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        var stored = (await cluster.GetInstanceAsync(started.InstanceId))!;
        await InvocationTraceAssertions.PrunedAsync(cluster.NewRepository(), started.InstanceId);
        var attempts = await InvocationTraceAssertions.AttemptsAsync(cluster.NewRepository(), started.InstanceId, parent);
        Assert.Equal(2, attempts.Length);
        Assert.NotEqual(attempts[0].ChildInstanceId, attempts[1].ChildInstanceId);
        Assert.Equal(WorkflowStatus.Failed, (await cluster.GetInstanceAsync(attempts[0].ChildInstanceId!))!.Status);
        Assert.Equal(WorkflowStatus.Completed, (await cluster.GetInstanceAsync(attempts[1].ChildInstanceId!))!.Status);
        Assert.Single(await InvocationTraceAssertions.RetriesAsync(cluster.NewRepository(), started.InstanceId));
        Assert.Equal(2, ledger.Calls);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CancellationDuringDurableBackoffStartsNoReplacement(string profile)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        await using var cluster = await ConformanceCluster.CreateAsync(profile, time: clock);
        var client = cluster.AddHost();
        var child = Workflow.Create<Data>("AlwaysFailedChild").Step<AlwaysFail>().Build();
        await cluster.Registry.RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<Data, object>([], "CancelInvocationRetry")
            .Invoke<Data>(child.Name, 1, setup => setup.OnError(error => error.ThenRetry(new RetryPolicy(
                TimeSpan.FromHours(1), TimeSpan.FromHours(1), 3, 1)))).Build();

        var id = await client.StartAsync(parent, new Data());

        await InvocationRetryProfile.Until(cluster.NewRepository(), id, saved =>
            ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords.Values.Any(record =>
                record.RetryAfterUtc != null && record.Attempts[0].ChildStatus == WorkflowStatus.Failed));

        await client.CancelAsync(id, new CancellationReason { ReasonCode = "cancel-between-attempts" });
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await InvocationRetryProfile.Finished(client, id)).Status);
        clock.Advance(TimeSpan.FromHours(2));
        await cluster.RecoverOnceAsync();
        var saved = (await cluster.GetInstanceAsync(id))!;
        await InvocationTraceAssertions.PrunedAsync(cluster.NewRepository(), id);
        var attempts = await InvocationTraceAssertions.AttemptsAsync(cluster.NewRepository(), id, parent);
        var attempt = Assert.Single(attempts);
        Assert.Null(attempt.RetryAfterUtc);
        Assert.Equal("Cancelled", attempt.Disposition);
        Assert.Single((await cluster.NewRepository().QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ReopenedHostKeepsTheAcceptedMaximumDurationDeadline(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var child = Workflow.Create<Data>("MaximumDelayChild").Step<AlwaysFail>().Build();

        var parent = new WorkflowBuilder<Data, object>([], "MaximumInvocationRetry")
            .Invoke<Data>(child.Name, 1, setup => setup.OnError(error => error.ThenRetry(new RetryPolicy(
                CheckpointCommit.MaxDuration, CheckpointCommit.MaxDuration, 1, 1)))).Build();

        await fixture.Catalog.RegisterWorkflowAsync(child);
        await fixture.Catalog.RegisterWorkflowAsync(parent);
        var first = await fixture.Open(holdRetries: true);
        var id = await first.Client.StartAsync(parent, new Data());
        var parked = await first.Parked(id);
        var deadline = Assert.Single(ExecutionCheckpoint.Read(parked.ExecutionStateJson).InvocationRecords.Values).RetryAfterUtc!.Value;
        var retry = Assert.Single(await first.Client.GetTraceAsync(id), entry => entry.EntryType == TraceEntryType.InvocationPolicyApplied);
        Assert.Equal(retry.Timestamp + CheckpointCommit.MaxDuration, deadline);
        await first.DisposeAsync();
        var second = await fixture.Open();

        var record = Assert.Single(ExecutionCheckpoint.Read((await second.Store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson)
            .InvocationRecords.Values);

        Assert.Equal(deadline, record.RetryAfterUtc);
        Assert.Equal(parked.RunnableAtUtc, (await second.Store.GetWorkflowInstanceAsync(id))!.RunnableAtUtc);
        Assert.Single(record.Attempts);
        await second.Client.CancelAsync(id, new CancellationReason { ReasonCode = "done" });
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await InvocationRetryProfile.Finished(second.Client, id)).Status);
    }

    public sealed class Data
    {
    }

    public sealed class Ledger
    {
        public int Calls;
    }

    public sealed class FailOnce(Ledger ledger) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref ledger.Calls) == 1)
                throw new InvalidOperationException("first child failed");

            return Task.CompletedTask;
        }
    }

    public sealed class AlwaysFail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("failed child");
    }
}
using System.Text.Json;
using System.Data.Common;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.ExecutionTests.InvocationRetryTests;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "D-18")]
[Trait("Feature", "durability")]
public sealed class InvocationRetryDurabilityTests
{
    [Fact]
    public async Task FailedScheduleCommitReconsumesTheSameChildOutcomeAndSpendsTheBudgetOnce()
    {
        var store = new FaultStore { Before = commit => commit.ReadTrunk().WorkflowName == "RetryScheduleCrash" &&
            commit.Parts.Any(part => part.Key.StartsWith("v/") &&
            JsonSerializer.Deserialize<InvocationRecordState>(part.Json.InlineText)!.ScheduledRetries == 1), Failures = 3 };
        var ledger = new Ledger();
        await using var services = Services(store, ledger: ledger);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "RetryScheduleCrash")
            .Invoke<Child, InvocationRetryTests.ChildData>(setup => setup.Input(data => data.Value).From(_ => "input")
                .OnError(error => error.ThenRetry(new RetryPolicy(1)))).Build();
        var id = await client.StartAsync(definition, new Data());
        await Until(() => Task.FromResult(Volatile.Read(ref store.Failures) == 0));
        var before = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);
        Assert.Single(Assert.Single(before.InvocationRecords.Values).Attempts);
        Assert.Equal(0, Assert.Single(before.InvocationRecords.Values).ScheduledRetries);
        await client.RecoverAsync(id);
        var completed = await client.AwaitAsync(id, WorkflowAwaitCondition.Finished).WaitAsync(ChildRuns.Patience);
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        var record = Assert.Single(ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson).InvocationRecords.Values);
        Assert.Equal(2, record.Attempts.Count);
        Assert.Equal(1, record.ScheduledRetries);
        Assert.Equal(2, ledger.Calls);
    }

    [Fact]
    public async Task LostRetryStartAcknowledgementKeepsItsChildAndImmutableReceiptAfterRemoval()
    {
        var store = new FaultStore { After = commit => commit.ReadTrunk().WorkflowName == "RetryStartCrash" &&
            commit.ChildStarts.Count > 0 && commit.Parts.Any(part => part.Key.StartsWith("v/") &&
            JsonSerializer.Deserialize<InvocationRecordState>(part.Json.InlineText)!.Attempts.Any(attempt => attempt.Number == 2)), Failures = 3 };
        await using var services = Services(store, ledger: new Ledger());
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "RetryStartCrash")
            .Invoke<Child, InvocationRetryTests.ChildData>(setup => setup.Input(data => data.Value).From(_ => "input")
                .OnError(error => error.ThenRetry(new RetryPolicy(1)))).Build();
        var id = await client.StartAsync(definition, new Data());
        await Until(() => Task.FromResult(Volatile.Read(ref store.Failures) == 0));
        var receipt = store.Receipt!;
        var start = store.LostCommit!;
        var record = Assert.Single(ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson).InvocationRecords.Values);
        Assert.Equal(2, record.Attempts.Count);
        var childId = record.Attempts[1].ChildInstanceId!;
        await client.RecoverAsync(id);
        await client.RecoverAsync(childId);
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await client.AwaitAsync(id, WorkflowAwaitCondition.Finished).WaitAsync(ChildRuns.Patience)).Status);
        Assert.Equal(2, (await store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items.Count);
        await store.DeleteWorkflowInstanceAsync(childId);
        var repeated = await store.CommitWorkflowInstanceAsync(start);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, repeated.Status);
        Assert.Equal(receipt.Revision, repeated.Revision);
        Assert.Equal(receipt.MadeRunnable, repeated.MadeRunnable);
        Assert.Null(await store.GetWorkflowInstanceAsync(childId));
    }

    [Fact]
    public async Task SavedOperatorDecisionRecoversItsOriginalTargetAfterSettlementFailure()
    {
        var store = new FaultStore();
        await using var services = Services(store, maxBytes: 18_000);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "DecisionSettlementCrash")
            .Invoke<RequiredChild, InvocationRetryTests.ChildData>(setup => setup.Input(data => data.Value).From(_ => new string('x', 100_000))
                .OnError(error => error.ThenRetry(new RetryPolicy(1)))).Build();
        var stopped = await client.RunAsync(definition, new Data());
        var pending = Assert.Single(await client.GetPendingActivitiesAsync(stopped.InstanceId));
        store.Before = commit => commit.ReadTrunk().WorkflowName == definition.Name && commit.Parts.Any(part =>
            part.Key.StartsWith("v/") &&
            JsonSerializer.Deserialize<InvocationRecordState>(part.Json.InlineText)!.ScheduledRetries == 1);
        store.Failures = 3;
        await Assert.ThrowsAsync<WorkflowEngineException>(() => client.ResolveAsync(stopped.InstanceId, pending.InvocationId,
            ActivityResolution.Failed("observed", expectedPolicyAttemptId: pending.PolicyAttemptId)));
        var saved = ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(stopped.InstanceId))!.ExecutionStateJson);
        var decision = Assert.Single(saved.InvocationRecords.Values).Attempts[0].Decision;
        Assert.Equal(pending.PolicyAttemptId, decision!.PolicyAttemptId);
        var recovered = await client.RecoverAsync(stopped.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        var next = Assert.Single(await client.GetPendingActivitiesAsync(stopped.InstanceId));
        Assert.NotEqual(pending.PolicyAttemptId, next.PolicyAttemptId);
        var record = Assert.Single(ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(stopped.InstanceId))!.ExecutionStateJson).InvocationRecords.Values);
        Assert.Equal(1, record.ScheduledRetries);
        Assert.Equal(2, record.Attempts.Count);
        Assert.Single((await client.GetTraceAsync(stopped.InstanceId)).Where(entry => entry.Payload is ActivityResolutionTrace));
    }

    [Fact]
    public async Task TerminationClosesAnExternalRecordWithoutReadingItsUnavailablePayload()
    {
        var store = new BrokenRecordStore();
        await using var services = Services(store, payloadThreshold: 128);
        var child = Workflow.Create<Data>("WaitingRetryChild").Step<Caught>().WaitFor<Go>("go").Build();
        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "TerminateRetryRecord")
            .Invoke<Data>(child.Name, 1, setup => setup.CancelWithParent(false).OnError(error => error.ThenRetry(new RetryPolicy(2)))).Build();
        var started = await client.RunAsync(definition, new Data());
        await store.WaitingInstanceAsync(child.Name, "go");
        var before = (await store.GetWorkflowInstanceAsync(started.InstanceId))!;
        var part = Assert.Single(before.SavedParts!, part => part.Key.StartsWith("v/")).Value;
        Assert.NotNull(part.Reference);
        store.Payloads.Broken = part.Reference;
        var operations = services.GetRequiredService<IWorkflowOperations>();
        await operations.TerminateAsync(started.InstanceId, "stop-subtree");
        var after = (await store.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal(WorkflowStatus.Terminated, after.Status);
        Assert.Equal(0, store.Payloads.BrokenReads);
        Assert.Equal(before.SavedParts, after.SavedParts);
        Assert.Equal("Terminated", ExecutionCheckpoint.ReadTrunk(after.ExecutionStateJson).InvocationClosure!.Disposition);
        store.Payloads.Broken = null;
        var record = Assert.Single((await operations.InspectAsync(started.InstanceId))!.Invocations);
        Assert.Equal("Terminated", record.Disposition);
        Assert.Equal("Ended", Assert.Single(record.Attempts).Phase);
        Assert.Contains(await client.GetTraceAsync(started.InstanceId), entry => entry.Payload is InvocationTrace { Disposition: "Terminated" });
    }

    private static async Task Until(Func<Task<bool>> predicate)
    {
        using var patience = new CancellationTokenSource(ChildRuns.Patience);
        while (!await predicate()) await Task.Delay(10, patience.Token);
    }

    private sealed class FaultStore : InMemoryWorkflowStateRepository
    {
        public Func<CheckpointCommit, bool>? Before;
        public Func<CheckpointCommit, bool>? After;
        public int Failures;
        public WorkflowCommitResult? Receipt;
        public CheckpointCommit? LostCommit;
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            if (Failures > 0 && Before?.Invoke(commit) == true && Interlocked.Decrement(ref Failures) >= 0)
                throw new IOException("injected before commit");
            var receipt = await base.CommitWorkflowInstanceAsync(commit);
            if (Failures > 0 && After?.Invoke(commit) == true && Interlocked.Decrement(ref Failures) >= 0)
            {
                Receipt ??= receipt; LostCommit ??= commit;
                throw new IOException("injected lost acknowledgement");
            }
            return receipt;
        }
    }

    private sealed class BrokenRecordStore : InMemoryWorkflowStateRepository
    {
        private BrokenPayloadStore? _payloads;
        public BrokenPayloadStore Payloads => _payloads ??= new(base.PayloadStore);
        public override IPayloadStore PayloadStore => Payloads;
    }
    private sealed class BrokenPayloadStore(IPayloadStore inner) : IPayloadStore
    {
        public PayloadReference? Broken;
        public int BrokenReads;
        public ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> body, IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default) => inner.PutAsync(body, dependencies, ct);
        public ValueTask<ReadOnlyMemory<byte>?> GetAsync(PayloadReference reference, CancellationToken ct = default)
        {
            if (reference != Broken) return inner.GetAsync(reference, ct);
            BrokenReads++;
            return new((ReadOnlyMemory<byte>?)null);
        }
        public ValueTask<bool> CollectAsync(PayloadReference reference, TimeSpan grace, CancellationToken ct = default) => inner.CollectAsync(reference, grace, ct);
        public ValueTask<IReadOnlyList<PayloadReference>> FindCollectionCandidatesAsync(int limit, TimeSpan grace, CancellationToken ct = default) => inner.FindCollectionCandidatesAsync(limit, grace, ct);
        public ValueTask InTransactionAsync(Func<IPayloadTransaction, CancellationToken, ValueTask> action, CancellationToken ct = default) => inner.InTransactionAsync(action, ct);
        public IPayloadTransaction Enlist(DbTransaction transaction) => inner.Enlist(transaction);
    }
}

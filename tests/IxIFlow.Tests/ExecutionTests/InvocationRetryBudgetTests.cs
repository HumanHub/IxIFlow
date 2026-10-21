using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static IxIFlow.Tests.ExecutionTests.CommitBudgetTests;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryBudgetTests
{
    [Fact]
    public async Task ChildInvocationRetainsHistoryWithinItsCommitBudget()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        var store = new RecordingStore(clock);
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IWorkflowStateRepository>(store);
        collection.AddSingleton<TimeProvider>(clock);
        collection.AddIxIFlow(options => options.HostName = "invocation-budget");
        collection.AddTransient<WorkActivity>();
        using var services = collection.BuildServiceProvider();
        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(
            Workflow.Create<BudgetData>("BudgetChild").Step<WorkActivity>().Build());
        var definition = Workflow.Create<BudgetData>("BudgetParent")
            .Step<WorkActivity>().Invoke<BudgetData>("BudgetChild", 1).Step<WorkActivity>().Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunToEndAsync(definition, new BudgetData());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var commits = store.Commits.ToArray();
        Assert.Equal(12, commits.Length);
        Assert.True(commits.Max(commit => commit.StateBytes()) < 2600,
            $"Largest commit: {commits.Max(commit => commit.StateBytes())} bytes");
        var saved = (await store.GetWorkflowInstanceAsync(result.InstanceId))!;
        var record = Assert.Single(ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords.Values);
        var attempt = Assert.Single(record.Attempts);
        Assert.Equal("Returned", record.Disposition);
        Assert.NotNull(attempt.ChildInstanceId);
        Assert.NotNull(attempt.StartedAtUtc);
        Assert.NotNull(attempt.EndedAtUtc);
        Assert.Equal(WorkflowStatus.Completed, attempt.ChildStatus);
    }

    [Fact]
    public void ChildInvocationRestoresItsSavedResolutionAndAttemptHistory()
    {
        var now = new DateTime(2026, 10, 22, 5, 0, 0, DateTimeKind.Utc);
        var definition = new WorkflowBuilder<BudgetData, object>([], "RestoreInvocation")
            .Invoke<BudgetData>("BudgetChild", 1).Build();
        var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition, new BudgetData(), new(), now);
        var continuation = Assert.Single(checkpoint.Continuations);
        var logicalId = InvocationRecords.LogicalId(instance, continuation, "root/0");
        var decision = new ActivityResolutionState { Kind = ActivityResolutionKind.Completed,
            PolicyAttemptId = InvocationRecords.Identity("policy-attempt", logicalId, "2"),
            Note = "observed", DecidedBy = "operator",
            InvocationResult = SerializedValue.From(new BudgetData { Count = 7 }) };
        var record = new InvocationRecordState { Id = logicalId, StepId = "root/0",
            ContinuationId = continuation.Id, FrameActivationId = continuation.Stack[0].ActivationId,
            WorkflowName = "BudgetChild", WorkflowVersion = 1, StartedAtUtc = now,
            DataType = WorkflowTypeIdentity.SavedName(typeof(BudgetData)),
            ScheduledRetries = 1, SelectedPolicyIndex = 0,
            Attempts = [new() { Number = 1, PreparedAtUtc = now, EndedAtUtc = now, Host = "first",
                    Phase = "Ended", Failure = SerializedException.From(new InvalidOperationException("input failed")),
                    Diagnostic = new("InputFailure", "diagnostic"), Disposition = "Retry",
                    SelectedPolicyIndex = 0, Action = StepErrorAction.Retry, RetryAfterUtc = now.AddSeconds(1) },
                new() { Number = 2, PreparedAtUtc = now.AddSeconds(1), Host = "second",
                    Phase = "NeedsResolution", Decision = decision }] };
        checkpoint.InvocationRecords.Add(logicalId, record);
        continuation.InvocationRecordId = logicalId;
        continuation.PendingActivity = new() { Id = logicalId, StepId = record.StepId,
            PolicyAttemptId = decision.PolicyAttemptId, ChildDataType = record.DataType, Resolution = decision };
        InvocationRecords.PrepareCommit(checkpoint, instance, now.AddSeconds(2));
        var stored = StoredCheckpoint.Split(checkpoint, StoredJson.Inline("{}"));
        var reopened = ExecutionCheckpoint.Read(StoredCheckpoint.AssembleState(stored.Trunk, stored.Parts));
        var history = Assert.Single(reopened.InvocationRecords.Values);
        Assert.Equal(record.StepId, history.StepId);
        Assert.Equal(record.ContinuationId, history.ContinuationId);
        Assert.Equal(1, history.ScheduledRetries);
        var failed = history.Attempts[0];
        Assert.Equal("input failed", failed.Failure!.Message);
        Assert.Equal("diagnostic", failed.Diagnostic!.DiagnosticId);
        Assert.Equal(now, failed.EndedAtUtc);
        Assert.Equal(now.AddSeconds(1), failed.RetryAfterUtc);
        Assert.Equal(0, failed.SelectedPolicyIndex);
        Assert.Equal(StepErrorAction.Retry, failed.Action);
        Assert.Null(failed.ChildInstanceId);
        var attempt = history.Attempts[1];
        Assert.Equal(decision.PolicyAttemptId, attempt.Id);
        Assert.Equal(InvocationRecords.Identity("child-instance", logicalId, "2"), attempt.ProposedChildInstanceId);
        Assert.Null(attempt.ChildInstanceId);
        var pending = Assert.Single(reopened.Continuations).PendingActivity!;
        Assert.Equal(logicalId, pending.Id);
        Assert.Equal(record.StepId, pending.StepId);
        Assert.Equal(now, pending.StartedAtUtc);
        Assert.Equal(record.DataType, pending.ChildDataType);
        Assert.Equal(attempt.ProposedChildInstanceId, pending.ChildWorkflowId);
        Assert.Equal(attempt.Id, pending.PolicyAttemptId);
        Assert.Equal("observed", pending.Resolution!.Note);
        Assert.Equal("operator", pending.Resolution.DecidedBy);
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(7, Assert.IsType<BudgetData>(pending.Resolution.InvocationResult!.Read(services)).Count);
    }

    private sealed class RecordingStore(TimeProvider clock) : FaultInjectingRepository(clock)
    {
        public ConcurrentQueue<CheckpointCommit> Commits { get; } = new();
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (result.Status == WorkflowCommitStatus.Applied) Commits.Enqueue(commit);
            return result;
        }
    }
}

using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryCommitScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ChildStartRecordAndWaitShareTheReceiptAndStaleOwnersCannotStartTheNextAttempt(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var definition = new WorkflowBuilder<Data, object>([], "RetryCommitParent").Invoke<Data>("RetryCommitChild", 1).Build();
        var childDefinition = Workflow.Create<Data>("RetryCommitChild").Build();
        var (parent, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
        await store.SeedAsync(parent);
        parent = (await store.GetWorkflowInstanceAsync(parent.InstanceId))!;
        checkpoint = ExecutionCheckpoint.Read(parent.ExecutionStateJson);
        checkpoint.RootData = new Data();
        var continuation = Assert.Single(checkpoint.Continuations);
        var logicalId = InvocationRecords.LogicalId(parent, continuation, "root/0");
        var childId = InvocationRecords.Identity("child-instance", logicalId, "1");
        var attempt = new InvocationAttemptState { Id = InvocationRecords.Identity("policy-attempt", logicalId, "1"),
            Number = 1, PreparedAtUtc = DateTime.UtcNow, ProposedChildInstanceId = childId, Phase = "WaitingChild" };
        var record = new InvocationRecordState { Id = logicalId, StepId = "root/0", WorkflowName = childDefinition.Name,
            WorkflowVersion = 1, ContinuationId = continuation.Id, FrameActivationId = continuation.Stack[0].ActivationId,
            StartedAtUtc = attempt.PreparedAtUtc, Attempts = [attempt] };
        checkpoint.InvocationRecords.Add(logicalId, record);
        continuation.InvocationRecordId = logicalId;
        continuation.PendingActivity = new ActivityInvocationState { Id = logicalId, StepId = record.StepId,
            PolicyAttemptId = attempt.Id, ChildWorkflowId = childId };
        var start = WorkflowInstances.Start(childDefinition, new Data(), parent, childId, Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        using var services = new ServiceCollection().BuildServiceProvider();
        new ContinuationTransitions(services, TimeProvider.System).AwaitChild(checkpoint, continuation, record.StepId, start);
        parent.Status = WorkflowStatus.Suspended;
        ExecutionCheckpointWriter.Prepare(parent, checkpoint, checkpoint.RootData, scopes, DateTime.UtcNow);
        var grant = await StoreTestSupport.LeaseAsync(store, parent.InstanceId);
        var opened = checkpoint.Inbox.WaitChanges(checkpoint).Opened;
        var commit = CheckpointCommit.Create(parent, grant.Epoch, parent.Revision, childStarts: [start], openedWaits: opened);
        var receipt = await store.CommitWorkflowInstanceAsync(commit);
        Assert.Equal(WorkflowCommitStatus.Applied, receipt.Status);
        var saved = (await store.GetWorkflowInstanceAsync(parent.InstanceId))!;
        Assert.Equal(childId, Assert.Single(ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords.Values).Attempts[0].ChildInstanceId);
        Assert.Single(await store.GetOpenWaitsAsync(new(InstanceId: parent.InstanceId, Kind: WaitKind.Child)));
        Assert.Single((await store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: parent.InstanceId))).Items);

        // A stale owner has a fully prepared attempt 2, but neither that record nor its child may publish.
        var nextChildId = InvocationRecords.Identity("child-instance", logicalId, "2");
        var next = WorkflowInstances.Start(childDefinition, new Data(), saved, nextChildId, Guid.NewGuid().ToString("N"), DateTime.UtcNow);
        var stale = CheckpointCommit.Create(saved, grant.Epoch - 1, saved.Revision,
            openedWaits: [new OpenedWait(next.ParentWaitId, WaitKind.Child, "", ChildCompleted.EventType, null)], childStarts: [next]);
        Assert.Equal(WorkflowCommitStatus.StaleEpoch, (await store.CommitWorkflowInstanceAsync(stale)).Status);
        Assert.Null(await store.GetWorkflowInstanceAsync(nextChildId));
        Assert.Equal(saved.Revision, (await store.GetWorkflowInstanceAsync(parent.InstanceId))!.Revision);

        var child = (await store.GetWorkflowInstanceAsync(childId))!;
        var childGrant = await StoreTestSupport.LeaseAsync(store, childId);
        child.Status = WorkflowStatus.Failed;
        var childState = ExecutionCheckpoint.Read(child.ExecutionStateJson);
        childState.UnhandledError = SerializedException.Capture(new InvalidOperationException("old child failed"), new WorkflowScopeCatalog(new WorkflowDefinition()), new ExecutionCheckpoint(), null, newOccurrence: true);
        child.ExecutionStateJson = JsonSerializer.Serialize(childState);
        var ended = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(child, childGrant.Epoch, child.Revision,
            parentNotice: ChildCompleted.NoticeOf(child, childState)));
        Assert.Equal(WorkflowCommitStatus.Applied, ended.Status);
        await store.DeleteWorkflowInstanceAsync(childId);
        var replay = await store.CommitWorkflowInstanceAsync(commit);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, replay.Status);
        Assert.Equal(receipt.Revision, replay.Revision);
        Assert.Equal(receipt.MadeRunnable, replay.MadeRunnable);
        Assert.Null(await store.GetWorkflowInstanceAsync(childId));
        Assert.Single(Assert.Single(ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(parent.InstanceId))!.ExecutionStateJson)
            .InvocationRecords.Values).Attempts);
    }

    public sealed class Data;
}

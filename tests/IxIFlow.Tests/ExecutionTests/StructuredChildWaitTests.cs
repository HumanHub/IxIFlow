using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// A child workflow is an instance of its own (D-06, D-08, D-09, decision D-17): the parent's commit starts
/// it with the parent's wait for it, the child takes its own events, and its terminal commit puts its
/// completion in the parent's inbox. Nothing projects, forwards or polls; a cancelled parent asks its child
/// to cancel and waits for it to end.
/// </summary>
[Trait("Feature", "children")]
public sealed class StructuredChildWaitTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task NonpersistentParentCannotInvokeAWaitingChild()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var child = Workflow.Create<ChildData>("PersistedChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("NonpersistentParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("PersistedChild", 1)
            .Build();

        await EngineAssert.FailsAsync(WorkflowErrorKind.InvalidRequest, () =>
            services.GetRequiredService<IWorkflowClient>()
                .RunAsync(parent, new ParentData(),
                    new WorkflowOptions { PersistState = false }));
        Assert.Empty(await repository.GetWorkflowInstancesByNameAsync(parent.Name));
        Assert.Empty(await repository.GetWorkflowInstancesByNameAsync(child.Name));
    }

    [Fact]
    [Trait("Spec", "D-06")]
    public async Task AChildRunsAsItsOwnInstanceAndItsCompletionWakesItsParent()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("OwnInstanceChild"));
        var parent = Workflow.Create<ParentData>("OwnInstanceParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("OwnInstanceChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();

        var started = await engine.RunAsync(parent, new ParentData());

        // The parent is idle at its wait for the child; nothing checks the child on a timer.
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var childId = await ChildIdAsync(services, started.InstanceId);
        var savedParent = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        var wait = Assert.Single(ExecutionCheckpoint.Read(savedParent.ExecutionStateJson).Waits);
        Assert.Equal((WaitKind.Child, childId), (wait.TableKind, wait.ChildWorkflowId));
        Assert.Null(savedParent.RunnableAtUtc);
        var savedChild = (await repository.GetWorkflowInstanceAsync(childId))!;
        Assert.Equal((started.InstanceId, wait.Id), (savedChild.ParentInstanceId, savedChild.ParentWaitId));

        await ChildWaitingAsync(services, childId, "approval");
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await engine.ResumeAsync(childId, "approval", new Approval())).Status);
        var finished = await FinishedAsync(engine, started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.True(Assert.IsType<ParentData>(finished.WorkflowData).ChildApproved);
    }

    [Fact]
    [Trait("Spec", "D-06")]
    public async Task AnEventForTheChildSentToItsParentFindsNoWait()
    {
        using var services = Services();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("AddressedChild"));
        var parent = Workflow.Create<ParentData>("AddressedChildParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("AddressedChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        await ChildWaitingAsync(services, childId, "approval");

        // The child's events go to the child: the parent's wait takes only the child's completion.
        EventAssert.Rejected(await engine.ResumeAsync(started.InstanceId, "approval", new Approval()),
            EventRejection.NoOpenWait);
        EventAssert.Consumed(await engine.ResumeAsync(childId, "approval", new Approval()));
        Assert.True(Assert.IsType<ParentData>((await FinishedAsync(engine, started.InstanceId)).WorkflowData)
            .ChildApproved);
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public async Task AParentWaitsWhileItsChildNeedsResolution()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("UnresolvedChild"));
        var parent = Workflow.Create<ParentData>("UnresolvedChildParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("UnresolvedChild", 1)
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        await ChildWaitingAsync(services, childId, "approval");
        var savedChild = (await repository.GetWorkflowInstanceAsync(childId))!;
        savedChild.Status = WorkflowStatus.NeedsResolution;
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(savedChild, 0, savedChild.Revision,
                Guid.NewGuid().ToString("N")))).Status);

        // The child sends nothing until an operator moves it; the parent stays at its wait, with nothing to
        // recover.
        await engine.RecoverAsync(started.InstanceId);
        var savedParent = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal(WorkflowStatus.Suspended, savedParent.Status);
        var wait = Assert.Single(ExecutionCheckpoint.Read(savedParent.ExecutionStateJson).Waits);
        Assert.Equal(childId, wait.ChildWorkflowId);
        Assert.Empty(await engine.GetPendingActivitiesAsync(started.InstanceId));
        EventAssert.Rejected(await engine.ResumeAsync(started.InstanceId, "approval", new Approval()),
            EventRejection.NoOpenWait);
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public async Task CancellingAParentCancelsItsWaitingChildAndRunsFinallyOnceTheChildHasEnded()
    {
        using var services = Services();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("CancellableChild"));
        var parent = Workflow.Create<ParentData>("CancellableParent")
            .Step<NoopActivity>()
            .Try(body => body.Invoke<ChildData>("CancellableChild", 1))
            .Finally(body => body.Step<MarkFinallyActivity>(step => step
                .Output(activity => activity.Ran).To(ctx => ctx.WorkflowData.FinallyRan)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        await ChildWaitingAsync(services, childId, "approval");

        await engine.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "operator" });
        var cancelled = await FinishedAsync(engine, started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.True(Assert.IsType<ParentData>(cancelled.WorkflowData).FinallyRan);
        Assert.Equal(WorkflowStatus.Cancelled,
            (await services.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(childId))!.Status);
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public async Task ACancelledParentWaitsForAChildAnotherHostOwns()
    {
        using var services = Services();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("RemotelyOwnedChild"));
        var parent = Workflow.Create<ParentData>("ParentWithRemotelyOwnedChild")
            .Step<NoopActivity>()
            .Try(body => body.Invoke<ChildData>("RemotelyOwnedChild", 1))
            .Finally(body => body.Step<MarkFinallyActivity>(step => step
                .Output(activity => activity.Ran).To(ctx => ctx.WorkflowData.FinallyRan)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        await ChildWaitingAsync(services, childId, "approval");
        var foreignToken = Guid.NewGuid().ToString("N");
        Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(childId, foreignToken, TimeSpan.FromMinutes(1)));

        var waiting = await engine.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "operator" });

        // The parent asked its child to cancel and waits for it, with nothing to poll.
        Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);
        Assert.Null((await repository.GetWorkflowInstanceAsync(started.InstanceId))!.RunnableAtUtc);
        Assert.NotNull(await repository.GetCancellationRequestAsync(childId));
        Assert.True(await repository.ReleaseExecutionLeaseAsync(childId, foreignToken));
        // The host that was told the child is runnable may already be running it.
        await engine.RecoverAsync(childId);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await FinishedAsync(engine, childId)).Status);
        var cancelled = await FinishedAsync(engine, started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.True(Assert.IsType<ParentData>(cancelled.WorkflowData).FinallyRan);
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public async Task ACancelledParentEndsWhenItsChildsCleanupFails()
    {
        using var services = Services();
        var child = Workflow.Create<ChildData>("FailingCancellationChild")
            .Step<NoopActivity>()
            .Try(body => body.WaitFor<Approval>("approval"))
            .Finally(body => body.Step<ThrowChildActivity>())
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("ParentWithFailingCancellationChild")
            .Step<NoopActivity>()
            .Invoke<ChildData>("FailingCancellationChild", 1)
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        await ChildWaitingAsync(services, childId, "approval");

        await engine.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "operator" });
        var cancelled = await FinishedAsync(engine, started.InstanceId);

        // The child's failure reaches a parent that is unwinding, which discards it.
        Assert.Equal(WorkflowStatus.Failed, (await repository.GetWorkflowInstanceAsync(childId))!.Status);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.Empty(await engine.GetPendingActivitiesAsync(started.InstanceId));
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public async Task ACancelledParentWaitsForAChildThatNeedsResolutionBeforeFinally()
    {
        // The child's activity runs, but its outcome is never saved, so an operator must decide it.
        var probe = new ActiveChildProbe();
        var store = new FaultInjectingRepository
        {
            FailCommit = instance => instance.WorkflowName == "UnsettledChild" && probe.Started.Task.IsCompleted
        };
        probe.Release.TrySetResult();
        using var services = Services(store, probe);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<ChildData>("UnsettledChild").Step<ActiveChildActivity>().Build());
        var parent = Workflow.Create<ParentData>("UnsettledChildParent")
            .Step<NoopActivity>()
            .Try(body => body.Invoke<ChildData>("UnsettledChild", 1))
            .Finally(body => body.Step<MarkFinallyActivity>(step => step
                .Output(activity => activity.Ran).To(ctx => ctx.WorkflowData.FinallyRan)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        // The run that lost the outcome gives up after its retries; the next one finds the activity unsettled.
        WorkflowExecutionResult recovered;
        using (var patience = new CancellationTokenSource(Patience))
        {
            while (store.FailedCommits < 3)
                await Task.Delay(10, patience.Token);
            store.FailCommit = _ => false;
            while ((recovered = await engine.RecoverAsync(childId)).Status == WorkflowExecutionStatus.Running)
                await Task.Delay(10, patience.Token);
        }
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);

        await engine.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "operator" });
        await engine.AwaitAsync(childId, WorkflowAwaitCondition.Idle).WaitAsync(Patience);

        // The child keeps its unresolved activity through the cancellation, and its parent waits for it.
        Assert.Equal(WorkflowStatus.NeedsResolution, (await store.GetWorkflowInstanceAsync(childId))!.Status);
        var waitingParent = (await store.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal(WorkflowStatus.Suspended, waitingParent.Status);
        Assert.False(JsonSerializer.Deserialize<ParentData>(waitingParent.WorkflowDataJson)!.FinallyRan);
        var pending = Assert.Single(await engine.GetPendingActivitiesAsync(childId));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await engine.ResolveAsync(childId, pending.InvocationId,
            ActivityResolution.Completed(new Dictionary<string, object?>()))).Status);
        var cancelled = await FinishedAsync(engine, started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.True(Assert.IsType<ParentData>(cancelled.WorkflowData).FinallyRan);
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public async Task AChildInvokedInsideFinallyRunsWhileItsParentIsCancelled()
    {
        using var services = Services();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<ChildData>("CancellationCleanupChild")
                .Step<ApproveActivity>(step => step
                    .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
                .Build());
        var parent = Workflow.Create<ParentData>("CancellationCleanupParent")
            .Step<NoopActivity>()
            .Try(body => body.WaitFor<Approval>("approval"))
            .Finally(body => body.Invoke<ChildData>("CancellationCleanupChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        await engine.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "operator" });
        var cancelled = await FinishedAsync(engine, started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.True(Assert.IsType<ParentData>(cancelled.WorkflowData).ChildApproved);
        var child = Assert.Single(await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstancesByNameAsync("CancellationCleanupChild"));
        Assert.Equal(WorkflowStatus.Completed, child.Status);
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public async Task AChildInsideFinallyTakesItsOwnEventsWhileItsParentIsCancelled()
    {
        using var services = Services();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            ApprovingChild("WaitingCleanupChild", "cleanup"));
        var parent = Workflow.Create<ParentData>("WaitingCleanupParent")
            .Step<NoopActivity>()
            .Try(body => body.WaitFor<Approval>("start"))
            .Finally(body => body.Invoke<ChildData>("WaitingCleanupChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());

        var cancelling = await engine.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "operator" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, cancelling.Status);
        var childId = Assert.Single(await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstancesByNameAsync("WaitingCleanupChild")).InstanceId;
        await ChildWaitingAsync(services, childId, "cleanup");
        EventAssert.Consumed(await engine.ResumeAsync(childId, "cleanup", new Approval()));

        var completed = await FinishedAsync(engine, started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, completed.Status);
        Assert.True(Assert.IsType<ParentData>(completed.WorkflowData).ChildApproved);
    }

    [Fact]
    [Trait("Spec", "D-06")]
    public async Task AChildWithSeveralWaitsTakesEachInItsOwnInbox()
    {
        using var services = Services();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<ChildData>("TwoWaitChild")
                .Step<NoopActivity>()
                .WaitFor<Approval>("first")
                .WaitFor<Approval>("second")
                .Step<ApproveActivity>(step => step
                    .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
                .Build());
        var parent = Workflow.Create<ParentData>("TwoWaitParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("TwoWaitChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);

        await ChildWaitingAsync(services, childId, "first");
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await engine.ResumeAsync(childId, "first", new Approval())).Status);
        await ChildWaitingAsync(services, childId, "second");
        Assert.Equal(WorkflowExecutionStatus.Success, (await engine.ResumeAsync(childId, "second", new Approval())).Status);

        Assert.True(Assert.IsType<ParentData>((await FinishedAsync(engine, started.InstanceId)).WorkflowData)
            .ChildApproved);
    }

    [Fact]
    [Trait("Spec", "D-06")]
    public async Task AParentRunsItsOtherBranchesWhileItsChildRuns()
    {
        var probe = new ActiveChildProbe();
        using var services = Services(activeChildProbe: probe);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<ChildData>("BusyChild").Step<ActiveChildActivity>().Build());
        var parent = Workflow.Create<ParentData>("ParentBesideBusyChild")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Invoke<ChildData>("BusyChild", 1))
                .Do(branch => branch.WaitFor<Approval>("other")))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        await probe.Started.Task.WaitAsync(Patience);

        // The child runs in a slot of its own, so its parent takes events meanwhile.
        var other = await engine.ResumeAsync(started.InstanceId, "other", new Approval());
        EventAssert.Consumed(other);
        Assert.Equal(WorkflowExecutionStatus.Suspended, other.Status);
        probe.Release.TrySetResult();

        Assert.Equal(WorkflowExecutionStatus.Success, (await FinishedAsync(engine, started.InstanceId)).Status);
    }

    [Fact]
    [Trait("Spec", "D-06")]
    public async Task ParallelChildrenTakeTheirAnswersInTheirOwnInboxes()
    {
        using var services = Services();
        var registry = services.GetRequiredService<IWorkflowVersionRegistry>();
        foreach (var (name, key) in new[] { ("FinanceApprovalChild", "finance"), ("LegalApprovalChild", "legal") })
            await registry.RegisterWorkflowAsync(ApprovingChild(name, key));
        var parent = Workflow.Create<ParentData>("ParallelChildApprovals")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Invoke<ChildData>("FinanceApprovalChild", 1, step => step
                    .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved)))
                .Do(branch => branch.Invoke<ChildData>("LegalApprovalChild", 1, step => step
                    .Output(data => data.Approved).To(ctx => ctx.WorkflowData.LegalApproved))))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var started = await engine.RunAsync(parent, new ParentData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var finance = Assert.Single(await repository.GetWorkflowInstancesByNameAsync("FinanceApprovalChild")).InstanceId;
        var legal = Assert.Single(await repository.GetWorkflowInstancesByNameAsync("LegalApprovalChild")).InstanceId;

        await ChildWaitingAsync(services, finance, "finance");
        Assert.Equal(WorkflowExecutionStatus.Success, (await engine.ResumeAsync(finance, "finance", new Approval())).Status);
        // The parent takes the finance child's completion when its host runs it.
        Assert.Equal(EventOutcome.Consumed, (await EventAssert.DecidedAsync((IWorkflowInbox)repository,
            started.InstanceId, $"child-completed-{finance}")).Outcome);
        // The finance branch has taken its child's output; the parent's data gets it at the join (D-14).
        var halfway = await BranchProgress.ViewAsync<ParentData>(repository, started.InstanceId);
        Assert.True(halfway.ChildApproved);
        Assert.False(halfway.LegalApproved);
        await ChildWaitingAsync(services, legal, "legal");
        Assert.Equal(WorkflowExecutionStatus.Success, (await engine.ResumeAsync(legal, "legal", new Approval())).Status);

        var finished = await FinishedAsync(engine, started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.True(Assert.IsType<ParentData>(finished.WorkflowData).LegalApproved);
    }

    [Fact]
    [Trait("Spec", "D-06")]
    public async Task AChildsAnswerAfterARestartIsTakenOnceAndWakesItsParent()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var child = ApprovingChild("RestartChild");
        var parent = Workflow.Create<ParentData>("RestartParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("RestartChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        string parentId;
        string childId;
        using (var firstProvider = Services(repository))
        {
            await firstProvider.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(child);
            var started = await firstProvider.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            parentId = started.InstanceId;
            childId = await ChildIdAsync(firstProvider, parentId);
            await ChildWaitingAsync(firstProvider, childId, "approval");
        }

        using var secondProvider = Services(repository);
        var registry = secondProvider.GetRequiredService<IWorkflowVersionRegistry>();
        await registry.RegisterWorkflowAsync(child);
        await registry.RegisterWorkflowAsync(parent);
        var engine = secondProvider.GetRequiredService<IWorkflowClient>();
        var completed = await engine.ResumeAsync(childId, "approval", new Approval(), "delivery-1");
        var duplicate = await engine.ResumeAsync(childId, "approval", new Approval(), "delivery-1");

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        EventAssert.Consumed(completed);
        EventAssert.Consumed(duplicate);
        var finished = await FinishedAsync(engine, parentId);
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.True(Assert.IsType<ParentData>(finished.WorkflowData).ChildApproved);
    }

    [Theory]
    [Trait("Spec", "D-06")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AParentLostBeforeTheCommitThatStartsItsChildStartsItOnceOnRecovery(bool cancelBeforeRecovery)
    {
        var store = new FaultInjectingRepository
        {
            // The commit that would start the child never reaches the store.
            FailCommit = instance => instance.WorkflowName == "CrashWindowParent" &&
                                     instance.ExecutionStateJson.Contains(nameof(ChildCompleted))
        };
        var child = ApprovingChild("CrashWindowChild");
        var parent = Workflow.Create<ParentData>("CrashWindowParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("CrashWindowChild", 1)
            .Build();
        string parentId;
        using (var firstProvider = Services(store))
        {
            await firstProvider.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(child);
            await EngineAssert.StoreFailureAsync(() => firstProvider.GetRequiredService<IWorkflowClient>()
                .RunAsync(parent, new ParentData()));
            parentId = Assert.Single(await store.GetWorkflowInstancesByNameAsync("CrashWindowParent")).InstanceId;
            Assert.Empty(await store.GetWorkflowInstancesByNameAsync("CrashWindowChild"));
        }
        store.FailCommit = _ => false;
        using var secondProvider = Services(store);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(child);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(parent);
        if (cancelBeforeRecovery)
            await store.RequestCancellationAsync(parentId, new CancellationReason { ReasonCode = "operator" });

        var recovered = await secondProvider.GetRequiredService<IWorkflowClient>().RecoverAsync(parentId);

        if (cancelBeforeRecovery)
        {
            Assert.Equal(WorkflowExecutionStatus.Cancelled, recovered.Status);
            Assert.Empty(await store.GetWorkflowInstancesByNameAsync("CrashWindowChild"));
        }
        else
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, recovered.Status);
            var started = Assert.Single(await store.GetWorkflowInstancesByNameAsync("CrashWindowChild"));
            Assert.Equal(await ChildIdAsync(secondProvider, parentId), started.InstanceId);
        }
    }

    [Fact]
    [Trait("Spec", "D-08")]
    public async Task AParentReadsItsNamedChildsDataAsThePreviousStep()
    {
        using var services = Services();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("WaitingChild"));
        var parent = Workflow.Create<ParentData>("WaitingChildParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("WaitingChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Step<FinishActivity>(step => step
                .Input(activity => activity.Approved).From(ctx => ctx.PreviousStep.Approved)
                .Output(activity => activity.Finished).To(ctx => ctx.WorkflowData.Finished))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        await ChildWaitingAsync(services, childId, "approval");

        EventAssert.Consumed(await engine.ResumeAsync(childId, "approval", new Approval()));
        var completed = await FinishedAsync(engine, started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        var data = Assert.IsType<ParentData>(completed.WorkflowData);
        Assert.True(data.ChildApproved);
        Assert.True(data.Finished);
    }

    [Fact]
    [Trait("Spec", "D-08")]
    public async Task OnlyTheMappedValuesOfAChildsDataReachItsParent()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<ChildData>("NotesChild")
                .Step<ApproveActivity>(step => step
                    .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
                .Build());
        var parent = Workflow.Create<ParentData>("NotesParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("NotesChild", 1, step => step
                .Input(data => data.Notes).From(_ => "child-only-" + new string('n', 200))
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();

        var started = await engine.RunAsync(parent, new ParentData());
        var finished = await FinishedAsync(engine, started.InstanceId);

        Assert.True(Assert.IsType<ParentData>(finished.WorkflowData).ChildApproved);
        var saved = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.DoesNotContain("child-only-", saved.WorkflowDataJson + saved.ExecutionStateJson);
    }

    [Fact]
    [Trait("Spec", "D-06")]
    public async Task AGrandchildsCompletionWakesItsParentWhichWakesItsOwn()
    {
        using var services = Services();
        var registry = services.GetRequiredService<IWorkflowVersionRegistry>();
        await registry.RegisterWorkflowAsync(ApprovingChild("Grandchild"));
        await registry.RegisterWorkflowAsync(Workflow.Create<ChildData>("MiddleChild")
            .Step<NoopActivity>()
            .Invoke<ChildData>("Grandchild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build());
        var parent = Workflow.Create<ParentData>("Grandparent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("MiddleChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var started = await engine.RunAsync(parent, new ParentData());
        var middle = await ChildIdAsync(services, started.InstanceId);
        await engine.AwaitAsync(middle, WorkflowAwaitCondition.Idle).WaitAsync(Patience);
        var grandchild = await ChildIdAsync(services, middle);
        Assert.Equal(middle, (await repository.GetWorkflowInstanceAsync(grandchild))!.ParentInstanceId);

        await ChildWaitingAsync(services, grandchild, "approval");
        EventAssert.Consumed(await engine.ResumeAsync(grandchild, "approval", new Approval()));

        var finished = await FinishedAsync(engine, started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.True(Assert.IsType<ParentData>(finished.WorkflowData).ChildApproved);
    }

    [Fact]
    [Trait("Spec", "D-06")]
    public async Task AParentCannotResolveItsInvocationWhileItsChildRuns()
    {
        var probe = new ActiveChildProbe();
        using var services = Services(activeChildProbe: probe);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<ChildData>("RunningChild")
                .Step<ActiveChildActivity>()
                .Step<ApproveActivity>(step => step
                    .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
                .Build());
        var parent = Workflow.Create<ParentData>("ParentOfRunningChild")
            .Step<NoopActivity>()
            .Invoke<ChildData>("RunningChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        await probe.Started.Task.WaitAsync(Patience);
        var childId = await ChildIdAsync(services, started.InstanceId);

        // The parent waits for its child; there is nothing for an operator to decide in it.
        Assert.Empty(await engine.GetPendingActivitiesAsync(started.InstanceId));
        Assert.Equal(WorkflowExecutionStatus.Faulted, (await engine.ResolveAsync(started.InstanceId, childId,
            ActivityResolution.CompletedInvocation(new ChildData { Approved = false }))).Status);
        probe.Release.TrySetResult();

        var finished = await FinishedAsync(engine, started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.True(Assert.IsType<ParentData>(finished.WorkflowData).ChildApproved);
    }

    [Fact]
    public async Task InvocationInputsAcceptDerivedResumeEvent()
    {
        using var services = Services();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<ChildData>("EventInputChild").Step<NoopActivity>().Build());
        var parent = Workflow.Create<ParentData>("DerivedEventInputParent")
            .Step<NoopActivity>()
            .WaitFor<BaseApproval>("approval")
            .Invoke<ChildData>("EventInputChild", 1, step => step
                .Input(data => data.Approved).From(ctx => ctx.PreviousStep.Approved)
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var waiting = await engine.RunAsync(parent, new ParentData());

        EventAssert.Consumed(await engine.ResumeAsync(waiting.InstanceId, "approval",
            new DerivedApproval { Approved = true }));
        var result = await FinishedAsync(engine, waiting.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(Assert.IsType<ParentData>(result.WorkflowData).ChildApproved);
    }

    [Fact]
    [Trait("Spec", "D-06")]
    public async Task AChildsTimeoutRunsInTheChildAndItsCompletionWakesItsParent()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time: time);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<ChildData>("TimedChild")
                .Step<NoopActivity>()
                .WaitFor<Approval>("approval", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMinutes(1))
                    .OnTimeout(path => path.Step<MarkChildTimeoutActivity>(step => step
                        .Output(activity => activity.TimedOut).To(ctx => ctx.WorkflowData.TimedOut))))
                .Build());
        var parent = Workflow.Create<ParentData>("TimedChildParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("TimedChild", 1, step => step
                .Output(data => data.TimedOut).To(ctx => ctx.WorkflowData.ChildTimedOut))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();

        var started = await engine.RunAsync(parent, new ParentData());
        await ChildWaitingAsync(services, await ChildIdAsync(services, started.InstanceId), "approval");
        time.Advance(TimeSpan.FromMinutes(2));
        var finished = await FinishedAsync(engine, started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.True(Assert.IsType<ParentData>(finished.WorkflowData).ChildTimedOut);
    }

    [Fact]
    [Trait("Spec", "D-08")]
    public async Task ParentCatchMatchesChildExceptionType()
    {
        using var services = Services();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<ChildData>("FailingChild").Step<ThrowChildActivity>().Build());
        var parent = Workflow.Create<ParentData>("CatchingParent")
            .Step<NoopActivity>()
            .Try(body => body.Invoke<ChildData>("FailingChild", 1))
            .Catch<ChildFailureException, MessageFault>(body => body
                .Step<CaptureFaultActivity>(step => step
                    .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.ChildError)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();

        var started = await engine.RunAsync(parent, new ParentData());
        var result = await FinishedAsync(engine, started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("child failed", Assert.IsType<ParentData>(result.WorkflowData).ChildError);
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public async Task AChildCancelledOnItsOwnRaisesChildWorkflowEndedExceptionInItsParent()
    {
        using var services = Services();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("CancelledAlone"));
        var parent = Workflow.Create<ParentData>("ParentOfCancelledChild")
            .Step<NoopActivity>()
            .Try(body => body.Invoke<ChildData>("CancelledAlone", 1))
            .Catch<ChildWorkflowEndedException>(body => body.Step<MarkFinallyActivity>(step => step
                .Output(activity => activity.Ran).To(ctx => ctx.WorkflowData.FinallyRan)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        await ChildWaitingAsync(services, childId, "approval");

        Assert.Equal(WorkflowExecutionStatus.Cancelled,
            (await engine.CancelAsync(childId, new CancellationReason { ReasonCode = "operator" })).Status);
        var finished = await FinishedAsync(engine, started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.True(Assert.IsType<ParentData>(finished.WorkflowData).FinallyRan);
    }

    [Fact]
    [Trait("Spec", "D-06")]
    public async Task AFailureWithAChildStillToStartSavesTheFailureAndStartsNoChild()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var registry = services.GetRequiredService<IWorkflowVersionRegistry>();
        await registry.RegisterWorkflowAsync(ApprovingChild("FineChild"));
        // A registered definition the engine cannot start: it has no data type.
        await registry.RegisterWorkflowAsync(new WorkflowDefinition { Name = "Untyped", Version = 1, Steps = [] });
        var parent = Workflow.Create<ParentData>("ParentOfUntyped")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(first => first.Invoke<ChildData>("FineChild", 1))
                .Do(second => second.Invoke<ChildData>("Untyped", 1)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData());

        // The first branch's child was to start in the commit the failure now makes; it is not started.
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(WorkflowStatus.Failed, (await repository.GetWorkflowInstanceAsync(result.InstanceId))!.Status);
        Assert.Empty(await repository.GetChildInstancesAsync(result.InstanceId));
    }

    [Fact]
    [Trait("Spec", "D-06")]
    public async Task ADeactivatedChildVersionIsNotStarted()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var registry = services.GetRequiredService<IWorkflowVersionRegistry>();
        await registry.RegisterWorkflowAsync(ApprovingChild("RetiredChild"));
        await registry.DeactivateWorkflowVersionAsync("RetiredChild", 1);
        var parent = Workflow.Create<ParentData>("ParentOfRetired")
            .Step<NoopActivity>()
            .Invoke<ChildData>("RetiredChild", 1)
            .Build();

        // As a client's start of it would be, the child's start is refused; the parent stays recoverable.
        var error = await EngineAssert.FailsAsync(WorkflowErrorKind.ArtifactUnavailable,
            () => services.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData()));
        Assert.Contains("is deactivated", error.Message);
        Assert.Empty(await repository.GetWorkflowInstancesByNameAsync("RetiredChild"));
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public async Task AChildsOutcomeTakenWhileItsParentUnwindsIsDiscarded()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("LateChild"));
        var parent = Workflow.Create<ParentData>("ParentUnwindingAtCompletion")
            .Step<NoopActivity>()
            .Try(body => body.Invoke<ChildData>("LateChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved)))
            .Finally(body => body.Step<FinishActivity>(step => step
                .Input(activity => activity.Approved).From(ctx => ctx.WorkflowData.ChildApproved)
                .Output(activity => activity.Finished).To(ctx => ctx.WorkflowData.Finished)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        await ChildWaitingAsync(services, childId, "approval");

        // The parent is held elsewhere while its child completes and a cancellation is requested, so it
        // applies the cancellation before it reads the completion.
        Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(started.InstanceId, "elsewhere",
            TimeSpan.FromMinutes(1)));
        Assert.Equal(WorkflowExecutionStatus.Success, (await engine.ResumeAsync(childId, "approval", new Approval())).Status);
        Assert.True(await repository.RequestCancellationAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "operator" }));
        Assert.True(await repository.ReleaseExecutionLeaseAsync(started.InstanceId, "elsewhere"));
        await engine.RecoverAsync(started.InstanceId);
        var cancelled = await FinishedAsync(engine, started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        var data = Assert.IsType<ParentData>(cancelled.WorkflowData);
        Assert.False(data.ChildApproved);
        Assert.False(data.Finished);
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public async Task DeletingAChildItsParentWaitsForEndsTheWaitAsTerminated()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("DeletedChild"));
        var parent = Workflow.Create<ParentData>("ParentOfDeleted")
            .Step<NoopActivity>()
            .Try(body => body.Invoke<ChildData>("DeletedChild", 1))
            .Catch<ChildWorkflowEndedException>(body => body.Step<MarkFinallyActivity>(step => step
                .Output(activity => activity.Ran).To(ctx => ctx.WorkflowData.FinallyRan)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        await ChildWaitingAsync(services, childId, "approval");

        await repository.DeleteWorkflowInstanceAsync(childId);
        var finished = await engine.FinishedAsync(await engine.RecoverAsync(started.InstanceId));

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.True(Assert.IsType<ParentData>(finished.WorkflowData).FinallyRan);
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public async Task ACancelledParentLeavesADetachedChildRunning()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("DetachedChild"));
        var parent = Workflow.Create<ParentData>("ParentOfDetached")
            .Step<NoopActivity>()
            .Invoke<ChildData>("DetachedChild", 1, step => step.CancelWithParent(false))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        await ChildWaitingAsync(services, childId, "approval");

        await engine.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "operator" });
        var cancelled = await FinishedAsync(engine, started.InstanceId);

        // The parent stops waiting at once; the child is not asked to cancel and finishes on its own.
        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.Null(await repository.GetCancellationRequestAsync(childId));
        Assert.Equal(WorkflowStatus.Suspended, (await repository.GetWorkflowInstanceAsync(childId))!.Status);
        Assert.Equal(WorkflowExecutionStatus.Success, (await engine.ResumeAsync(childId, "approval", new Approval())).Status);
        Assert.Equal(WorkflowStatus.Cancelled, (await repository.GetWorkflowInstanceAsync(started.InstanceId))!.Status);
        // Its completion reaches the parent's closed wait and is refused.
        var notice = await ((IWorkflowInbox)repository).GetEventAsync(started.InstanceId, $"child-completed-{childId}");
        Assert.Equal((EventOutcome.Rejected, EventRejection.WaitCancelled), (notice!.Outcome, notice.Rejection));
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public async Task AFailingParentLeavesADetachedChildRunning()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("OutlivingChild"));
        var parent = Workflow.Create<ParentData>("FailingParentOfDetached")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(first => first.Invoke<ChildData>("OutlivingChild", 1, step => step.CancelWithParent(false)))
                .Do(second => second.WaitFor<Approval>("fail").Step<ThrowChildActivity>()))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = Assert.Single(await repository.GetChildInstancesAsync(started.InstanceId)).InstanceId;
        await ChildWaitingAsync(services, childId, "approval");

        var failed = await engine.ResumeAsync(started.InstanceId, "fail", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, failed.Status);
        Assert.Null(await repository.GetCancellationRequestAsync(childId));
        Assert.Equal(WorkflowStatus.Suspended, (await repository.GetWorkflowInstanceAsync(childId))!.Status);
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public async Task ADetachedChildCancelledBeforeItsStartIsSavedNeverStarts()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("NeverStarted"));
        // The other branch wins the join in the same pass, before the child's start is committed.
        var parent = Workflow.Create<ParentData>("ParentWonBeforeStart")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(first => first.Invoke<ChildData>("NeverStarted", 1, step => step.CancelWithParent(false)))
                .Do(_ => { }))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Empty(await repository.GetChildInstancesAsync(result.InstanceId));
    }

    // An input mapped to a property the child's data does not have is an invalid definition, not skipped (F-23).
    [Fact]
    [Trait("Spec", "F-23")]
    public async Task AChildInputTheChildsDataCannotTakeFailsTheInvocation()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("NarrowChild"));
        var parent = Workflow.Create<ParentData>("ParentOfNarrowChild")
            .Step<NoopActivity>()
            .Invoke<WideChildData>("NarrowChild", 1, step => step
                .Input(child => child.Extra).From(_ => "lost before"))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("Child workflow input 'ChildData.Extra' is not a property of its data", result.ErrorMessage);
        Assert.Empty(await repository.GetChildInstancesAsync(result.InstanceId));
    }

    // A whole number takes a child's enum property, as reflection assigned it before conversion was checked.
    [Fact]
    [Trait("Spec", "F-23")]
    public async Task AChildInputNumberTakesTheChildsEnumProperty()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<LevelData>("LevelChild").Step<NoopActivity>().Build());
        var parent = Workflow.Create<ParentData>("ParentOfLevelChild")
            .Step<NoopActivity>()
            .Invoke<LevelNumberData>("LevelChild", 1, step => step.Input(child => child.Level).From(_ => 2))
            .Build();

        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        var result = await FinishedAsync(engine, started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var child = Assert.Single(await repository.GetChildInstancesAsync(result.InstanceId));
        Assert.Contains("\"Level\":2", child.WorkflowDataJson);
    }

    // A value the child's data property cannot take is an invalid definition, not passed on unconverted (F-23).
    [Fact]
    [Trait("Spec", "F-23")]
    public async Task AChildInputOfAnotherTypeFailsTheInvocation()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("StrictChild"));
        var parent = Workflow.Create<ParentData>("ParentOfStrictChild")
            .Step<NoopActivity>()
            .Invoke<WideChildData>("StrictChild", 1, step => step
                .Input(child => child.Approved).From(_ => "perhaps"))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("Child workflow input 'ChildData.Approved' takes Boolean, not String", result.ErrorMessage);
        Assert.Empty(await repository.GetChildInstancesAsync(result.InstanceId));
    }

    // An engine failure ends the parent at once, without unwinding its branches; the children it waits for are
    // still asked to cancel, since nothing will take their completions.
    [Fact]
    [Trait("Spec", "D-09")]
    public async Task AParentEndedByAnEngineFailureAsksItsChildToCancel()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(ApprovingChild("AbandonedChild"));
        var parent = Workflow.Create<ParentData>("ParentEndedByEngineFailure")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(first => first.Invoke<ChildData>("AbandonedChild", 1))
                // An input that cannot be saved and read back is an invalid definition, an engine failure.
                .Do(second => second.WaitFor<Approval>("fail").Step<TakesUnreadableInput>(step => step
                    .Input(activity => activity.Value).From(_ => new UnreadableValue(1)))))
            .Build();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        var childId = Assert.Single(await repository.GetChildInstancesAsync(started.InstanceId)).InstanceId;
        await ChildWaitingAsync(services, childId, "approval");

        var failed = await engine.ResumeAsync(started.InstanceId, "fail", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Faulted, failed.Status);
        Assert.Contains("cannot be saved and read back", failed.ErrorMessage);
        Assert.NotNull(await repository.GetCancellationRequestAsync(childId));
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public void AParentThatFailsAsksOnlyItsAttachedChildrenToCancel()
    {
        // A failure that ends the instance at once, such as an engine failure, reaches the children it still
        // waits for without cancelling their branches first.
        var checkpoint = new Core.Runtime.ExecutionCheckpoint();
        checkpoint.Waits.Add(new Core.Runtime.WaitState { ChildWorkflowId = "attached", EventType = "child" });
        checkpoint.Waits.Add(new Core.Runtime.WaitState { ChildWorkflowId = "detached", EventType = "child", Detached = true });

        Core.Runtime.ContinuationTransitions.AbandonChildren(checkpoint);

        Assert.Equal(["attached"], checkpoint.ChildCancellations.Select(cancellation => cancellation.ChildInstanceId));
    }

    [Fact]
    [Trait("Spec", "D-09")]
    public void WhetherAChildIsCancelledWithItsParentIsPartOfTheDefinition()
    {
        WorkflowDefinition Parent(bool cancel) => Workflow.Create<ParentData>("ParentFingerprint")
            .Step<NoopActivity>()
            .Invoke<ChildData>("AnyChild", 1, step => step.CancelWithParent(cancel))
            .Build();

        Assert.NotEqual(new Core.Runtime.WorkflowScopeCatalog(Parent(true)).Fingerprint,
            new Core.Runtime.WorkflowScopeCatalog(Parent(false)).Fingerprint);
    }

    // A child that waits for one approval and records it.
    private static WorkflowDefinition ApprovingChild(string name, string key = "approval") =>
        Workflow.Create<ChildData>(name)
            .Step<NoopActivity>()
            .WaitFor<Approval>(key)
            .Step<ApproveActivity>(step => step
                .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();

    private static async Task<WorkflowExecutionResult> FinishedAsync(IWorkflowClient engine, string instanceId) =>
        await engine.AwaitAsync(instanceId, WorkflowAwaitCondition.Finished).WaitAsync(Patience);

    // A child runs as soon as a slot takes it; an event addressed by key needs its wait to be open.
    private static async Task ChildWaitingAsync(IServiceProvider services, string childId, string key)
    {
        var inbox = (IWorkflowInbox)services.GetRequiredService<IWorkflowStateRepository>();
        using var patience = new CancellationTokenSource(Patience);
        while ((await inbox.GetOpenWaitsAsync(new WaitQuery(InstanceId: childId, Key: key))).Count == 0)
            await Task.Delay(10, patience.Token);
    }

    private static ServiceProvider Services(IWorkflowStateRepository? repository = null,
        ActiveChildProbe? activeChildProbe = null, TimeProvider? time = null)
    {
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        if (time != null)
            registrations.AddSingleton(time);
        registrations.AddTransient<NoopActivity>();
        registrations.AddTransient<ApproveActivity>();
        registrations.AddTransient<FinishActivity>();
        registrations.AddTransient<MarkFinallyActivity>();
        registrations.AddTransient<MarkChildTimeoutActivity>();
        registrations.AddTransient<ThrowChildActivity>();
        registrations.AddTransient<CaptureFaultActivity>();
        if (activeChildProbe != null)
        {
            registrations.AddSingleton(activeChildProbe);
            registrations.AddTransient<ActiveChildActivity>();
        }
        registrations.AddIxIFlow();
        if (repository != null)
            registrations.AddSingleton(repository);
        return registrations.BuildServiceProvider();
    }

    private static async Task<string> ChildIdAsync(IServiceProvider services, string parentId)
    {
        var instance = await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(parentId);
        var checkpoint = ExecutionCheckpoint.Read(instance!.ExecutionStateJson);
        return checkpoint.Continuations.Single().PendingActivity!.ChildWorkflowId!;
    }

    public sealed class ParentData
    {
        public bool ChildApproved { get; set; }
        public bool LegalApproved { get; set; }
        public bool Finished { get; set; }
        public bool FinallyRan { get; set; }
        public bool ChildTimedOut { get; set; }
        public string ChildError { get; set; } = "";
    }

    public sealed class ChildData
    {
        public bool Approved { get; set; }
        public bool TimedOut { get; set; }
        public string Notes { get; set; } = "";
    }

    public sealed class Approval;

    public class BaseApproval
    {
        public bool Approved { get; set; }
    }

    public sealed class DerivedApproval : BaseApproval;

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class ApproveActivity : IAsyncActivity
    {
        public bool Approved { get; private set; }

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Approved = true;
            return Task.CompletedTask;
        }
    }

    public sealed class FinishActivity : IAsyncActivity
    {
        public bool Approved { get; set; }
        public bool Finished { get; private set; }

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Finished = Approved;
            return Task.CompletedTask;
        }
    }

    public sealed class MarkFinallyActivity : IAsyncActivity
    {
        public bool Ran { get; private set; }

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Ran = true;
            return Task.CompletedTask;
        }
    }

    public sealed class MarkChildTimeoutActivity : IAsyncActivity
    {
        public bool TimedOut { get; private set; }

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            TimedOut = true;
            return Task.CompletedTask;
        }
    }

    public sealed class ChildFailureException(string message) : Exception(message);

    public enum Level
    {
        Low,
        Medium,
        High
    }

    public sealed class LevelData
    {
        public Level Level { get; set; }
    }

    /// <summary>The child's data as the parent names it: its enum as a number.</summary>
    public sealed class LevelNumberData
    {
        public int Level { get; set; }
    }

    /// <summary>A child data type that does not match the registered child's data: one property it lacks, one of
    /// another type.</summary>
    public sealed class WideChildData
    {
        public string Extra { get; set; } = "";
        public string Approved { get; set; } = "";
    }

    /// <summary>Its constructor's parameter names no property, so it cannot be read back from JSON.</summary>
    public sealed class UnreadableValue(int seed)
    {
        public int Number { get; } = seed;
    }

    public sealed class TakesUnreadableInput : IAsyncActivity
    {
        public UnreadableValue? Value { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class ThrowChildActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) =>
            throw new ChildFailureException("child failed");
    }

    public sealed class CaptureFaultActivity : IAsyncActivity
    {
        public string Message { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class ActiveChildProbe
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationSeen { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ActiveChildActivity(ActiveChildProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            probe.Started.TrySetResult();
            try
            {
                await probe.Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                probe.CancellationSeen.TrySetResult();
                throw;
            }
        }
    }
}

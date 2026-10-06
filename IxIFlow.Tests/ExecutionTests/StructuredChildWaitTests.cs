using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class StructuredChildWaitTests
{
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
            .Invoke<ChildData>("PersistedChild", 1, _ => { })
            .Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(parent, new ParentData(),
                    new WorkflowOptions { PersistState = false }));
        Assert.Empty(await repository.GetWorkflowInstancesByNameAsync(parent.Name));
        Assert.Empty(await repository.GetWorkflowInstancesByNameAsync(child.Name));
    }

    [Fact]
    public async Task ParentRecoveryObservesChildCompletedBeforeParentCheckpoint()
    {
        using var services = Services();
        var child = Workflow.Create<ChildData>("ExternallyCompletedChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Step<ApproveActivity>(step => step
                .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("ObservingParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("ExternallyCompletedChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        var childResult = await engine.ResumeWorkflowAsync(childId, "approval", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, childResult.Status);

        var recovered = await engine.RecoverWorkflowAsync(started.InstanceId);

        Assert.True(recovered.Status == WorkflowExecutionStatus.Success, recovered.ErrorMessage);
        Assert.True(Assert.IsType<ParentData>(recovered.WorkflowData).ChildApproved);
    }

    [Fact]
    public async Task ParentResumeRunsAfterChildAlreadyCompleted()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var child = Workflow.Create<ChildData>("ChildCompletesFirst")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Step<ApproveActivity>(step => step
                .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("ParentCompletesAfterChild")
            .Step<NoopActivity>()
            .Invoke<ChildData>("ChildCompletesFirst", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await engine.ResumeWorkflowAsync(childId, "approval", new Approval())).Status);

        var resumed = await engine.ResumeWorkflowAsync(started.InstanceId,
            "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.True(Assert.IsType<ParentData>(resumed.WorkflowData).ChildApproved);
        Assert.Equal(WorkflowStatus.Completed,
            (await repository.GetWorkflowInstanceAsync(started.InstanceId))!.Status);
    }

    [Fact]
    public async Task ParentReportsChildActivityNeedingResolution()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var child = Workflow.Create<ChildData>("UnresolvedChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("UnresolvedChildParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("UnresolvedChild", 1, _ => { })
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        var savedChild = (await repository.GetWorkflowInstanceAsync(childId))!;
        savedChild.Status = WorkflowStatus.NeedsResolution;
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await repository.CommitWorkflowInstanceAsync(savedChild, savedChild.Revision,
                Guid.NewGuid().ToString("N"))).Status);

        var recovered = await engine.RecoverWorkflowAsync(started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        Assert.Contains(childId, recovered.ErrorMessage);
        var savedParent = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        var savedCheckpoint = ExecutionCheckpoint.Read(savedParent.ExecutionStateJson);
        var childLink = Assert.Single(savedCheckpoint.Waits);
        Assert.Equal(childId, childLink.ChildWorkflowId);
        Assert.True(string.IsNullOrEmpty(childLink.EventType));
        Assert.Null(childLink.ChildWaitId);
        Assert.Equal(ContinuationStatus.WaitingResolution,
            Assert.Single(savedCheckpoint.Continuations).Status);
        Assert.NotNull(savedParent.NextDueAtUtc);
        var staleApproval = await engine.ResumeWorkflowAsync(started.InstanceId,
            "approval", new Approval());
        Assert.False(staleApproval.EventAccepted);
        await engine.RecoverWorkflowAsync(started.InstanceId);
        var unchangedLink = Assert.Single(ExecutionCheckpoint.Read(
            (await repository.GetWorkflowInstanceAsync(started.InstanceId))!.ExecutionStateJson).Waits);
        Assert.Equal(childLink.Id, unchangedLink.Id);
    }

    [Fact]
    public async Task CancellingParentClearsStaleChildResolutionAfterChildReturnsToWait()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var child = Workflow.Create<ChildData>("ChildReturnsToWait")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("ParentClearsChildResolution")
            .Step<NoopActivity>()
            .Invoke<ChildData>("ChildReturnsToWait", 1, _ => { })
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        var savedChild = (await repository.GetWorkflowInstanceAsync(childId))!;
        savedChild.Status = WorkflowStatus.NeedsResolution;
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await repository.CommitWorkflowInstanceAsync(savedChild, savedChild.Revision,
                Guid.NewGuid().ToString("N"))).Status);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution,
            (await engine.RecoverWorkflowAsync(started.InstanceId)).Status);
        var savedParent = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        var checkpoint = ExecutionCheckpoint.Read(savedParent.ExecutionStateJson);
        checkpoint.CancellationRequested = true;
        Assert.Single(checkpoint.Continuations).CancellationUnwind = true;
        savedParent.CancellationReason = new CancellationReason { ReasonCode = "operator" };
        savedParent.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await repository.CommitWorkflowInstanceAsync(savedParent, savedParent.Revision,
                Guid.NewGuid().ToString("N"))).Status);
        savedChild = (await repository.GetWorkflowInstanceAsync(childId))!;
        savedChild.Status = WorkflowStatus.Suspended;
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await repository.CommitWorkflowInstanceAsync(savedChild, savedChild.Revision,
                Guid.NewGuid().ToString("N"))).Status);

        await engine.RecoverWorkflowAsync(started.InstanceId);

        var refreshed = ExecutionCheckpoint.Read((await repository
            .GetWorkflowInstanceAsync(started.InstanceId))!.ExecutionStateJson);
        var continuation = Assert.Single(refreshed.Continuations);
        Assert.Equal(ContinuationStatus.Waiting, continuation.Status);
        Assert.Null(continuation.PendingActivity!.ResolutionReason);
        var link = Assert.Single(refreshed.Waits);
        Assert.Null(link.ChildWaitId);
        Assert.True(string.IsNullOrEmpty(link.EventType));
    }

    [Fact]
    public async Task ParentRecoveryRefreshesChildWaitAfterChildAdvances()
    {
        using var services = Services();
        var child = Workflow.Create<ChildData>("TwoWaitChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("first")
            .WaitFor<Approval>("second")
            .Step<ApproveActivity>(step => step
                .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("TwoWaitParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("TwoWaitChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Suspended,
            (await engine.ResumeWorkflowAsync(childId, "first", new Approval())).Status);

        var refreshed = await engine.RecoverWorkflowAsync(started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Suspended, refreshed.Status);
        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "second", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.True(Assert.IsType<ParentData>(completed.WorkflowData).ChildApproved);
    }

    [Fact]
    public async Task ParentAcceptsNextChildWaitWithoutExplicitRecovery()
    {
        using var services = Services();
        var child = Workflow.Create<ChildData>("NextWaitChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("first")
            .WaitFor<Approval>("second")
            .Step<ApproveActivity>(step => step
                .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("NextWaitParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("NextWaitChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Suspended,
            (await engine.ResumeWorkflowAsync(childId, "first", new Approval())).Status);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId,
            "second", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.True(completed.EventAccepted);
        Assert.True(Assert.IsType<ParentData>(completed.WorkflowData).ChildApproved);
    }

    [Fact]
    public async Task ParentStopsExposingChildEventAfterChildStartsRunning()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var probe = new ActiveChildProbe();
        using var services = Services(repository, probe);
        var child = Workflow.Create<ChildData>("RunningAfterApproval")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Step<ActiveChildActivity>(_ => { })
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("ParentOfRunningApproval")
            .Step<NoopActivity>()
            .Invoke<ChildData>("RunningAfterApproval", 1, _ => { })
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        var childExecution = engine.ResumeWorkflowAsync(childId, "approval", new Approval());
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert.False((await engine.ResumeWorkflowAsync(started.InstanceId,
                "approval", new Approval(), attempt.Token)).EventAccepted);
            var waiting = await engine.RecoverWorkflowAsync(started.InstanceId);
            Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);
            var savedParent = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
            var childLink = Assert.Single(ExecutionCheckpoint.Read(savedParent.ExecutionStateJson).Waits);
            Assert.Equal(childId, childLink.ChildWorkflowId);
            Assert.Null(childLink.ChildWaitId);
            Assert.True(string.IsNullOrEmpty(childLink.EventType));
            Assert.False((await engine.ResumeWorkflowAsync(started.InstanceId,
                "approval", new Approval())).EventAccepted);
        }
        finally
        {
            probe.Release.TrySetResult();
            await childExecution.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await engine.RecoverWorkflowAsync(started.InstanceId)).Status);
    }

    [Fact]
    public async Task DueChildWaitDoesNotFaultParentWhileChildNeedsResolution()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var child = Workflow.Create<ChildData>("DueUnresolvedChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval", configure: wait => wait
                .TimeoutAfter(TimeSpan.FromMilliseconds(80))
                .OnTimeout(path => path.Step<MarkChildTimeoutActivity>(_ => { })))
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("DueUnresolvedParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("DueUnresolvedChild", 1, _ => { })
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        var savedChild = (await repository.GetWorkflowInstanceAsync(childId))!;
        var childCheckpoint = ExecutionCheckpoint.Read(savedChild.ExecutionStateJson);
        childCheckpoint.Waits.Clear();
        var position = childCheckpoint.Continuations.Single().Stack.Single();
        position.NextStepIndex = 0;
        var continuation = childCheckpoint.Continuations.Single();
        continuation.Status = ContinuationStatus.WaitingResolution;
        continuation.PendingActivity = new ActivityInvocationState
        {
            StepId = "root/0",
            Attempts = [new ActivityAttemptState()]
        };
        savedChild.Status = WorkflowStatus.NeedsResolution;
        savedChild.ExecutionStateJson = JsonSerializer.Serialize(childCheckpoint);
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await repository.CommitWorkflowInstanceAsync(savedChild, savedChild.Revision,
                Guid.NewGuid().ToString("N"))).Status);
        await Task.Delay(160);

        var recovered = await engine.RecoverWorkflowAsync(started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, recovered.Status);
        Assert.Equal(WorkflowStatus.NeedsResolution,
            (await repository.GetWorkflowInstanceAsync(started.InstanceId))!.Status);
    }

    [Fact]
    public async Task ParallelChildWorkflowsKeepIndependentWaitsUntilBothResume()
    {
        using var services = Services();
        var registry = services.GetRequiredService<IWorkflowVersionRegistry>();
        foreach (var (name, key) in new[]
                 { ("FinanceApprovalChild", "finance"), ("LegalApprovalChild", "legal") })
            await registry.RegisterWorkflowAsync(Workflow.Create<ChildData>(name)
                .Step<NoopActivity>()
                .WaitFor<Approval>(key)
                .Step<ApproveActivity>(step => step
                    .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
                .Build());
        var parent = Workflow.Create<ParentData>("ParallelChildApprovals")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Invoke<ChildData>("FinanceApprovalChild", 1, step => step
                    .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved)))
                .Do(branch => branch.Invoke<ChildData>("LegalApprovalChild", 1, step => step
                    .Output(data => data.Approved).To(ctx => ctx.WorkflowData.LegalApproved))))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());

        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var first = await engine.ResumeWorkflowAsync(started.InstanceId, "finance", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Suspended, first.Status);
        Assert.True(Assert.IsType<ParentData>(first.WorkflowData).ChildApproved);
        Assert.False(Assert.IsType<ParentData>(first.WorkflowData).LegalApproved);
        var second = await engine.ResumeWorkflowAsync(started.InstanceId, "legal", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, second.Status);
        Assert.True(Assert.IsType<ParentData>(second.WorkflowData).LegalApproved);
    }

    [Fact]
    public async Task ParentChildWaitResumesAfterProviderRestartAndDeduplicatesDelivery()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var child = Workflow.Create<ChildData>("RestartChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Step<ApproveActivity>(step => step
                .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();
        var parent = Workflow.Create<ParentData>("RestartParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("RestartChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        string parentId;
        using (var firstProvider = Services(repository))
        {
            await firstProvider.GetRequiredService<IWorkflowVersionRegistry>()
                .RegisterWorkflowAsync(child);
            var started = await firstProvider.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(parent, new ParentData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            parentId = started.InstanceId;
        }

        using var secondProvider = Services(repository);
        var registry = secondProvider.GetRequiredService<IWorkflowVersionRegistry>();
        await registry.RegisterWorkflowAsync(child);
        await registry.RegisterWorkflowAsync(parent);
        var engine = secondProvider.GetRequiredService<IWorkflowEngine>();
        var completed = await engine.ResumeWorkflowDeliveryAsync(parentId, "approval",
            new Approval(), "delivery-1");
        var duplicate = await engine.ResumeWorkflowDeliveryAsync(parentId, "approval",
            new Approval(), "delivery-1");

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.True(completed.EventAccepted);
        Assert.True(Assert.IsType<ParentData>(completed.WorkflowData).ChildApproved);
        Assert.Equal(WorkflowExecutionStatus.Success, duplicate.Status);
        Assert.True(duplicate.EventAccepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryOfParentStartBeforeChildCreationHonorsCancellation(
        bool cancelBeforeRecovery)
    {
        using var firstProvider = Services();
        var child = Workflow.Create<ChildData>("CrashWindowChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Build();
        await firstProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("CrashWindowParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("CrashWindowChild", 1, _ => { })
            .Build();
        var started = await firstProvider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(parent, new ParentData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var saved = (await firstProvider.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(started.InstanceId))!;
        var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
        Assert.NotNull(checkpoint.Continuations.Single().PendingActivity);
        checkpoint.Waits.Clear();
        checkpoint.Continuations.Single().Status = ContinuationStatus.Active;
        checkpoint.Continuations.Single().PendingActivity!.ChildWorkflowId = null;
        saved.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        saved.Status = WorkflowStatus.Running;
        saved.Revision = 0;

        var recoveredStore = new InMemoryWorkflowStateRepository();
        await recoveredStore.CommitWorkflowInstanceAsync(saved, 0, Guid.NewGuid().ToString("N"));
        using var secondProvider = Services(recoveredStore);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(parent);
        if (cancelBeforeRecovery)
            await recoveredStore.RequestCancellationAsync(started.InstanceId,
                new CancellationReason { ReasonCode = "operator" });

        var recovered = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .RecoverWorkflowAsync(started.InstanceId);

        if (cancelBeforeRecovery)
        {
            Assert.Equal(WorkflowExecutionStatus.Cancelled, recovered.Status);
            Assert.Empty(await recoveredStore.GetWorkflowInstancesByNameAsync("CrashWindowChild"));
        }
        else
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, recovered.Status);
            var childId = await ChildIdAsync(secondProvider, started.InstanceId);
            Assert.Equal(WorkflowStatus.Suspended,
                (await recoveredStore.GetWorkflowInstanceAsync(childId))!.Status);
        }
    }

    [Fact]
    public async Task ParentResumesWaitInsideNamedChildWorkflow()
    {
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddTransient<NoopActivity>();
        registrations.AddTransient<ApproveActivity>();
        registrations.AddTransient<FinishActivity>();
        registrations.AddIxIFlow();
        using var services = registrations.BuildServiceProvider();
        var registry = services.GetRequiredService<IWorkflowVersionRegistry>();
        var child = Workflow.Create<ChildData>("WaitingChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Step<ApproveActivity>(step => step
                .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();
        await registry.RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("WaitingChildParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("WaitingChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Step<FinishActivity>(step => step
                .Input(activity => activity.Approved).From(ctx => ctx.PreviousStep.Approved)
                .Output(activity => activity.Finished).To(ctx => ctx.WorkflowData.Finished))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();

        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new Approval());
        Assert.True(completed.EventAccepted);
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        var data = Assert.IsType<ParentData>(completed.WorkflowData);
        Assert.True(data.ChildApproved);
        Assert.True(data.Finished);
    }

    [Fact]
    public async Task CancellingParentCancelsWaitingChildAndRunsFinally()
    {
        using var services = Services();
        var child = Workflow.Create<ChildData>("CancellableChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("CancellableParent")
            .Step<NoopActivity>()
            .Try(body => body.Invoke<ChildData>("CancellableChild", 1, _ => { }))
            .Finally(body => body.Step<MarkFinallyActivity>(step => step
                .Output(activity => activity.Ran).To(ctx => ctx.WorkflowData.FinallyRan)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);

        var cancelled = await engine.CancelWorkflowAsync(started.InstanceId,
                new CancellationReason { ReasonCode = "operator" })
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.True(Assert.IsType<ParentData>(cancelled.WorkflowData).FinallyRan);
        Assert.Equal(WorkflowStatus.Cancelled,
            (await services.GetRequiredService<IWorkflowStateRepository>()
                .GetWorkflowInstanceAsync(childId))!.Status);
    }

    [Fact]
    public async Task ParentCancellationPollsChildOwnedByAnotherHost()
    {
        using var services = Services();
        var child = Workflow.Create<ChildData>("RemotelyOwnedChild")
            .Step<NoopActivity>().WaitFor<Approval>("approval").Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("ParentWithRemotelyOwnedChild")
            .Step<NoopActivity>()
            .Try(body => body.Invoke<ChildData>("RemotelyOwnedChild", 1, _ => { }))
            .Finally(body => body.Step<MarkFinallyActivity>(step => step
                .Output(activity => activity.Ran).To(ctx => ctx.WorkflowData.FinallyRan)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        var foreignToken = Guid.NewGuid().ToString("N");
        Assert.True(await repository.TryAcquireExecutionLeaseAsync(childId, foreignToken,
            TimeSpan.FromMinutes(1)));

        var waiting = await engine.CancelWorkflowAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "operator" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);
        Assert.NotNull((await repository.GetWorkflowInstanceAsync(started.InstanceId))!.NextDueAtUtc);

        Assert.True(await repository.ReleaseExecutionLeaseAsync(childId, foreignToken));
        var cancelledChild = await engine.RecoverWorkflowAsync(childId);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelledChild.Status);
        var dueParent = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        dueParent.NextDueAtUtc = DateTime.UtcNow.AddSeconds(-1);
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await repository.CommitWorkflowInstanceAsync(dueParent, dueParent.Revision,
                Guid.NewGuid().ToString("N"))).Status);
        using var scanner = new WorkflowRecoveryService(
            services.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance);
        await scanner.RecoverOnceAsync(CancellationToken.None);
        var cancelledParent = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal(WorkflowStatus.Cancelled, cancelledParent.Status);
        Assert.True(JsonSerializer.Deserialize<ParentData>(cancelledParent.WorkflowDataJson)!.FinallyRan);
    }

    [Fact]
    public async Task ParentCancellationSettlesWhenChildCleanupFails()
    {
        using var services = Services();
        var child = Workflow.Create<ChildData>("FailingCancellationChild")
            .Step<NoopActivity>()
            .Try(body => body.WaitFor<Approval>("approval"))
            .Finally(body => body.Step<ThrowChildActivity>(_ => { }))
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("ParentWithFailingCancellationChild")
            .Step<NoopActivity>()
            .Invoke<ChildData>("FailingCancellationChild", 1, _ => { })
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);

        var cancelled = await engine.CancelWorkflowAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "operator" });

        Assert.Equal(WorkflowStatus.Failed,
            (await repository.GetWorkflowInstanceAsync(childId))!.Status);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.Empty(await engine.GetPendingActivitiesAsync(started.InstanceId));
    }

    [Fact]
    public async Task CancellingParentWithActiveChildRequiresResolutionBeforeFinally()
    {
        var probe = new ActiveChildProbe();
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddSingleton(probe);
        registrations.AddTransient<NoopActivity>();
        registrations.AddTransient<ActiveChildActivity>();
        registrations.AddTransient<MarkFinallyActivity>();
        registrations.AddIxIFlow();
        using var services = registrations.BuildServiceProvider();
        var child = Workflow.Create<ChildData>("ActiveChild")
            .Step<ActiveChildActivity>()
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("ActiveChildParent")
            .Step<NoopActivity>()
            .Try(body => body.Invoke<ChildData>("ActiveChild", 1, _ => { }))
            .Finally(body => body.Step<MarkFinallyActivity>(step => step
                .Output(activity => activity.Ran).To(ctx => ctx.WorkflowData.FinallyRan)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();

        var execution = engine.ExecuteWorkflowAsync(parent, new ParentData(),
            new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMinutes(1) });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var instance = Assert.Single(await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstancesByNameAsync("ActiveChildParent"));
        var childId = Assert.Single(await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstancesByNameAsync("ActiveChild")).InstanceId;
        await engine.CancelWorkflowAsync(instance.InstanceId,
            new CancellationReason { ReasonCode = "operator" });
        var unresolved = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, unresolved.Status);
        Assert.False(Assert.IsType<ParentData>(unresolved.WorkflowData).FinallyRan);
        var waitingParent = (await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstanceAsync(instance.InstanceId))!;
        Assert.NotNull(waitingParent.NextDueAtUtc);
        Assert.True(waitingParent.NextDueAtUtc < waitingParent.StartedAt!.Value.AddMinutes(1));
        Assert.True(probe.CancellationSeen.Task.IsCompleted);
        Assert.Equal(WorkflowStatus.NeedsResolution,
            (await services.GetRequiredService<IWorkflowStateRepository>()
                .GetWorkflowInstanceAsync(childId))?.Status);

        var pending = Assert.Single(await engine.GetPendingActivitiesAsync(childId));
        var childResolution = await engine.ResolveActivityAsync(childId, pending.InvocationId,
            ActivityResolution.Completed(new Dictionary<string, object?>()));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, childResolution.Status);
        var cancelled = await engine.RecoverWorkflowAsync(instance.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.True(Assert.IsType<ParentData>(cancelled.WorkflowData).FinallyRan);
    }

    [Fact]
    public async Task CancellationRunsChildWorkflowInsideFinally()
    {
        using var services = Services();
        var cleanupChild = Workflow.Create<ChildData>("CancellationCleanupChild")
            .Step<ApproveActivity>(step => step
                .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(cleanupChild);
        var parent = Workflow.Create<ParentData>("CancellationCleanupParent")
            .Step<NoopActivity>()
            .Try(body => body.WaitFor<Approval>("approval"))
            .Finally(body => body.Invoke<ChildData>("CancellationCleanupChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var cancelled = await engine.CancelWorkflowAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "operator" });

        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.True(Assert.IsType<ParentData>(cancelled.WorkflowData).ChildApproved);
        var child = Assert.Single(await services.GetRequiredService<IWorkflowStateRepository>()
            .GetWorkflowInstancesByNameAsync("CancellationCleanupChild"));
        Assert.Equal(WorkflowStatus.Completed, child.Status);
    }

    [Fact]
    public async Task ParentProjectsWaitFromChildInsideCancellationFinally()
    {
        using var services = Services();
        var cleanupChild = Workflow.Create<ChildData>("WaitingCleanupChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("cleanup")
            .Step<ApproveActivity>(step => step
                .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(cleanupChild);
        var parent = Workflow.Create<ParentData>("WaitingCleanupParent")
            .Step<NoopActivity>()
            .Try(body => body.WaitFor<Approval>("start"))
            .Finally(body => body.Invoke<ChildData>("WaitingCleanupChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved)))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());

        var cancelling = await engine.CancelWorkflowAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "operator" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, cancelling.Status);
        var completed = await engine.ResumeWorkflowAsync(started.InstanceId,
            "cleanup", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Cancelled, completed.Status);
        Assert.True(Assert.IsType<ParentData>(completed.WorkflowData).ChildApproved);
    }

    [Fact]
    public async Task ParentRecoveryWaitsForRunningChildAndRejectsParentResolution()
    {
        var originalStore = new InMemoryWorkflowStateRepository();
        var probe = new ActiveChildProbe();
        using var originalProvider = Services(originalStore, probe);
        var child = Workflow.Create<ChildData>("RecoveredRunningChild")
            .Step<ActiveChildActivity>()
            .Step<ApproveActivity>(step => step
                .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();
        var parent = Workflow.Create<ParentData>("ParentOfRunningChild")
            .Step<NoopActivity>()
            .Invoke<ChildData>("RecoveredRunningChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        await originalProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        using var stoppedCall = new CancellationTokenSource();
        var originalExecution = originalProvider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(parent, new ParentData(),
                cancellationToken: stoppedCall.Token);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var savedParent = Assert.Single(await originalStore
            .GetWorkflowInstancesByNameAsync("ParentOfRunningChild"));
        var savedChild = Assert.Single(await originalStore
            .GetWorkflowInstancesByNameAsync("RecoveredRunningChild"));

        var recoveredStore = new InMemoryWorkflowStateRepository();
        savedParent.Revision = 0;
        savedChild.Revision = 0;
        await recoveredStore.CommitWorkflowInstanceAsync(savedParent, 0, Guid.NewGuid().ToString("N"));
        await recoveredStore.CommitWorkflowInstanceAsync(savedChild, 0, Guid.NewGuid().ToString("N"));
        stoppedCall.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => originalExecution);

        using var recoveredProvider = Services(recoveredStore, new ActiveChildProbe());
        var registry = recoveredProvider.GetRequiredService<IWorkflowVersionRegistry>();
        await registry.RegisterWorkflowAsync(child);
        await registry.RegisterWorkflowAsync(parent);
        var engine = recoveredProvider.GetRequiredService<IWorkflowEngine>();
        var waiting = await engine.RecoverWorkflowAsync(savedParent.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);
        Assert.NotNull((await recoveredStore.GetWorkflowInstanceAsync(savedParent.InstanceId))?.NextDueAtUtc);

        var unresolvedChild = await engine.RecoverWorkflowAsync(savedChild.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, unresolvedChild.Status);
        var unresolvedParent = await engine.RecoverWorkflowAsync(savedParent.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, unresolvedParent.Status);
        var parentPending = Assert.Single(await engine.GetPendingActivitiesAsync(savedParent.InstanceId));
        Assert.False(parentPending.CanResolve);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ResolveActivityAsync(
            savedParent.InstanceId, parentPending.InvocationId,
            ActivityResolution.CompletedInvocation(new ChildData { Approved = false })));

        var childPending = Assert.Single(await engine.GetPendingActivitiesAsync(savedChild.InstanceId));
        var completedChild = await engine.ResolveActivityAsync(savedChild.InstanceId,
            childPending.InvocationId,
            ActivityResolution.Completed(new Dictionary<string, object?>()));
        Assert.Equal(WorkflowExecutionStatus.Success, completedChild.Status);
        var dueParent = (await recoveredStore.GetWorkflowInstanceAsync(savedParent.InstanceId))!;
        dueParent.NextDueAtUtc = DateTime.UtcNow.AddSeconds(-1);
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await recoveredStore.CommitWorkflowInstanceAsync(dueParent, dueParent.Revision,
                Guid.NewGuid().ToString("N"))).Status);
        using var scanner = new WorkflowRecoveryService(
            recoveredProvider.GetRequiredService<IServiceScopeFactory>(), recoveredStore,
            NullLogger<WorkflowRecoveryService>.Instance);
        await scanner.RecoverOnceAsync(CancellationToken.None);
        var completedParent = (await recoveredStore.GetWorkflowInstanceAsync(savedParent.InstanceId))!;
        Assert.Equal(WorkflowStatus.Completed, completedParent.Status);
        Assert.True(JsonSerializer.Deserialize<ParentData>(completedParent.WorkflowDataJson)!.ChildApproved);
    }

    [Fact]
    public async Task ParentInvocationCanBeResolvedAfterChildIsTerminal()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var child = Workflow.Create<ChildData>("TerminalResolutionChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("TerminalResolutionParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("TerminalResolutionChild", 1, step => step
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        var childId = await ChildIdAsync(services, started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await engine.ResumeWorkflowAsync(childId, "approval", new Approval())).Status);
        var savedParent = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        var checkpoint = ExecutionCheckpoint.Read(savedParent.ExecutionStateJson);
        checkpoint.Waits.Clear();
        var continuation = Assert.Single(checkpoint.Continuations);
        continuation.Status = ContinuationStatus.WaitingResolution;
        continuation.PendingActivity!.ResolutionReason = "Invocation output needs resolution";
        savedParent.Status = WorkflowStatus.NeedsResolution;
        savedParent.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await repository.CommitWorkflowInstanceAsync(savedParent, savedParent.Revision,
                Guid.NewGuid().ToString("N"))).Status);

        var pending = Assert.Single(await engine.GetPendingActivitiesAsync(started.InstanceId));
        Assert.True(pending.CanResolve);
        var resolved = await engine.ResolveActivityAsync(started.InstanceId,
            pending.InvocationId,
            ActivityResolution.CompletedInvocation(new ChildData { Approved = true }));

        Assert.Equal(WorkflowExecutionStatus.Success, resolved.Status);
        Assert.True(Assert.IsType<ParentData>(resolved.WorkflowData).ChildApproved);
    }

    [Fact]
    public async Task InvocationInputsAcceptDerivedResumeEvent()
    {
        using var services = Services();
        var child = Workflow.Create<ChildData>("EventInputChild")
            .Step<NoopActivity>().Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("DerivedEventInputParent")
            .Step<NoopActivity>()
            .WaitFor<BaseApproval>("approval")
            .Invoke<ChildData>("EventInputChild", 1, step => step
                .Input(data => data.Approved).From(ctx => ctx.PreviousStep.Approved)
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.ChildApproved))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var waiting = await engine.ExecuteWorkflowAsync(parent, new ParentData());

        var result = await engine.ResumeWorkflowAsync(waiting.InstanceId, "approval",
            new DerivedApproval { Approved = true });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(Assert.IsType<ParentData>(result.WorkflowData).ChildApproved);
    }

    [Fact]
    public async Task ChildWaitTimeoutWakesAndCompletesParent()
    {
        using var services = Services();
        var child = Workflow.Create<ChildData>("TimedChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval", configure: wait => wait
                .TimeoutAfter(TimeSpan.FromMilliseconds(80))
                .OnTimeout(path => path.Step<MarkChildTimeoutActivity>(step => step
                    .Output(activity => activity.TimedOut).To(ctx => ctx.WorkflowData.TimedOut))))
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("TimedChildParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("TimedChild", 1, step => step
                .Output(data => data.TimedOut).To(ctx => ctx.WorkflowData.ChildTimedOut))
            .Build();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        await Task.Delay(160);
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        using var scanner = new WorkflowRecoveryService(
            services.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance);

        await scanner.RecoverOnceAsync(CancellationToken.None);
        await scanner.RecoverOnceAsync(CancellationToken.None);

        var saved = await repository.GetWorkflowInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        Assert.True(JsonSerializer.Deserialize<ParentData>(saved.WorkflowDataJson)!.ChildTimedOut);
    }

    [Fact]
    public async Task ParentCatchMatchesChildExceptionType()
    {
        using var services = Services();
        var child = Workflow.Create<ChildData>("FailingChild")
            .Step<ThrowChildActivity>()
            .Build();
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(child);
        var parent = Workflow.Create<ParentData>("CatchingParent")
            .Step<NoopActivity>()
            .Try(body => body.Invoke<ChildData>("FailingChild", 1, _ => { }))
            .Catch<ChildFailureException, MessageFault>(body => body
                .Step<CaptureFaultActivity>(step => step
                    .Input(activity => activity.Message).From(ctx => ctx.Fault.Message)
                    .Output(activity => activity.Message).To(ctx => ctx.WorkflowData.ChildError)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(parent, new ParentData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("child failed", Assert.IsType<ParentData>(result.WorkflowData).ChildError);
    }

    private static ServiceProvider Services(IWorkflowStateRepository? repository = null,
        ActiveChildProbe? activeChildProbe = null)
    {
        var registrations = new ServiceCollection();
        registrations.AddLogging();
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

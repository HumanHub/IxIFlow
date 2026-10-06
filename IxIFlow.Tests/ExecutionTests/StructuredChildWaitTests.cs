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

    [Fact]
    public async Task RecoveryStartsChildWhenParentStartWasSavedBeforeChildWasCreated()
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

        var recovered = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .RecoverWorkflowAsync(started.InstanceId);

        Assert.Equal(WorkflowExecutionStatus.Suspended, recovered.Status);
        var childId = await ChildIdAsync(secondProvider, started.InstanceId);
        Assert.Equal(WorkflowStatus.Suspended,
            (await recoveredStore.GetWorkflowInstanceAsync(childId))!.Status);
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

    private static ServiceProvider Services(IWorkflowStateRepository? repository = null)
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
}

using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// Core engine scenarios that must behave the same in every profile (spec J-02).
/// </summary>
[Trait("Spec", "J-02")]
public sealed class CoreScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CompletedWorkflowIsSavedWithItsData(string profile)
    {
        await using var cluster = await CreateClusterAsync(profile);
        var engine = cluster.AddHost();
        var definition = Workflow.Create<CounterData>("ConformanceComplete")
            .Step<IncrementActivity>(step => step
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Count)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Count))
            .Build();

        var result = await engine.RunAsync(definition, new CounterData { Count = 41 });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(42, ((CounterData)result.WorkflowData!).Count);
        var saved = await cluster.GetInstanceAsync(result.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task SuspendedWorkflowResumesOnAnotherHost(string profile)
    {
        await using var cluster = await CreateClusterAsync(profile);
        var first = cluster.AddHost();
        var second = cluster.AddHost();
        var definition = ApprovalWorkflow();
        await cluster.Registry.RegisterWorkflowAsync(definition);

        var started = await first.RunAsync(definition, new CounterData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var resumed = await second.ResumeAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.Equal(1, ((CounterData)resumed.WorkflowData!).Count);
        Assert.Equal(WorkflowStatus.Completed, (await cluster.GetInstanceAsync(started.InstanceId))!.Status);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ConcurrentResumesOfOneDeliveryApplyOnce(string profile)
    {
        var counter = new ExecutionCounter();
        await using var cluster = await CreateClusterAsync(profile, services => services.AddSingleton(counter));
        var first = cluster.AddHost();
        var second = cluster.AddHost();
        var definition = Workflow.Create<CounterData>("ConformanceResumeOnce")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Step<CountExecutionActivity>()
            .WaitFor<Approval>("approval")
            .Build();
        await cluster.Registry.RegisterWorkflowAsync(definition);
        var started = await first.RunAsync(definition, new CounterData());

        await Task.WhenAll(
            first.ResumeAsync(started.InstanceId, "approval", new Approval(), "delivery-1"),
            second.ResumeAsync(started.InstanceId, "approval", new Approval(), "delivery-1"));
        Assert.Equal(1, counter.Count);

        await first.ResumeAsync(started.InstanceId, "approval", new Approval(), "delivery-1");
        Assert.Equal(1, counter.Count);
        Assert.Equal(WorkflowStatus.Suspended, (await cluster.GetInstanceAsync(started.InstanceId))!.Status);

        var completed = await second.ResumeAsync(
            started.InstanceId, "approval", new Approval(), "delivery-2");
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, counter.Count);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CancelledSuspendedWorkflowCannotBeResumed(string profile)
    {
        await using var cluster = await CreateClusterAsync(profile);
        var first = cluster.AddHost();
        var second = cluster.AddHost();
        var definition = ApprovalWorkflow();
        await cluster.Registry.RegisterWorkflowAsync(definition);
        var started = await first.RunAsync(definition, new CounterData());

        var cancelled = await second.CancelAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "conformance" });
        var resumed = await first.ResumeAsync(started.InstanceId, "approval", new Approval());

        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        Assert.NotEqual(WorkflowExecutionStatus.Success, resumed.Status);
        var saved = await cluster.GetInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.Cancelled, saved!.Status);
    }

    [Theory]
    [Trait("Spec", "D-06")]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task EventOnAnotherHostCompletesChildAndWakesParent(string profile)
    {
        // The child's event arrives on a second host, as a webhook landing on another machine would.
        await using var cluster = await CreateClusterAsync(profile);
        var engine = cluster.AddHost();
        var receivingHost = cluster.AddHost();
        var child = Workflow.Create<CounterData>("ConformanceChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Step<IncrementActivity>(step => step
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Count)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Count))
            .Build();
        var parent = Workflow.Create<CounterData>("ConformanceParent")
            .Step<NoopActivity>()
            .Invoke<CounterData>("ConformanceChild", 1, step => step
                .Output(data => data.Count).To(ctx => ctx.WorkflowData.Count))
            .Build();
        await cluster.Registry.RegisterWorkflowAsync(child);
        await cluster.Registry.RegisterWorkflowAsync(parent);
        var started = await engine.RunAsync(parent, new CounterData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var childId = ExecutionCheckpoint.Read((await cluster.GetInstanceAsync(started.InstanceId))!.ExecutionStateJson)
            .Continuations.Single(item => item.PendingActivity?.ChildWorkflowId != null)
            .PendingActivity!.ChildWorkflowId!;

        var childResult = await receivingHost.ResumeAsync(childId, "approval", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, childResult.Status);
        await cluster.RecoverOnceAsync();

        var saved = await cluster.GetInstanceAsync(started.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        Assert.Equal(1, System.Text.Json.JsonSerializer.Deserialize<CounterData>(saved.WorkflowDataJson)!.Count);
    }

    private static Task<ConformanceCluster> CreateClusterAsync(string profile,
        Action<IServiceCollection>? configure = null) =>
        ConformanceCluster.CreateAsync(profile, services =>
        {
            services.AddTransient<NoopActivity>();
            services.AddTransient<IncrementActivity>();
            services.AddTransient<CountExecutionActivity>();
            configure?.Invoke(services);
        });

    private static WorkflowDefinition ApprovalWorkflow() =>
        Workflow.Create<CounterData>("ConformanceApproval")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval")
            .Step<IncrementActivity>(step => step
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Count)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Count))
            .Build();

    public sealed class CounterData
    {
        public int Count { get; set; }
    }

    public sealed class Approval
    {
    }

    public sealed class ExecutionCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class IncrementActivity : IAsyncActivity
    {
        public int Value { get; set; }
        public int Result { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Value + 1;
            return Task.CompletedTask;
        }
    }

    public sealed class CountExecutionActivity(ExecutionCounter counter) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            counter.Increment();
            return Task.CompletedTask;
        }
    }
}

using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// Two hosts with their own repositories and in-process gates resume one suspended instance.
/// A delivery is applied once, and a redelivered event does not satisfy the next wait with the same key.
/// </summary>
internal static class ResumeOnceScenario
{
    public static async Task RunAsync(IWorkflowStateRepository firstRepository,
        IWorkflowStateRepository secondRepository, string instanceId)
    {
        var registry = new WorkflowVersionRegistry();
        var counter = new ResumeCounter();
        using var firstHost = CreateHost(firstRepository, registry, counter);
        using var secondHost = CreateHost(secondRepository, registry, counter);
        var definition = Workflow.Create<ResumeOnceData>("ResumeOnce")
            .Step<ResumeOnceSeedActivity>()
            .WaitFor<ResumeOnceApproval>("approval")
            .Step<CountResumeActivity>()
            .WaitFor<ResumeOnceApproval>("approval")
            .Build();
        await registry.RegisterWorkflowAsync(definition);
        var firstEngine = firstHost.GetRequiredService<IWorkflowEngine>();
        var secondEngine = secondHost.GetRequiredService<IWorkflowEngine>();

        var started = await firstEngine.ExecuteWorkflowAsync(definition, new ResumeOnceData(),
            new WorkflowOptions { InstanceId = instanceId });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        await Task.WhenAll(
            firstEngine.ResumeWorkflowDeliveryAsync(instanceId, "approval", new ResumeOnceApproval(), "delivery-1"),
            secondEngine.ResumeWorkflowDeliveryAsync(instanceId, "approval", new ResumeOnceApproval(), "delivery-1"));
        Assert.Equal(1, counter.Count);
        Assert.Equal(WorkflowStatus.Suspended, (await firstRepository.GetWorkflowInstanceAsync(instanceId))!.Status);

        await firstEngine.ResumeWorkflowDeliveryAsync(instanceId, "approval", new ResumeOnceApproval(), "delivery-1");
        Assert.Equal(1, counter.Count);
        Assert.Equal(WorkflowStatus.Suspended, (await secondRepository.GetWorkflowInstanceAsync(instanceId))!.Status);

        var completed = await secondEngine.ResumeWorkflowDeliveryAsync(
            instanceId, "approval", new ResumeOnceApproval(), "delivery-2");
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, counter.Count);
    }

    private static ServiceProvider CreateHost(IWorkflowStateRepository repository,
        IWorkflowVersionRegistry registry, ResumeCounter counter)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton(repository));
        services.Replace(ServiceDescriptor.Singleton(registry));
        services.Replace(ServiceDescriptor.Singleton(new InProcessInstanceGate()));
        services.AddSingleton(counter);
        services.AddTransient<ResumeOnceSeedActivity>();
        services.AddTransient<CountResumeActivity>();
        return services.BuildServiceProvider();
    }
}

public sealed class ResumeCounter
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public void Increment() => Interlocked.Increment(ref _count);
}

public sealed class ResumeOnceData
{
}

public sealed class ResumeOnceApproval
{
}

public sealed class ResumeOnceSeedActivity : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class CountResumeActivity(ResumeCounter counter) : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        counter.Increment();
        return Task.CompletedTask;
    }
}

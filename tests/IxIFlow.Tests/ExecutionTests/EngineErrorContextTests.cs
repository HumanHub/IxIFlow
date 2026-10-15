using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Engine failures name the instance, and the step and attempt when they apply (spec A-10).
/// </summary>
[Trait("Feature", "recovery")]
[Trait("Spec", "A-10")]
public sealed class EngineErrorContextTests
{
    [Fact]
    public async Task StoreFailureNamesTheInstanceStepAndAttempt()
    {
        var commits = 0;
        var repository = new FaultInjectingRepository { FailCommit = _ => ++commits >= 2 };
        using var services = Services(repository);
        var definition = Workflow.Create<ContextData>("ContextStoreFailure")
            .Step<NoopActivity>()
            .Build();

        var error = await EngineAssert.StoreFailureAsync(() => services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new ContextData()));

        var saved = Assert.Single(await repository.GetWorkflowInstancesByNameAsync("ContextStoreFailure"));
        Assert.Equal(saved.InstanceId, error.InstanceId);
        Assert.Equal("root/0", error.StepId);
        Assert.NotNull(error.AttemptId);
        Assert.Contains($"instance '{saved.InstanceId}'", error.Message);
        Assert.Contains($"attempt '{error.AttemptId}'", error.Message);
    }

    [Fact]
    public async Task RequestForAMissingInstanceNamesTheInstance()
    {
        using var services = Services(new FaultInjectingRepository());
        var instanceId = Guid.NewGuid().ToString("N");

        var error = await EngineAssert.FailsAsync(WorkflowErrorKind.InvalidRequest,
            () => services.GetRequiredService<IWorkflowEngine>().CancelWorkflowAsync(instanceId,
                new CancellationReason { ReasonCode = "withdrawn" }));

        Assert.Equal(instanceId, error.InstanceId);
        Assert.Null(error.StepId);
        Assert.Null(error.AttemptId);
    }

    private static ServiceProvider Services(IWorkflowStateRepository repository)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(repository);
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        return services.BuildServiceProvider();
    }

    public sealed class ContextData;

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

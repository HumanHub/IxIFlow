using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json.Nodes;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The execution snapshot is a view (A-08): it is not stored with the instance, and the client computes it from
/// the saved checkpoint and the registered definition when asked.
/// </summary>
[Trait("Feature", "durability")]
public sealed class SnapshotViewTests
{
    [Fact]
    [Trait("Spec", "A-08")]
    public async Task ASnapshotIsComputedWhenAskedAndNotStored()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var repository = new InMemoryWorkflowStateRepository(time);
        using var services = Services(repository, time);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(Waiting("SnapshotView"), new ViewData());
        time.Advance(TimeSpan.FromMinutes(5));

        var snapshot = await engine.GetSnapshotAsync(started.InstanceId);

        Assert.NotNull(snapshot);
        Assert.Equal(WorkflowStatus.Suspended, snapshot.Status);
        Assert.Equal("Waiting", Assert.Single(snapshot.Pointers).Status);
        Assert.Equal(time.GetUtcNow().UtcDateTime, snapshot.CapturedAtUtc);
        var stored = await repository.GetWorkflowInstanceAsync(started.InstanceId);
        Assert.DoesNotContain("Pointers", System.Text.Json.JsonSerializer.Serialize(stored));
    }

    [Fact]
    [Trait("Spec", "A-08")]
    public async Task AnInstanceThatDoesNotExistHasNoSnapshot()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), TimeProvider.System);

        Assert.Null(await services.GetRequiredService<IWorkflowClient>().GetSnapshotAsync("no-such-instance"));
    }

    [Fact]
    [Trait("Spec", "A-08")]
    public async Task ASnapshotNeedsTheDefinitionTheInstanceWasSavedWith()
    {
        var repository = new InMemoryWorkflowStateRepository();
        string instanceId;
        using (var first = Services(repository, TimeProvider.System))
            instanceId = (await first.GetRequiredService<IWorkflowClient>()
                .RunAsync(Waiting("SnapshotDefinition"), new ViewData())).InstanceId;

        // A host that has not registered the definition, then a mismatched stored checkpoint.
        using var unregistered = Services(repository, TimeProvider.System);
        var missing = await EngineAssert.FailsAsync(WorkflowErrorKind.ArtifactUnavailable,
            () => unregistered.GetRequiredService<IWorkflowClient>().GetSnapshotAsync(instanceId));
        Assert.Contains("is unavailable", missing.Message);

        using var changed = Services(repository, TimeProvider.System);
        var definition = Workflow.Create<ViewData>("SnapshotDefinition").Step<ViewActivity>().Step<ViewActivity>()
            .WaitFor<ViewEvent>("go").Build();
        var catalog = changed.GetRequiredService<IWorkflowCatalog>();
        var registration = await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.RegisterWorkflowAsync(definition));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, registration.Kind);
        await catalog.RegisterWorkflowAsync(Waiting("SnapshotDefinition"));

        var instance = (await repository.GetWorkflowInstanceAsync(instanceId))!;
        var checkpoint = JsonNode.Parse(instance.ExecutionStateJson)!;
        checkpoint["StructureHash"] = new WorkflowScopeCatalog(definition).StructureHash;
        instance.ExecutionStateJson = checkpoint.ToJsonString();
        await repository.SeedAsync(instance);
        var checkpointJson = (await repository.GetWorkflowInstanceAsync(instanceId))!.ExecutionStateJson;
        var differs = await EngineAssert.FailsAsync(WorkflowErrorKind.ArtifactUnavailable,
            () => changed.GetRequiredService<IWorkflowClient>().GetSnapshotAsync(instanceId));
        Assert.Contains("differs from the saved version", differs.Message);
        Assert.Equal(checkpointJson, (await repository.GetWorkflowInstanceAsync(instanceId))!.ExecutionStateJson);
    }

    private static WorkflowDefinition Waiting(string name) =>
        Workflow.Create<ViewData>(name).Step<ViewActivity>().WaitFor<ViewEvent>("go").Build();

    private static ServiceProvider Services(IWorkflowStateRepository repository, TimeProvider time)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(time);
        services.AddIxIFlow();
        services.AddSingleton(repository);
        return services.BuildServiceProvider();
    }

    public sealed class ViewData
    {
        public int Value { get; set; }
    }

    public sealed class ViewEvent;

    public sealed class ViewActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

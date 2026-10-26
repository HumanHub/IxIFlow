using IxIFlow.Authoring;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "F-17")]
[Trait("Spec", "F-18")]
[Trait("Feature", "registration")]
public sealed class PublishedInvokeScenarios
{
    private static async Task<(WorkflowVersion Callee, WorkflowVersion Caller)> PublishAsync(IWorkflowStateRepository store)
    {
        var options = new WorkflowPublisherOptions
        {
            SourceOptions = new() { Types = { ["Data"] = "global::" + typeof(FirstData).FullName } }
        };
        var publisher = new WorkflowPublisher(store, store, options);
        async Task<WorkflowVersion> Publish(string name, string yaml)
        {
            var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow", yaml), null);
            var result = await publisher.PublishAsync(draft.Id);
            Assert.True(result.Success, string.Join("; ", result.Diagnostics));
            return result.Workflow!;
        }
        var callee = await Publish("Callee", """
            workflow: {name: Callee, version: 1, dataType: Data, input: [Value], output: [Value]}
            definitions: []
            """);
        // The publisher must resolve the callee from the catalog, without an executable registration.
        var caller = await Publish("Caller", """
            workflow: {name: Caller, version: 1, dataType: Data}
            definitions:
              - kind: invokeWorkflow
                id: child
                workflow: {name: Callee}
                input: [{target: Value, from: {kind: path, path: workflow.Value}}]
                output: [{source: Value, to: {path: workflow.Result}}]
            """);
        return (callee, caller);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PublishCatalogCallerAndInvokeItOnAColdHost(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var publishing = database.NewRepository();
        var (callee, caller) = await PublishAsync(publishing);
        if (publishing is IAsyncDisposable disposable) await disposable.DisposeAsync();
        var cold = database.NewRepository();
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.PublishedInvokeConformance", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(Directory.Exists(cache));
            await using var host = PublishedBundleScenario.Host(cold, cache, configure: database.ConfigureHost);
            await database.StartHostLinksAsync(host);
            using var definition = await host.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync("Caller", 1);
            var call = Assert.Single(definition.Definition.Steps);
            Assert.Equal(callee.ContractJson, call.InvokedContract!.ToJson());
            var client = host.GetRequiredService<IWorkflowClient>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var id = await client.StartAsync("Caller", 1, new FirstData { Value = "cold catalog child" }, cancellationToken: timeout.Token);
            var result = await client.AwaitAsync(id, WorkflowAwaitCondition.Finished, timeout.Token);
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal("cold catalog child", result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));
            Assert.NotSame(typeof(FirstData).Assembly, result.WorkflowData.GetType().Assembly);
            Assert.Equal(caller.ArtifactDigest, (await cold.GetWorkflowInstanceAsync(id))!.BundleDigest);
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PublishedChildDependencyKindAndRetirementGuardsSurviveStoreReopen(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var first = database.NewRepository();
        var (callee, caller) = await PublishAsync(first);
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = caller.Name, WorkflowVersion = caller.Version,
            Status = WorkflowStatus.Suspended, ExecutionStateJson = StoreTestSupport.CheckpointJson,
            WorkflowDataJson = StoredJson.Inline("{}"), WorkflowDataType = WorkflowTypeIdentity.SavedName(typeof(FirstData)),
            BundleDigest = caller.ArtifactDigest
        };
        await first.SeedAsync(instance, wakeAfter: TimeSpan.Zero);
        if (first is IAsyncDisposable disposable) await disposable.DisposeAsync();

        var reopened = database.NewRepository();
        var saved = (await reopened.GetWorkflowVersionAsync(caller.Name, caller.Version))!;
        Assert.Equal(new WorkflowVersionDependency(callee.Name, callee.Version, WorkflowDependencyKind.Child), Assert.Single(saved.Dependencies!));
        var changed = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.RegisterWorkflowVersionAsync(saved with { Dependencies = [] }));
        Assert.Contains("immutable", changed.Message);
        foreach (var state in new[] { WorkflowVersionState.Published, WorkflowVersionState.Deprecated })
        {
            await reopened.SetWorkflowVersionStateAsync(caller.Name, caller.Version, state, new CatalogMutationToken((await reopened.GetWorkflowVersionAsync(caller.Name, caller.Version))!.LifecycleRevision));
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(async () => await reopened.SetWorkflowVersionStateAsync(callee.Name, callee.Version, WorkflowVersionState.Retired, new CatalogMutationToken((await reopened.GetWorkflowVersionAsync(callee.Name, callee.Version))!.LifecycleRevision)));
            Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
            Assert.Contains("required by caller Caller v1", error.Message);
            Assert.Equal(WorkflowVersionState.Published, (await reopened.GetWorkflowVersionAsync(callee.Name, callee.Version))!.State);
        }
        await reopened.SetWorkflowVersionStateAsync(caller.Name, caller.Version, WorkflowVersionState.Retired, new CatalogMutationToken((await reopened.GetWorkflowVersionAsync(caller.Name, caller.Version))!.LifecycleRevision));
        if (reopened is IAsyncDisposable reopenedDisposable) await reopenedDisposable.DisposeAsync();

        var retired = database.NewRepository();
        var retiredCaller = (await retired.GetWorkflowVersionAsync(caller.Name, caller.Version))!;
        Assert.Equal(WorkflowVersionState.Retired, retiredCaller.State);
        Assert.Equal(WorkflowDependencyKind.Child, Assert.Single(retiredCaller.Dependencies!).Kind);
        var unfinished = await Assert.ThrowsAsync<WorkflowEngineException>(async () => await retired.SetWorkflowVersionStateAsync(callee.Name, callee.Version, WorkflowVersionState.Retired, new CatalogMutationToken((await retired.GetWorkflowVersionAsync(callee.Name, callee.Version))!.LifecycleRevision)));
        Assert.Equal(WorkflowErrorKind.InvalidState, unfinished.Kind);
        Assert.Contains("unfinished parent instances of Caller v1", unfinished.Message);
        var completed = (await retired.GetWorkflowInstanceAsync(instance.InstanceId))!;
        completed.Status = WorkflowStatus.Completed;
        await retired.SeedAsync(completed);
        await retired.SetWorkflowVersionStateAsync(callee.Name, callee.Version, WorkflowVersionState.Retired, new CatalogMutationToken((await retired.GetWorkflowVersionAsync(callee.Name, callee.Version))!.LifecycleRevision));
        Assert.Equal(WorkflowVersionState.Retired, (await retired.GetWorkflowVersionAsync(callee.Name, callee.Version))!.State);
    }
}

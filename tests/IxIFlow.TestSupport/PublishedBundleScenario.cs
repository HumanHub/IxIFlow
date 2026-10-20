using IxIFlow.Authoring;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Infrastructure;

public static class PublishedBundleScenario
{
    public static async Task<WorkflowVersion> PublishParentAsync(IWorkflowStateRepository store)
    {
        const string yaml = """
            workflow:
              name: BundleParent
              version: 1
              dataType: Data
            definitions:
              - kind: invokeWorkflow
                workflow: {name: BundleLifecycle, version: 1}
                input:
                  - target: Value
                    from: {kind: path, path: workflow.Value}
                output:
                  - source: Result
                    to: {kind: path, path: workflow.Result}
            """;
        var options = new WorkflowPublisherOptions();
        options.SourceOptions.Types["Data"] = "global::" + typeof(FirstData).FullName;
        options.SourceOptions.Workflows["BundleLifecycle@1"] = new("global::" + typeof(FirstData).FullName, 1);
        var draft = await store.SaveDefinitionAsync(new("BundleParent", 1, "workflow", yaml), 0);
        var result = await new WorkflowPublisher(store, store, options).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return result.Version!;
    }

    public static async Task<WorkflowVersion> PublishAsync(IWorkflowStateRepository store, int version = 1)
    {
        var yaml = $$"""
            workflow:
              name: BundleLifecycle
              version: {{version}}
              dataType: Data
            definitions:
              - kind: suspend
                id: signal
                reason: continue
                event: Signal
                output:
                  - source: Value
                    to: {kind: path, path: workflow.Result}
            """;
        var options = new WorkflowPublisherOptions();
        options.SourceOptions.Types["Data"] = "global::" + (version == 1 ? typeof(FirstData) : typeof(SecondData)).FullName;
        options.SourceOptions.Types["Signal"] = "global::" + typeof(Signal).FullName;
        options.SourceOptions.Members["Data.Result"] = "Result";
        options.SourceOptions.Members["Signal.Value"] = "Value";
        var draft = await store.SaveDefinitionAsync(new("BundleLifecycle", version, "workflow", yaml), 0);
        var result = await new WorkflowPublisher(store, store, options).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return result.Version!;
    }

    public static ServiceProvider Host(IWorkflowStateRepository store, string cache, int capacity = 16,
        IArtifactStore? artifacts = null, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddIxIFlow().UseWorkflowBundles(cache, capacity);
        services.AddSingleton(store);
        if (artifacts != null) services.AddSingleton(artifacts);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public static async Task CrossHostAsync(Func<IWorkflowStateRepository> newRepository, string cacheRoot,
        Action<IServiceCollection>? configure = null, Func<IServiceProvider, Task>? startLinks = null)
    {
        var first = newRepository();
        var version = await PublishAsync(first);
        string id;
        await using (var host = Host(first, Path.Combine(cacheRoot, "first"), configure: configure))
        {
            var client = host.GetRequiredService<IWorkflowClient>();
            if (startLinks != null) await startLinks(host);
            id = await client.StartAsync(version.Name, 1, new FirstData { Value = "input" });
            await UntilAsync(first, id, WorkflowStatus.Suspended);
            Assert.Equal(version.ArtifactDigest, (await first.GetWorkflowInstanceAsync(id))!.BundleDigest);
        }
        if (first is IAsyncDisposable disposable) await disposable.DisposeAsync();
        var second = newRepository();
        await using (var host = Host(second, Path.Combine(cacheRoot, "second"), configure: configure))
        {
            var client = host.GetRequiredService<IWorkflowClient>();
            if (startLinks != null) await startLinks(host);
            var result = await client.ResumeAsync(id, new Signal { Value = "completed on second host" });
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal("completed on second host", result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));
            Assert.NotSame(typeof(FirstData).Assembly, result.WorkflowData.GetType().Assembly);
            Assert.Equal(version.ArtifactDigest, (await second.GetWorkflowInstanceAsync(id))!.BundleDigest);
        }
        if (second is IAsyncDisposable secondDisposable) await secondDisposable.DisposeAsync();
    }

    public static async Task UntilAsync(IWorkflowStateRepository store, string id, WorkflowStatus status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while ((await store.GetWorkflowInstanceSummaryAsync(id))?.Status != status) await Task.Delay(10, timeout.Token);
    }
}

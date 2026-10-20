using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class BundleDebugPayloadMergeTests
{
    private const string Secret = "bundle-inspection-secret";
    private static string Folder() => Path.Combine(Path.GetTempPath(), "BundleDebugMerge", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublishedDebuggingEditsStepsHydratesAndReplaysAcrossHosts(bool sqlite, bool payloads)
    {
        var root = Folder();
        Directory.CreateDirectory(root);
        IWorkflowStateRepository store = sqlite
            ? new SqliteWorkflowStateRepository(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "state.db") }.ConnectionString)
            : new InMemoryWorkflowStateRepository();
        try
        {
            var version = await PublishAsync(store);
            var id = Guid.NewGuid().ToString("N");
            var large = new string('x', 4096);
            await using (var first = Host(store, Path.Combine(root, "first"), payloads))
            {
                var ops = first.GetRequiredService<IWorkflowOperations>();
                Assert.Equal(id, await ops.LaunchAsync(id, version.Name, 1,
                    JsonSerializer.SerializeToElement(new DebugData { Value = large, Secret = Secret }), true, "launch"));
                await first.GetRequiredService<IWorkflowClient>().AwaitAsync(id);
                var saved = (await store.GetWorkflowInstanceAsync(id))!;
                Assert.Equal(WorkflowStatus.Paused, saved.Status);
                Assert.Equal(version.ArtifactDigest, saved.BundleDigest);
                Assert.Equal(payloads, saved.WorkflowDataJson.Reference != null);
                var inspection = (await ops.InspectAsync(id))!;
                Assert.DoesNotContain(Secret, JsonSerializer.Serialize(inspection));
                Assert.Equal(large, Assert.Single(inspection.Data).Value.GetProperty("Value").GetString());
                var table = await ops.GetStepTableAsync(id);
                Assert.Equal(3, table.Count);
                var source = Assert.Single(table.Select(step => step.SourceLocation!.Split(':')[0]).Distinct());
                Assert.Contains("BundleDebugMerge", await ops.GetSourceAsync(id, source));
                await ops.SetBreakpointsAsync(id, [new("condition", "second",
                    Condition: "context.WorkflowData.Value.Length > 0 && context.PreviousStep.Value.Length > 0")], "breakpoint");
                Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(id,
                    Assert.Single(inspection.Threads).ContinuationId, DebugStepKind.Over, "step")).Status);
                inspection = (await ops.InspectAsync(id))!;
                Assert.Equal("second", Assert.Single(inspection.Threads).AuthorStepId);
                var receipt = await ops.EditDataAsync(id, "edit", inspection.Revision,
                    new(inspection.DebugState.PauseGeneration), [new(["Value"], JsonSerializer.SerializeToElement("edited"))], "editor");
                Assert.Equal(inspection.Revision + 1, receipt.Revision);
                Assert.DoesNotContain(Secret, JsonSerializer.Serialize(await ops.InspectAsync(id)));
            }
            // This host must reacquire the saved digest, previous activity, and every referenced part.
            await using var second = Host(store, Path.Combine(root, "second"), payloads);
            var operations = second.GetRequiredService<IWorkflowOperations>();
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await operations.ContinueAsync(id, "continue")).Status);
            var waiting = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(version.ArtifactDigest, waiting.BundleDigest);
            var client = second.GetRequiredService<IWorkflowClient>();
            var storedWait = Assert.Single(await store.GetOpenWaitsAsync(new(InstanceId: id, Kind: WaitKind.Event)));
            Assert.Equal(payloads, storedWait.DefaultPayloadJson!.Reference != null);
            var wait = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: id)));
            Assert.Equal(large, JsonDocument.Parse(wait.DefaultPayloadJson!).RootElement.GetProperty("Value").GetString());
            Assert.DoesNotContain(Secret, wait.DefaultPayloadJson);
            Assert.Equal(WorkflowExecutionStatus.Paused, (await operations.PauseAsync(id, "idle-pause")).Status);
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await operations.ContinueAsync(id, "idle-continue")).Status);
            var result = await client.ResumeAsync(id, new DebugData { Value = "done" });
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            var type = result.WorkflowData!.GetType();
            Assert.NotSame(typeof(DebugData).Assembly, type.Assembly);
            Assert.Equal("edited", type.GetProperty("Value")!.GetValue(result.WorkflowData));
            Assert.Equal("done", type.GetProperty("Result")!.GetValue(result.WorkflowData));
            Assert.Equal(Secret, type.GetProperty("Secret")!.GetValue(result.WorkflowData));
            var replay = await operations.OpenReplayAsync(id);
            while (replay.StepForward()) { }
            Assert.True(replay.Current.IsCheckpoint);
            Assert.NotNull(replay.Current.Data);
            Assert.DoesNotContain(Secret, JsonSerializer.Serialize(replay.Current));
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionsUseTheRequestedBundleVersionAndDoNotRootItsContext(bool conditions)
    {
        var store = new InMemoryWorkflowStateRepository();
        await PublishAsync(store);
        await PublishAsync(store, 2);
        using var compiler = new BreakpointConditionCompiler();
        var references = CompileVersions(store, compiler, conditions);
        for (var index = 0; index < 100 && references.Any(reference => reference.IsAlive); index++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            // Exercise the runtime's ordinary expiring JSON metadata accessor cache.
            _ = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                .GetTypeInfo(typeof(DebugData), new JsonSerializerOptions());
            await Task.Delay(30);
        }
        Assert.All(references, reference => Assert.False(reference.IsAlive));
        GC.KeepAlive(compiler);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CompileVersions(IWorkflowStateRepository store, BreakpointConditionCompiler compiler, bool conditions)
    {
        using var catalog = new WorkflowCatalog(store, artifacts: (IArtifactStore)store, bundles: new(Folder(), 0));
        using var one = catalog.ResolveWorkflowDefinitionAsync("BundleDebugMerge", 1).GetAwaiter().GetResult();
        using var two = catalog.ResolveWorkflowDefinitionAsync("BundleDebugMerge", 2).GetAwaiter().GetResult();
        var first = one.Definition.WorkflowDataType!;
        var second = two.Definition.WorkflowDataType!;
        Assert.NotSame(first, second);
        if (!conditions) return [new(AssemblyLoadContext.GetLoadContext(first.Assembly)!), new(AssemblyLoadContext.GetLoadContext(second.Assembly)!)];
        var request = new BreakpointConditionRequest("same-structure", "context.WorkflowData.Value == \"first\"", first);
        var a = compiler.Compile(request);
        var b = compiler.Compile(request with { DataType = second });
        Assert.Empty(a.Diagnostics); Assert.Empty(b.Diagnostics);
        var data = Activator.CreateInstance(first)!;
        first.GetProperty("Value")!.SetValue(data, "first");
        Assert.True(a.Evaluate!(data, null, null, null, DateTimeOffset.UtcNow));
        var other = Activator.CreateInstance(second)!;
        second.GetProperty("Value")!.SetValue(other, "first");
        Assert.True(b.Evaluate!(other, null, null, null, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidCastException>(() => a.Evaluate!(other, null, null, null, DateTimeOffset.UtcNow));
        return [new(AssemblyLoadContext.GetLoadContext(first.Assembly)!), new(AssemblyLoadContext.GetLoadContext(second.Assembly)!)];
    }

    private static ServiceProvider Host(IWorkflowStateRepository store, string cache, bool payloads)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(options => { options.Debugging = true; options.PayloadThresholdBytes = payloads ? 128 : 0; })
            .UseWorkflowBundles(cache, 0);
        services.AddSingleton(store);
        services.AddBreakpointConditions();
        return services.BuildServiceProvider();
    }

    private static async Task<WorkflowVersion> PublishAsync(IWorkflowStateRepository store, int version = 1)
    {
        var yaml = $$$"""
            workflow: {name: BundleDebugMerge, version: {{{version}}}, dataType: IxIFlow.BundleFixtures.DebugData}
            definitions:
              - kind: activity
                id: first
                activity: IxIFlow.BundleFixtures.Echo
                input: [{target: Value, from: {kind: path, path: workflow.Value}}]
              - kind: activity
                id: second
                activity: IxIFlow.BundleFixtures.Echo
                input: [{target: Value, from: {kind: path, path: previousStep.Value}}]
                output: [{source: Value, to: {kind: path, path: workflow.Result}}]
              - kind: suspend
                id: signal
                event: IxIFlow.BundleFixtures.DebugData
                defaultPayload: {Value: suggested}
                input:
                  - {target: Value, from: {kind: path, path: workflow.Result}}
                  - {target: Secret, from: {kind: path, path: workflow.Secret}}
                output: [{source: Value, to: {kind: path, path: workflow.Result}}]
            """;
        var draft = await store.SaveDefinitionAsync(new("BundleDebugMerge", version, "workflow", yaml), 0);
        var published = await new WorkflowPublisher(store, (IArtifactStore)store).PublishAsync(draft.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
        return published.Version!;
    }
}

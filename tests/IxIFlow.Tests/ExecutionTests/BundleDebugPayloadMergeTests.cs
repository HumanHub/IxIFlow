using System.Reflection;
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
                await ops.SetBreakpointsAsync(id,
                [
                    new("condition", "second", Condition: "context.WorkflowData.Value.Length == 4096 && context.PreviousStep.Value.Length == 4096"),
                    new("restored-condition", "signal", Condition: "context.WorkflowData.Value == \"edited\" && context.PreviousStep.Value.Length == 4096")
                ], "breakpoint");
                Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.ContinueAsync(id, "reach-condition")).Status);
                inspection = (await ops.InspectAsync(id))!;
                Assert.Equal("second", Assert.Single(inspection.Threads).AuthorStepId);
                AssertConditionStop(inspection, "condition");
                Assert.Equal(1, (await ops.GetBreakpointsAsync(id)).Single(b => b.Id == "condition").HitCount);
                var receipt = await ops.EditDataAsync(id, "edit", inspection.Revision,
                    new(inspection.DebugState.PauseGeneration), [new(["Value"], JsonSerializer.SerializeToElement("edited"))], "editor");
                Assert.Equal(inspection.Revision + 1, receipt.Revision);
                Assert.DoesNotContain(Secret, JsonSerializer.Serialize(await ops.InspectAsync(id)));
            }
            // This host must reacquire the saved digest, previous activity, and every referenced part.
            await using var second = Host(store, Path.Combine(root, "second"), payloads);
            var operations = second.GetRequiredService<IWorkflowOperations>();
            Assert.Equal(WorkflowExecutionStatus.Paused, (await operations.ContinueAsync(id, "restored-condition")).Status);
            var restored = (await operations.InspectAsync(id))!;
            AssertConditionStop(restored, "restored-condition");
            Assert.All(await operations.GetBreakpointsAsync(id), breakpoint => Assert.Equal(1, breakpoint.HitCount));
            // Stepping remains covered separately from the conditional stops.
            Assert.Equal(WorkflowExecutionStatus.Paused, (await operations.StepAsync(id,
                Assert.Single(restored.Threads).ContinuationId, DebugStepKind.Over, "step-wait")).Status);
            Assert.Equal("Blocked", (await operations.InspectAsync(id))!.DebugState.Stop!.Reason);
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
        var listRequest = new BreakpointConditionRequest("generic-bundle-input", "context.WorkflowData.Count == 0",
            typeof(List<>).MakeGenericType(first));
        var list = compiler.Compile(listRequest);
        Assert.Empty(list.Diagnostics);
        Assert.NotNull(list.Evaluate);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.Same(a.Evaluate, compiler.Compile(request).Evaluate);
        Assert.Same(b.Evaluate, compiler.Compile(request with { DataType = second }).Evaluate);
        Assert.Same(list.Evaluate, compiler.Compile(listRequest).Evaluate);
        var data = Activator.CreateInstance(first)!;
        first.GetProperty("Value")!.SetValue(data, "first");
        Assert.True(a.Evaluate!(data, null, null, null, DateTimeOffset.UtcNow));
        var other = Activator.CreateInstance(second)!;
        second.GetProperty("Value")!.SetValue(other, "first");
        Assert.True(b.Evaluate!(other, null, null, null, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidCastException>(() => a.Evaluate!(other, null, null, null, DateTimeOffset.UtcNow));
        return [new(AssemblyLoadContext.GetLoadContext(first.Assembly)!), new(AssemblyLoadContext.GetLoadContext(second.Assembly)!)];
    }

    private static ServiceProvider Host(IWorkflowStateRepository store, string cache, bool payloads,
        IBreakpointConditionCompiler? compiler = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(options => { options.Debugging = true; options.PayloadThresholdBytes = payloads ? 128 : 0; })
            .UseWorkflowBundles(cache, 0);
        services.AddSingleton(store);
        services.AddBreakpointConditions();
        if (compiler != null) services.AddSingleton(compiler);
        return services.BuildServiceProvider();
    }

    private static void AssertConditionStop(InstanceInspection inspection, string id)
    {
        var stop = Assert.IsType<BreakpointStop>(inspection.DebugState.Stop);
        Assert.Equal(id, stop.BreakpointId);
        Assert.Null(stop.Reason);
        Assert.Empty(stop.Diagnostics);
        Assert.Equal(1, stop.HitCount);
    }

    [Fact]
    public async Task FirstPublishedOperationReleasesItsContextWhileTheHostAndProviderStayAlive()
    {
        var store = new InMemoryWorkflowStateRepository();
        await PublishAsync(store);
        await using var services = Host(store, Folder(), false);
        var reference = CompleteOnFreshProvider(services);
        var host = services.GetRequiredService<WorkflowHost>();
        await AssertCollectedAsync([reference]);
        GC.KeepAlive(host);
        GC.KeepAlive(services);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompleteOnFreshProvider(ServiceProvider services)
    {
        var client = services.GetRequiredService<IWorkflowClient>();
        var id = client.StartAsync("BundleDebugMerge", new DebugData { Value = "first" }).GetAwaiter().GetResult();
        Assert.Equal(WorkflowExecutionStatus.Suspended, client.AwaitAsync(id).GetAwaiter().GetResult().Status);
        var result = client.ResumeAsync(id, new DebugData { Value = "done" }).GetAwaiter().GetResult();
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        return new(AssemblyLoadContext.GetLoadContext(result.WorkflowData!.GetType().Assembly)!);
    }

    private static async Task AssertCollectedAsync(WeakReference[] references)
    {
        for (var index = 0; index < 150 && references.Any(reference => reference.IsAlive); index++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            _ = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                .GetTypeInfo(typeof(DebugData), new JsonSerializerOptions());
            await Task.Delay(30);
        }
        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    [Fact]
    public async Task LazilyResolvedRenewerDoesNotCaptureTheBundleTypeScope()
    {
        var store = new InMemoryWorkflowStateRepository();
        await PublishAsync(store);
        await using var services = Host(store, Folder(), false);
        var reference = ResolveRenewerInBundleScope(store, services);
        await AssertCollectedAsync([reference]);
        GC.KeepAlive(services);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ResolveRenewerInBundleScope(IWorkflowStateRepository store, ServiceProvider services)
    {
        using var catalog = new WorkflowCatalog(store, artifacts: (IArtifactStore)store, bundles: new(Folder(), 0));
        using var lease = catalog.ResolveWorkflowDefinitionAsync("BundleDebugMerge", 1).GetAwaiter().GetResult();
        using var types = lease.EnterTypes();
        _ = services.GetRequiredService<ExecutionLeaseRenewer>();
        return new(AssemblyLoadContext.GetLoadContext(lease.Definition.WorkflowDataType!.Assembly)!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecreatedHostEvaluatesClaimCheckedPreviousActivityForTrueAndFalseConditions(bool matches)
    {
        var store = new CrashAfterPreparationStore();
        await PublishAsync(store);
        var id = Guid.NewGuid().ToString("N");
        var large = new string('x', 4096);
        await using (var first = Host(store, Folder(), true))
        {
            var operations = first.GetRequiredService<IWorkflowOperations>();
            await operations.LaunchAsync(id, "BundleDebugMerge", 1,
                JsonSerializer.SerializeToElement(new DebugData { Value = large }), true, "launch");
            await first.GetRequiredService<IWorkflowClient>().AwaitAsync(id);
            await operations.SetBreakpointsAsync(id, [new("restored", "second",
                Condition: "context.PreviousStep.Value == context.WorkflowData.Value && context.PreviousStep.Value.Length " +
                    (matches ? "== 4096" : "== 1"))], "condition");
            store.FailAfterPreparation = true;
            await Assert.ThrowsAsync<WorkflowEngineException>(() => operations.ContinueAsync(id, "prepare"));
        }
        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        var debug = WorkflowDebugRules.Checkpoint(saved);
        Assert.NotNull(debug.Evaluation);
        Assert.Equal(1, Assert.Single(debug.Breakpoints).HitCount);
        var reader = new PayloadReader(async reference => (await store.PayloadStore.GetAsync(reference))!.Value,
            new(20_000_000));
        var checkpoint = ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(saved, reader));
        var previous = Assert.Single(checkpoint.Continuations).Previous!;
        Assert.NotNull(previous.Json.Reference);
        Assert.Equal(large, JsonDocument.Parse(await reader.ReadAsync(previous.Json)).RootElement.GetProperty("Value").GetString());
        store.FailAfterPreparation = false;
        await using var second = Host(store, Folder(), true);
        var result = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(id);
        Assert.Equal(matches ? WorkflowExecutionStatus.Paused : WorkflowExecutionStatus.Suspended, result.Status);
        var operationsAfter = second.GetRequiredService<IWorkflowOperations>();
        var inspection = (await operationsAfter.InspectAsync(id))!;
        if (matches) AssertConditionStop(inspection, "restored");
        else Assert.Null(inspection.DebugState.Stop);
        Assert.Null(WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(id))!).Evaluation);
        Assert.Equal(1, Assert.Single(await operationsAfter.GetBreakpointsAsync(id)).HitCount);
    }

    private sealed class CrashAfterPreparationStore : InMemoryWorkflowStateRepository
    {
        public bool FailAfterPreparation;
        private bool _prepared;
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            if (FailAfterPreparation && _prepared) throw new IOException("Crash after condition preparation");
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (WorkflowDebugRules.Checkpoint(commit.ReadTrunk()).Evaluation != null) _prepared = true;
            return result;
        }
    }

    [Theory]
    [InlineData("context.WorkflowData.Stream.DataAvailable", "DataAvailable")]
    [InlineData("context.WorkflowData.Socket.Available > 0", "Available")]
    [InlineData("context.WorkflowData.Process.HasExited", "HasExited")]
    public void FrameworkPropertiesOutsideTheExplicitAllowlistAreRejected(string text, string member)
    {
        using var compiler = new BreakpointConditionCompiler();
        var compilation = compiler.Compile(new("framework-properties", text, typeof(FrameworkData)));
        Assert.Null(compilation.Evaluate);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "BPC001" &&
            text.Substring(diagnostic.Start, diagnostic.Length).Contains(member, StringComparison.Ordinal));
    }

    public sealed class FrameworkData
    {
        public System.Net.Sockets.NetworkStream Stream { get; set; } = null!;
        public System.Net.Sockets.Socket Socket { get; set; } = null!;
        public System.Diagnostics.Process Process { get; set; } = null!;
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("load")]
    [InlineData("image")]
    public async Task ReferenceClosureFailuresReportPositionedDiagnosticsAndPreserveBreakpoints(string failure)
    {
        var context = new ReferenceFailureContext(failure);
        try
        {
            using var stream = File.OpenRead(typeof(DebugData).Assembly.Location);
            var type = context.LoadFromStream(stream).GetType(typeof(DebugData).FullName!)!;
            using var compiler = new ReferenceFailureCompiler(type, failure == "duplicate");
            var store = new InMemoryWorkflowStateRepository();
            await PublishAsync(store);
            await using var services = Host(store, Folder(), false, compiler);
            var operations = services.GetRequiredService<IWorkflowOperations>();
            var id = Guid.NewGuid().ToString("N");
            await operations.LaunchAsync(id, "BundleDebugMerge", 1,
                JsonSerializer.SerializeToElement(new DebugData { Value = "data" }), true, "launch");
            await services.GetRequiredService<IWorkflowClient>().AwaitAsync(id);
            await operations.SetBreakpointsAsync(id, [new("kept", "second", Condition: "context.WorkflowData.Value.Length > 0")], "set");
            compiler.Fail = true;
            var refused = await Assert.ThrowsAsync<BreakpointConditionException>(() => operations.SetBreakpointsAsync(id,
                [new("replacement", "second", Condition: "true")], "invalid"));
            AssertReferenceDiagnostic(Assert.Single(refused.Diagnostics), "true");
            Assert.Equal("kept", Assert.Single(await operations.GetBreakpointsAsync(id)).Id);
            Assert.Equal(WorkflowExecutionStatus.Paused, (await operations.ContinueAsync(id, "evaluate")).Status);
            var stop = (await operations.InspectAsync(id))!.DebugState.Stop!;
            Assert.Equal("kept", stop.BreakpointId);
            Assert.Equal("ConditionCompilationFailed", stop.Reason);
            Assert.Equal(1, stop.HitCount);
            AssertReferenceDiagnostic(Assert.Single(stop.Diagnostics), "context.WorkflowData.Value.Length > 0");
            Assert.Equal("kept", Assert.Single(await operations.GetBreakpointsAsync(id)).Id);
        }
        finally { context.Unload(); }
    }

    private static void AssertReferenceDiagnostic(BreakpointConditionDiagnostic diagnostic, string text)
    {
        Assert.Equal("BPC003", diagnostic.Code);
        Assert.Equal(0, diagnostic.Start);
        Assert.Equal(text.Length, diagnostic.Length);
        Assert.Equal(1, diagnostic.Line);
        Assert.Equal(1, diagnostic.Column);
    }

    private sealed class ReferenceFailureContext(string failure) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(SensitiveAttribute).Assembly.GetName().Name)
            {
                if (failure == "load") throw new FileLoadException("Reference load failed");
                if (failure == "image") throw new BadImageFormatException("Reference image failed");
            }
            return null;
        }
    }

    private sealed class ReferenceFailureCompiler(Type type, bool duplicate) : IBreakpointConditionCompiler, IDisposable
    {
        private readonly BreakpointConditionCompiler _inner = new();
        public bool Fail;
        public BreakpointConditionCompilation Compile(BreakpointConditionRequest request) => _inner.Compile(Fail
            ? request with { DataType = type, PreviousType = duplicate ? typeof(DebugData) : null, ItemType = null }
            : request);
        public void Dispose() => _inner.Dispose();
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

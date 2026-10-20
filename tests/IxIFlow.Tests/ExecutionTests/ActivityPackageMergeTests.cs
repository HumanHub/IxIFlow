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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

[Collection("Native package isolation")]
[Trait("Feature", "registration")]
[Trait("Spec", "F-12")]
[Trait("Spec", "F-20")]
[Trait("Spec", "G-04")]
public sealed class ActivityPackageMergeTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "IxIFlow.PackageMerge", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedConditionsUsePackageAndTransitiveContractsAsApplicationTypes(bool sqlite)
    {
        var root = Folder();
        Directory.CreateDirectory(root);
        var store = sqlite
            ? (IWorkflowStateRepository)new SqliteWorkflowStateRepository(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "state.db") }.ConnectionString)
            : new InMemoryWorkflowStateRepository();
        try
        {
            var publisher = new WorkflowPublisher(store, (IArtifactStore)store);
            var references = WorkflowCompilation.ReferencePaths().Select(path => MetadataReference.CreateFromFile(path)).ToArray();
            static byte[] Emit(string name, string source, IEnumerable<MetadataReference> references)
            {
                var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                using var output = new MemoryStream();
                var result = compilation.Emit(output);
                Assert.True(result.Success, string.Join("; ", result.Diagnostics));
                return output.ToArray();
            }
            var model = Emit("Microsoft.PackageModels", """
                namespace PackageConditions;
                public sealed class Model { public int Amount { get; set; } }
                """, references);
            // Same simple name as a default-context fixture; only this pinned image has Data.
            var data = Emit("System.Payments", """
                using System.Threading;
                using System.Threading.Tasks;
                using IxIFlow.Core;
                using IxIFlow.ActivitySdk;
                namespace PackageConditions;
                public sealed class Data { public Model Model { get; set; } = new(); }
                [WorkflowActivity("fixture.condition", Version = "1.0")]
                public sealed class Check : IAsyncActivity
                {
                    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
                        => Task.CompletedTask;
                }
                """, references.Append(MetadataReference.CreateFromImage(model)));
            await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.condition-model", "1.0.0",
                new() { ["lib/net10.0/Microsoft.PackageModels.dll"] = model })));
            await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.condition-data", "1.0.0",
                new() { ["lib/net10.0/System.Payments.dll"] = data },
                "<dependency id=\"fixture.condition-model\" version=\"[1.0.0]\" />")));
            const string yaml = """
                workflow: {name: PackageConditions, version: 1, dataType: PackageConditions.Data}
                imports:
                  catalogs: [fixture.condition-data@1.0.0]
                definitions:
                  - kind: activity
                    id: entry
                    activity: fixture.condition
                    activityVersion: "1.0"
                  - kind: activity
                    id: check
                    activity: fixture.condition
                    activityVersion: "1.0"
                  - kind: suspend
                    id: signal
                    event: PackageConditions.Data
                """;
            var draft = await store.SaveDefinitionAsync(new("PackageConditions", 1, "workflow", yaml), 0);
            var published = await publisher.PublishAsync(draft.Id);
            Assert.True(published.Success, string.Join("; ", published.Diagnostics));
            var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(published.Version!.ManifestJson!)!;
            Assert.Equal(new[] { "fixture.condition-data", "fixture.condition-model" }, manifest.ActivityPackages.Select(pin => pin.Id));
            var services = new ServiceCollection();
            services.AddIxIFlow(options => options.Debugging = true).UseWorkflowBundles(Path.Combine(root, "cache"), 0);
            services.AddSingleton(store);
            services.AddBreakpointConditions();
            await using var host = services.BuildServiceProvider();
            using var lease = await host.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync("PackageConditions", 1);
            var type = lease.Definition.WorkflowDataType!;
            var modelType = type.GetProperty("Model")!.PropertyType;
            var context = AssemblyLoadContext.GetLoadContext(type.Assembly)!;
            Assert.NotSame(AssemblyLoadContext.Default, context);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(modelType.Assembly));
            Assert.Equal(typeof(DebugData).Assembly.GetName().Name, type.Assembly.GetName().Name);
            Assert.NotSame(typeof(DebugData).Assembly, type.Assembly);
            Assert.True(WorkflowSharedAssemblies.HasFrameworkIdentities);
            Assert.False(WorkflowSharedAssemblies.IsFrameworkAssembly(type.Assembly.GetName()));
            Assert.False(WorkflowSharedAssemblies.IsFrameworkAssembly(modelType.Assembly.GetName()));
            Assert.True(WorkflowSharedAssemblies.IsFrameworkAssembly(typeof(string).Assembly.GetName()));
            const string condition = "context.WorkflowData.Model.Amount == 42";
            var compiler = host.GetRequiredService<IBreakpointConditionCompiler>();
            var hash = WorkflowDefinitionValidator.Inspect(lease.Definition);
            var compiled = compiler.Compile(new(hash, condition, type));
            Assert.Empty(compiled.Diagnostics);
            Assert.NotNull(compiled.Evaluate);
            var instance = WorkflowJson.Deserialize("{\"Model\":{\"Amount\":42}}", type)!;
            Assert.True(compiled.Evaluate(instance, null, null, null, DateTimeOffset.UtcNow));
            modelType.GetProperty("Amount")!.SetValue(type.GetProperty("Model")!.GetValue(instance), 7);
            Assert.False(compiled.Evaluate(instance, null, null, null, DateTimeOffset.UtcNow));
            var refused = compiler.Compile(new(hash,
                "context.WorkflowData.Model.ToString() == \"42\"", type));
            Assert.Null(refused.Evaluate);
            Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == "BPC001");
            var operations = host.GetRequiredService<IWorkflowOperations>();
            var id = Guid.NewGuid().ToString("N");
            await operations.LaunchAsync(id, "PackageConditions", 1,
                JsonSerializer.SerializeToElement(new { Model = new { Amount = 42 } }), true, "launch");
            await host.GetRequiredService<IWorkflowClient>().AwaitAsync(id);
            Assert.Equal(WorkflowStatus.Paused, (await store.GetWorkflowInstanceAsync(id))!.Status);
            await operations.SetBreakpointsAsync(id, [new("package-condition", "check", Condition: condition)], "set");
            var result = await operations.ContinueAsync(id, "continue");
            Assert.True(result.Status == WorkflowExecutionStatus.Paused,
                $"Expected Paused; received {result.Status}: {result.ErrorType}: {result.ErrorMessage}\n{result.ErrorStackTrace}");
            var inspection = (await operations.InspectAsync(id))!;
            var stop = Assert.IsType<BreakpointStop>(inspection.DebugState.Stop);
            Assert.Equal("package-condition", stop.BreakpointId);
            Assert.Null(stop.Reason);
            Assert.Empty(stop.Diagnostics);
            Assert.Equal(1, stop.HitCount);
            Assert.Equal(published.Version.ArtifactDigest, (await store.GetWorkflowInstanceAsync(id))!.BundleDigest);
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    [NativeTheory]
    [InlineData("none")]
    [InlineData("registration")]
    [InlineData("condition")]
    [InlineData("public")]
    public async Task RetirementReleasesPackageConditionsJsonProfilesAndVerifiedNativeImagesWithFlowAlreadySuppressed(string throwing)
    {
        var store = new InMemoryWorkflowStateRepository();
        await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(ActivityPackageFixture.NativeActivityPackage()));
        await ActivityPackageFixture.PublishAsync(store, yaml: ActivityPackageFixture.Yaml(extraImport: "fixture.native-call@1.0.0")
            .Replace("dataType: Packaged.Data", "dataType: NativeFixture.Data"));
        var cache = Folder();
        var logs = new CleanupLogger();
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(cache, 0), logger: logs);
        using var compiler = new BreakpointConditionCompiler(logs);
        using var secondCompiler = new BreakpointConditionCompiler(logs);
        var references = RetainPackageObjectAfterRetirement(catalog, compiler, secondCompiler, cache, throwing);
        cache = IxIFlow.TestSupport.ExistingPath.Canonicalize(cache);
        for (var index = 0; index < 100 && references.Any(reference => reference.IsAlive); index++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            _ = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                .GetTypeInfo(typeof(DebugData), new JsonSerializerOptions());
            await Task.Delay(30);
        }
        Assert.All(references, reference => Assert.False(reference.IsAlive));
        // The native owner can need one more finalization after the long weak
        // context reference clears. Check modules and verification streams too.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        AssertNativeMapped(cache, false);
        foreach (var native in Directory.GetFiles(Path.Combine(cache, "native"), "*", SearchOption.AllDirectories))
        {
            using (File.Open(native, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(native);
        }
        Assert.Equal(throwing == "none" ? 0 : 1, logs.Messages.Count);
        if (throwing != "none")
        {
            var message = Assert.Single(logs.Messages);
            Assert.Contains("InvalidOperationException", message);
            Assert.Contains(throwing == "public" ? "public handler failed" : throwing + " failed", message);
            Assert.Contains(nameof(CompileAndRetire), message); // Stack text, not an exception object.
        }
        GC.KeepAlive(catalog);
        GC.KeepAlive(compiler);
        GC.KeepAlive(secondCompiler);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RetainPackageObjectAfterRetirement(WorkflowCatalog catalog, BreakpointConditionCompiler compiler,
        BreakpointConditionCompiler secondCompiler, string cache, string throwing)
    {
        var retained = CompileAndRetire(catalog, compiler, secondCompiler, cache, throwing);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(retained.References[0].IsAlive);
        Assert.Equal(42, retained.Data.GetType().GetProperty("NativeValue")!.GetValue(retained.Data));
        AssertNativeMapped(cache, true);
        GC.KeepAlive(retained.Data);
        return retained.References;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference[] References, object Data) CompileAndRetire(WorkflowCatalog catalog, BreakpointConditionCompiler compiler,
        BreakpointConditionCompiler secondCompiler, string cache, string throwing)
    {
        using var lease = catalog.ResolveWorkflowDefinitionAsync("PackageLifecycle", 1).GetAwaiter().GetResult();
        var type = lease.Definition.WorkflowDataType!;
        var context = Assert.IsType<BundleLoadContext>(AssemblyLoadContext.GetLoadContext(type.Assembly));
        var request = new BreakpointConditionRequest(WorkflowDefinitionValidator.Inspect(lease.Definition), "context.WorkflowData.Value.Length == 0", type);
        if (throwing == "registration")
            Assert.True(context.TryRegisterUnloading(_ => throw new InvalidOperationException("registration failed")));
        var compiled = compiler.Compile(request);
        Assert.Empty(compiled.Diagnostics);
        Assert.NotNull(compiled.Evaluate);
        var data = Activator.CreateInstance(type)!;
        Assert.True(compiled.Evaluate(data, null, null, null, DateTimeOffset.UtcNow));
        var nativeRequest = request with { Text = "context.WorkflowData.NativeValue == 42" };
        var nativeCondition = compiler.Compile(nativeRequest);
        Assert.Empty(nativeCondition.Diagnostics);
        Assert.True(nativeCondition.Evaluate!(data, null, null, null, DateTimeOffset.UtcNow));
        var firstConditionContext = AssemblyLoadContext.GetLoadContext(compiled.Evaluate.Method.DeclaringType!.Assembly)!;
        if (throwing == "condition")
            firstConditionContext.Unloading += _ => throw new InvalidOperationException("condition failed");
        var second = secondCompiler.Compile(request);
        Assert.Empty(second.Diagnostics);
        var secondConditionContext = AssemblyLoadContext.GetLoadContext(second.Evaluate!.Method.DeclaringType!.Assembly)!;
        var secondUnloading = 0;
        secondConditionContext.Unloading += _ => secondUnloading++;
        Assert.Contains("Value", WorkflowJson.Serialize(data, type));
        var profile = WorkflowJson.Options(type: type);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.Same(compiled.Evaluate, compiler.Compile(request).Evaluate);
        cache = IxIFlow.TestSupport.ExistingPath.Canonicalize(cache);
        var native = Assert.Single(Directory.GetFiles(Path.Combine(cache, "native"), "*", SearchOption.AllDirectories));
        if (OperatingSystem.IsWindows())
            Assert.Throws<IOException>(() => { using var locked = File.Open(native, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        var unloading = 0;
        context.Unloading += _ =>
        {
            unloading++;
            Assert.True(nativeCondition.Evaluate!(data, null, null, null, DateTimeOffset.UtcNow));
            if (throwing == "public") throw new InvalidOperationException("public handler failed");
        };
        var loader = typeof(WorkflowCatalog).GetField("_bundles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(catalog)!;
        var entries = (System.Collections.IDictionary)loader.GetType().GetField("_loaded", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(loader)!;
        var entry = Assert.Single(entries.Values.Cast<object>());
        var stop = (CancellationTokenSource)entry.GetType().GetField("Stop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(entry)!;
        using (ExecutionContext.SuppressFlow())
        {
            lease.Dispose();
            Assert.True(ExecutionContext.IsFlowSuppressed());
        }
        Assert.Equal(1, unloading);
        Assert.Equal(1, secondUnloading);
        Assert.Throws<ObjectDisposedException>(() => stop.Token);
        Assert.NotSame(profile, WorkflowJson.Options(type: type));
        var afterUnload = compiler.Compile(request);
        Assert.Empty(afterUnload.Diagnostics);
        Assert.NotSame(compiled.Evaluate, afterUnload.Evaluate);
        Assert.True(afterUnload.Evaluate!(data, null, null, null, DateTimeOffset.UtcNow));
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(nativeCondition.Evaluate!(data, null, null, null, DateTimeOffset.UtcNow));
        Assert.True(compiled.Evaluate(data, null, null, null, DateTimeOffset.UtcNow));
        if (OperatingSystem.IsWindows())
            Assert.Throws<IOException>(() => { using var locked = File.Open(native, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        AssertNativeMapped(cache, true);
        context.Unload();
        Assert.Equal(1, unloading);
        return ([new(context, true), new(type.Assembly, true), new(firstConditionContext, true), new(secondConditionContext, true)], data);
    }

    private static void AssertNativeMapped(string cache, bool expected)
    {
        cache = IxIFlow.TestSupport.ExistingPath.Canonicalize(cache);
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Assert.Equal(expected, process.Modules.Cast<System.Diagnostics.ProcessModule>()
                .Any(module => module.FileName.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        }
        if (OperatingSystem.IsLinux()) Assert.Equal(expected, File.ReadAllText("/proc/self/maps").Contains(cache, StringComparison.Ordinal));
    }

    private sealed class CleanupLogger : ILogger<WorkflowCatalog>, ILogger<BreakpointConditionCompiler>
    {
        public readonly List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            Assert.Null(error);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                Assert.All(values, value => Assert.True(value.Value is null or string));
            lock (Messages) Messages.Add(format(state, error));
        }
    }
}

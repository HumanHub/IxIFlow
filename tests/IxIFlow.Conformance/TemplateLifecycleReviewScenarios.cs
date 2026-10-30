using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.Loader;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.Conformance;

// Exercises template lifecycle operations after loading published bundles into a fresh host.
[Trait("Feature", "templates"), Trait("Spec", "F-16"), Trait("Spec", "F-17"), Trait("Spec", "F-26")]
public sealed class TemplateLifecycleReviewScenarios
{
    private static readonly Lazy<byte[]> Package = new(() => PublishingFeed.Package("review.probes", new()
    {
        ["lib/net10.0/Review.Probes.dll"] = PublishingFeed.Emit("Review.Probes", """
                                                                                 using System; using System.Text; using System.Threading; using System.Threading.Tasks; using System.Collections.Concurrent;
                                                                                 using IxIFlow.Core; using IxIFlow.ActivitySdk; using IxIFlow.Templates; using IxIFlow.Builders; using IxIFlow.Builders.Interfaces;
                                                                                 namespace ReviewProbes;
                                                                                 public class Model { public string Value {get;set;} = "Ana"; }
                                                                                 public class Data { public string Value {get;set;} = ""; public Model Model {get;set;} = new Model(); }
                                                                                 public class Signal { }
                                                                                 public static class Pins { private static int reads; public static TemplateReference Changing => new(Interlocked.Increment(ref reads) == 1 ? "alpha" : "beta", 1); }
                                                                                 [WorkflowActivity("review.scope")]
                                                                                 public class RenderScope : IScopeActivity<StringBuilder> {
                                                                                     private readonly TemplateRenderer renderer;
                                                                                     private readonly ConcurrentQueue<string> events;
                                                                                     public RenderScope(TemplateRenderer renderer, ConcurrentQueue<string> events) { this.renderer=renderer; this.events=events; }
                                                                                     [ActivityInput] public TemplateReference Template {get;set;}
                                                                                     private string mode = "valid";
                                                                                     [ActivityInput] public string Mode {get => mode; set {
                                                                                         mode=value;
                                                                                         if (!value.StartsWith("prepare-")) return;
                                                                                         try { renderer.RenderAsync(new TemplateReference("beta",1),new Model(),null).GetAwaiter().GetResult(); }
                                                                                         catch(WorkflowEngineException) when(value == "prepare-swallow") { events.Enqueue("swallowed:prepare"); }
                                                                                     }}
                                                                                     private ConcurrentQueue<string> Log(IScopeContext c) => (ConcurrentQueue<string>)c.Services.GetService(typeof(ConcurrentQueue<string>));
                                                                                     private async Task Render(IScopeContext c, string phase) {
                                                                                         Log(c).Enqueue(phase+":"+Template.Key);
                                                                                         if (Mode == phase+"-authored") throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,"authored lifecycle");
                                                                                         var bad = Mode.StartsWith(phase+"-");
                                                                                         try { var result = await ((TemplateRenderer)c.Services.GetService(typeof(TemplateRenderer))).RenderAsync(bad ? new TemplateReference("beta",1) : Template,new Model(),null); Log(c).Enqueue(phase+"="+result.Content); }
                                                                                         catch(WorkflowEngineException) when (Mode == phase+"-swallow") { Log(c).Enqueue("swallowed:"+phase); }
                                                                                     }
                                                                                     public async Task<StringBuilder> OpenAsync(ScopeOpening o, IScopeContext c, CancellationToken t) {
                                                                                         await Render(c, o.Reopen ? "reopen" : "open"); return new StringBuilder();
                                                                                     }
                                                                                     public Task CloseAsync(StringBuilder value, ScopeClosing c, IScopeContext s, CancellationToken t) => Render(s,"close");
                                                                                 }
                                                                                 [WorkflowActivity("review.output")]
                                                                                 public class GetterRender : IAsyncActivity {
                                                                                     private TemplateRenderer renderer;
                                                                                     private ConcurrentQueue<string> log;
                                                                                     [ActivityInput] public TemplateReference Template {get;set;}
                                                                                     [ActivityInput] public string Mode {get;set;} = "valid";
                                                                                     [ActivityOutput] public string Rendered {get {
                                                                                         log.Enqueue("getter");
                                                                                         if (Mode == "authored") throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,"authored getter");
                                                                                         if (Mode == "arbitrary") throw new InvalidOperationException("authored getter");
                                                                                         try { return renderer.RenderAsync(Mode == "valid" ? Template : new TemplateReference("beta",1),new Model(),null).GetAwaiter().GetResult().Content; }
                                                                                         catch(WorkflowEngineException) when(Mode.StartsWith("swallow")) {
                                                                                             log.Enqueue("swallowed:getter");
                                                                                             if (Mode == "swallow-limit") throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted,"authored limit after refusal");
                                                                                             return "swallowed";
                                                                                         }
                                                                                     } private set {} }
                                                                                     public Task ExecuteAsync(IActivityContext c, CancellationToken t) {renderer=(TemplateRenderer)c.Services.GetService(typeof(TemplateRenderer)); log=(ConcurrentQueue<string>)c.Services.GetService(typeof(ConcurrentQueue<string>)); return Task.CompletedTask;}
                                                                                 }
                                                                                 [WorkflowActivity("review.marker")]
                                                                                 public class Marker : IAsyncActivity {
                                                                                     public Task ExecuteAsync(IActivityContext c, CancellationToken t) {((ConcurrentQueue<string>)c.Services.GetService(typeof(ConcurrentQueue<string>))).Enqueue("handler"); return Task.CompletedTask;}
                                                                                 }
                                                                                 [Workflow("ReviewFactory")]
                                                                                 public class Factory : IWorkflow<Data> {
                                                                                     public static WorkflowDefinition Make() => Workflow.Create<Data>("InnerFactory",1).Input(d => d.Value).Output(d => d.Value).Assign(c => c.WorkflowData.Value,c => c.WorkflowData.Value+" nested").Build();
                                                                                     public static WorkflowDefinition Definition() { var b=Workflow.Create<Data>("ReviewFactory",1); new Factory().Build(b); return b.Build(); }
                                                                                     public void Build(IWorkflowBuilder<Data> b) { var d=Make(); var pin=new WorkflowInlinePin(d.Name,d.Version,WorkflowDefinitionValidator.Inspect(d),WorkflowDefinitionValidator.GetContract(d).Hash); b.Input(d => d.Value).Output(d => d.Value).Use<Data>(Make,pin,s => s.Input(d => d.Value).From(c => c.WorkflowData.Value).Output(d => d.Value).To(c => c.WorkflowData.Value)); }
                                                                                 }
                                                                                 """)
    }));

    private static TemplateEngines Engines() => new([new LiquidTemplateEngine()]);
    private static WorkflowPublisher Publisher(IWorkflowStateRepository store) => new(store, store, templateEngines: Engines());

    private const string Header =
        "workflow: {name: Review, dataType: ReviewProbes.Data}\nimports: {catalogs: [review.probes@1.0.0]}\ncode:\n  alpha: {language: csharp-expression, source: 'new IxIFlow.Templates.TemplateReference(\"alpha\",1)'}\n  changed: {language: csharp-expression, source: ReviewProbes.Pins.Changing}\n";

    private const string OtherSite = "  - {kind: renderTemplate, id: beta, template: beta@1, model: {path: workflow.Model}}\n";
    private const string Marker = "{kind: activity, activity: review.marker}";
    private const string Wait = "{kind: waitFor, key: go, event: ReviewProbes.Signal}";

    private static string Catch(string body) =>
        "{kind: try, steps: [" + body + "], catches: [{exception: System.Exception, steps: [" + Marker + "]}]}";

    private static string Saga(string body) =>
        "{kind: saga, steps: [" + body + "], onError: [{steps: [" + Marker + "], compensate: none, then: continue}]}";

    private static string Scope(string mode, string body = "", bool changed = false, bool affine = false) =>
        "{kind: scope, activity: review.scope, " + (affine ? "threading: threadAffine, " : "") + "defaults: {Mode: " + mode +
        "}, input: [{target: Template, from: {kind: function, function: " + (changed ? "changed" : "alpha") + "}}], steps: [" + body + "]}";

    private static string Output(string mode, bool mapping) => "{kind: activity, activity: review.output, defaults: {Mode: " + mode +
        "}, input: [{target: Template, from: {kind: function, function: alpha}}]" +
        (mapping ? ", output: [{source: Rendered, to: {path: workflow.Value}}]" : "") + "}";

    private static async Task Seed(IWorkflowStateRepository store)
    {
        var publisher = Publisher(store);
        await publisher.UploadPackageAsync(new MemoryStream(Package.Value));

        foreach (var key in new[] { "alpha", "beta" })
        {
            var draft = await store.SaveDefinitionAsync(
                new(key, 1, "template",
                    $"template: {{key: {key}, version: 1, engine: liquid, format: text, model: ReviewProbes.Model}}\nimports: {{catalogs: [review.probes@1.0.0]}}\ncontent: 'Hello {{{{ Value }}}}'\n"),
                null);

            var result = await publisher.PublishAsync(draft.Id);
            Assert.True(result.Success, string.Join(";", result.Diagnostics));
        }
    }

    private static async Task<WorkflowVersion> Publish(IWorkflowStateRepository store, string body)
    {
        var draft = await store.SaveDefinitionAsync(new("Review", 1, "workflow", Header + "definitions:\n  - " + body + "\n" + OtherSite),
            null);

        var logger = new PublishLog();
        var result = await new WorkflowPublisher(store, store, logger: logger, templateEngines: Engines()).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join(";", result.Diagnostics) + logger.Error);

        return result.Workflow!;
    }

    private sealed class PublishLog : ILogger<WorkflowPublisher>
    {
        internal Exception? Error;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) =>
            Error = error ?? Error;
    }

    private static ServiceProvider Host(IWorkflowStateRepository store, ProfileStore database, string cache, ConcurrentQueue<string> log)
    {
        var services = new ServiceCollection();
        services.AddIxIFlow().UseWorkflowBundles(cache);
        services.AddSingleton(store);
        services.AddSingleton(log);
        services.AddSingleton(new TemplateRenderer(store, Engines()));
        database.ConfigureHost(services);

        return services.BuildServiceProvider();
    }

    private static async Task<WorkflowExecutionResult> Start(ServiceProvider host, ProfileStore database)
    {
        await database.StartHostLinksAsync(host);
        var client = host.GetRequiredService<IWorkflowClient>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var id = await client.StartAsync("Review", 1, new { Value = "start" }, cancellationToken: timeout.Token);

        return await client.AwaitAsync(id, WorkflowAwaitCondition.Idle, timeout.Token);
    }

    private static void GuardFault(WorkflowExecutionResult result, ConcurrentQueue<string> log)
    {
        Assert.True(result.Status == WorkflowExecutionStatus.Faulted, $"{result.Status}: {result.ErrorMessage}");
        Assert.Equal(typeof(WorkflowEngineException).FullName, result.Diagnostic!.ErrorType);
        Assert.Contains("not permitted", result.ErrorMessage);
        Assert.DoesNotContain("handler", log);
    }

    public static IEnumerable<object[]> ScopeCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[]
            {
                "valid", "changed", "prepare-swallow", "prepare-propagate", "open-swallow", "open-propagate", "close-swallow",
                "close-propagate", "open-authored", "close-authored"
            }
            .SelectMany(mode => new[] { false, true }.Select(affine => new object[] { profile, mode, affine })));

    [Theory, MemberData(nameof(ScopeCases))]
    public async Task ScopePreparationOpenAndCloseUseTheSiteAndTrustedFailureSlot(string profile, string mode, bool affine)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Seed(store);
        await Publish(store, Catch(Scope(mode, changed: mode == "changed", affine: affine)));
        var log = new ConcurrentQueue<string>();
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.ReviewFixes", Guid.NewGuid().ToString("N"));

        try
        {
            await using var host = Host(store, database, cache, log);
            var result = await Start(host, database);

            if (mode == "valid" || mode.EndsWith("authored"))
            {
                Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
                Assert.Equal(mode.EndsWith("authored"), log.Contains("handler"));

                if (mode == "valid")
                {
                    Assert.Contains("open=Hello Ana", log);
                    Assert.Contains("close=Hello Ana", log);
                }
            }
            else
                GuardFault(result, log);

            if (mode == "changed")
                Assert.Empty(log);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    public static IEnumerable<object[]> ReopenCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "valid", "saved-input", "reopen-swallow", "reopen-propagate" }.Select(mode => new object[] { profile, mode }));

    [Theory, MemberData(nameof(ReopenCases))]
    public async Task ColdReopenChecksSavedInputsAndBypassesCatchAndSagaHandlers(string profile, string mode)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Seed(store);
        await Publish(store, Catch(Scope(mode, Saga(Wait + ", " + Marker), affine: true)));
        var log = new ConcurrentQueue<string>();
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.ReviewFixes", Guid.NewGuid().ToString("N"));

        try
        {
            WorkflowExecutionResult idle;

            await using (var first = Host(store, database, Path.Combine(cache, "first"), log))
            {
                idle = await Start(first, database);
                Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
                await first.GetRequiredService<WorkflowHost>().StopAsync(default);
            }

            var instance = (await store.GetWorkflowInstanceAsync(idle.InstanceId))!;
            var instanceState = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance));

            if (mode == "saved-input")
            {
                Assert.Single(instanceState.Scopes).Inputs.Properties["Template"].Value!.Json =
                    StoredJson.Inline(JsonSerializer.Serialize(new TemplateReference("beta", 1)));

                instance.ExecutionStateJson = JsonSerializer.Serialize(instanceState);

                // Make the suspended row claimable for this fixture write. Send the actual
                // resume event after the commit, against its resulting wait registration.
                Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(new(idle.InstanceId, Guid.NewGuid().ToString("N"),
                    Assert.Single(instanceState.Waits).EventType, "{}", Key: "go"))).Status);

                await store.SeedAsync(instance);
                Assert.Contains("beta", StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(idle.InstanceId))!));
            }

            Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(new(idle.InstanceId, Guid.NewGuid().ToString("N"),
                Assert.Single(instanceState.Waits).EventType, "{}", Key: "go"))).Status);

            await using var second = Host(store, database, Path.Combine(cache, "second"), log);
            await database.StartHostLinksAsync(second);
            var client = second.GetRequiredService<IWorkflowClient>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.RecoverAsync(idle.InstanceId, timeout.Token);
            var result = await client.AwaitAsync(idle.InstanceId, WorkflowAwaitCondition.Idle, timeout.Token);

            if (mode == "valid")
            {
                Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
                Assert.Contains("reopen=Hello Ana", log);
                Assert.Equal(2, log.Count(item => item == "close=Hello Ana"));
            }
            else
                GuardFault(result, log);

            if (mode == "saved-input")
                Assert.DoesNotContain(log, item => item.StartsWith("reopen:"));
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    public static IEnumerable<object[]> OutputCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "mapping", "previous", "saga" }.SelectMany(phase =>
            new[] { "valid", "swallow", "swallow-limit", "propagate", "authored", "arbitrary" }.Select(mode =>
                new object[] { profile, phase, mode })));

    [Theory, MemberData(nameof(OutputCases))]
    public async Task OutputMappingsPreviousAndSagaCaptureRetainTheDispatchFrame(string profile, string phase, string mode)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Seed(store);
        var activity = Output(mode, phase == "mapping");

        await Publish(store,
            Catch(phase == "saga" ? Saga(activity) : activity + (phase == "previous" ? ", {kind: sequence, steps: []}" : "")));

        var log = new ConcurrentQueue<string>();
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.ReviewFixes", Guid.NewGuid().ToString("N"));

        try
        {
            await using var host = Host(store, database, cache, log);
            var result = await Start(host, database);
            Assert.Contains("getter", log);

            if (mode == "valid")
            {
                Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);

                if (phase == "mapping")
                    Assert.Equal("Hello Ana", result.WorkflowData!.GetType().GetProperty("Value")!.GetValue(result.WorkflowData));

                Assert.DoesNotContain("handler", log);
            }
            else if (mode is "authored" or "arbitrary")
            {
                // Saga capture errors require resolution; mapping accessor failures reach Catch;
                // deferred previous serialization remains a checkpoint failure.
                Assert.Equal(
                    phase == "saga" ? WorkflowExecutionStatus.NeedsResolution :
                    phase == "previous" ? WorkflowExecutionStatus.Faulted : WorkflowExecutionStatus.Success, result.Status);

                Assert.Equal(phase == "mapping", log.Contains("handler"));
            }
            else
                GuardFault(result, log);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PublishedUseTypeWithNestedFactoryPinColdLoadsAndExecutes(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Seed(store);

        // Publication checks every declared dependency's catalog identity, including code-defined bodies.
        // Register their metadata in a separate collectible context, then use only the published closure.
        var context = new AssemblyLoadContext(null, isCollectible: true);

        try
        {
            using var package = new ZipArchive(new MemoryStream(Package.Value));
            using var image = new MemoryStream();

            await using (var entry = package.GetEntry("lib/net10.0/Review.Probes.dll")!.Open())
                await entry.CopyToAsync(image);

            image.Position = 0;
            var type = context.LoadFromStream(image).GetType("ReviewProbes.Factory")!;
            using var types = WorkflowTypeIdentity.Enter(context);
            using var catalog = new WorkflowCatalog(store);

            foreach (var method in new[] { "Make", "Definition" })
                await catalog.RegisterWorkflowAsync((WorkflowDefinition)type.GetMethod(method)!.Invoke(null, null)!);
        }
        finally
        {
            context.Unload();
        }

        var version = await Publish(store,
            "{kind: useType, workflowType: ReviewProbes.Factory, input: [{target: Value, from: {path: workflow.Value}}], output: [{source: Value, to: {path: workflow.Value}}]}");

        await using var stream = await store.OpenAsync(version.ArtifactDigest!.Value);
        var bundle = await WorkflowBundleArchive.ReadAsync(stream!, version.ArtifactDigest.Value);
        Assert.Equal(2, bundle.Manifest.InlineWorkflows.Count);
        var factory = Assert.Single(bundle.Manifest.InlineWorkflows, pin => pin.Name == "InnerFactory");
        Assert.Null(factory.BundleDigest);
        Assert.Equal("ReviewProbes.Factory", factory.EntryType);
        Assert.Equal("Review.Probes", factory.AssemblyName);
        Assert.DoesNotContain("private/Review.Probes.dll", bundle.Entries.Keys);
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.ReviewFixes", Guid.NewGuid().ToString("N"));

        try
        {
            await using var host = Host(store, database, cache, new());
            var result = await Start(host, database);
            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
            Assert.Equal("start nested", result.WorkflowData!.GetType().GetProperty("Value")!.GetValue(result.WorkflowData));

            foreach (var changed in new[]
                     {
                         factory with { Name = "forged" }, factory with { StructureHash = "forged" },
                         factory with { ContractHash = "forged" }, factory with { EntryType = "Forged.Entry" },
                         factory with { AssemblyName = "Forged.Assembly" }, factory with { Sites = ["root/99"] }
                     })
            {
                var error = await Assert.ThrowsAsync<WorkflowEngineException>(async () =>
                {
                    var manifest = bundle.Manifest with
                    {
                        InlineWorkflows = bundle.Manifest.InlineWorkflows.Select(pin => pin == factory ? changed : pin).ToArray()
                    };

                    var bytes = WorkflowBundleArchive.Create(manifest,
                        bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));

                    var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
                    using var loader = new WorkflowBundleLoader(store, new(Path.Combine(cache, Guid.NewGuid().ToString("N"))));
                    using var lease = await loader.AcquireAsync(digest, default);
                });

                Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
            }
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }
}
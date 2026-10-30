using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Generators;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "templates"), Trait("Spec", "F-16"), Trait("Spec", "F-17"), Trait("Spec", "F-14")]
public sealed class TemplateBundleScenarios
{
    public sealed class ParityModel
    {
        public string Value { get; set; } = "";
    }

    public sealed class ParityData
    {
        public ParityModel Model { get; set; } = new();
        public string? Culture { get; set; }
        public RenderedTemplate? Body { get; set; }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RenderStepAndFluentTwinHaveEqualHashesContractsAndResults(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        await new TemplatePublisher(store, store, Engines()).PublishAsync<ParityModel>(TemplateVersion.Create<ParityModel>(
            "parity", 1, "liquid", TemplateFormat.Text, new TemplateVariant("", "Hello {{ Value }}"),
            new TemplateVariant("de", "Hallo {{ Value }}")));

        var fluent = Workflow.Create<ParityData>("TemplateParity")
            .Step<RenderTemplate<ParityModel>>(setup => setup
                .Input(activity => activity.Template).From(_ => new TemplateReference("parity", 1))
                .Input(activity => activity.Model).From(context => context.WorkflowData.Model)
                .Input(activity => activity.Culture).From(context => context.WorkflowData.Culture)
                .Output(activity => activity.Rendered).To(context => context.WorkflowData.Body))
            .WithId("render").Build();

        var document = $$$"""
                          workflow:
                            name: TemplateParity
                            dataType: {{{typeof(ParityData).FullName!.Replace('+', '.')}}}
                          definitions:
                            - kind: renderTemplate
                              id: render
                              template: parity@1
                              modelType: {{{typeof(ParityModel).FullName!.Replace('+', '.')}}}
                              model: {path: workflow.Model}
                              culture: {path: workflow.Culture}
                              output: [{source: Rendered, to: {path: workflow.Body}}]
                          """;

        var yaml = IxIFlow.Tests.Infrastructure.TestWorkflowCompilation.Build(WorkflowSourceTranslator.Translate(document, "parity.yaml"));
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
        Assert.Equal(WorkflowDefinitionValidator.GetContract(fluent).Hash, WorkflowDefinitionValidator.GetContract(yaml).Hash);
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.TemplateParity", Guid.NewGuid().ToString("N"));
        var bodies = new List<RenderedTemplate?>();

        try
        {
            foreach (var definition in new[] { fluent, yaml })
            {
                await using var host = Host(store, Path.Combine(cache, bodies.Count.ToString()), database, new());
                await database.StartHostLinksAsync(host);

                var result = await host.GetRequiredService<IWorkflowClient>()
                    .RunAsync(definition, new ParityData { Model = new() { Value = "Ana" }, Culture = "de-AT" });

                Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
                bodies.Add(Assert.IsType<ParityData>(result.WorkflowData).Body);
                await host.GetRequiredService<WorkflowHost>().StopAsync(default);
            }

            Assert.Equal(bodies[0], bodies[1]);
            Assert.Equal("Hallo Ana", bodies[0]!.Content);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    private static readonly Lazy<byte[]> Package = new(() => PublishingFeed.Package("template.probes", new()
    {
        ["lib/net10.0/Template.Probes.dll"] = PublishingFeed.Emit("Template.Probes", """
                                                                                     using System; using System.Threading; using System.Threading.Tasks; using System.Collections.Concurrent;
                                                                                     using IxIFlow.Core; using IxIFlow.ActivitySdk; using IxIFlow.Templates;
                                                                                     namespace TemplateProbes;
                                                                                     public static class Pins {
                                                                                         private static int reads;
                                                                                         public static TemplateReference Reference => new TemplateReference(Interlocked.Increment(ref reads) == 1 ? "alpha" : "beta", 1);
                                                                                         public static TemplateReference Captured => new TemplateReference(Environment.GetEnvironmentVariable("IXIFLOW_TEMPLATE_RECONSTRUCT") ?? "alpha", 1);
                                                                                     }
                                                                                     public class Model { public string Value {get;set;} = ""; }
                                                                                     public class Envelope<T> { public T Value {get;set;} }
                                                                                     public class Data { public Model Model {get;set;} = new Model(); public Envelope<string> Generic {get;set;} = new Envelope<string>(); public RenderedTemplate Body {get;set;} public string Key {get;set;} = "alpha"; }
                                                                                     public class Signal { }
                                                                                     [WorkflowActivity("template.probe")]
                                                                                     public class Probe : IRecoverableActivity<string> {
                                                                                         [ActivityInput] public TemplateReference Template {get;set;}
                                                                                         [ActivityInput] public Model Model {get;set;} = new Model();
                                                                                         [ActivityInput] public string Mode {get;set;} = "good";
                                                                                         [ActivityOutput] public RenderedTemplate Rendered {get;private set;}
                                                                                         private static ConcurrentQueue<string> Log(IActivityContext c) => (ConcurrentQueue<string>)c.Services.GetService(typeof(ConcurrentQueue<string>));
                                                                                         private Task<RenderedTemplate> Render(IActivityContext c, TemplateReference t) => ((TemplateRenderer)c.Services.GetService(typeof(TemplateRenderer))).RenderAsync(t, Model, null);
                                                                                         public async Task ExecuteAsync(IActivityContext c, CancellationToken token = default) {
                                                                                             Log(c).Enqueue("execute:" + Mode);
                                                                                             await Task.Yield();
                                                                                             if (Mode == "authored") throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, "authored failure");
                                                                                             if (Mode == "arbitrary") throw new Exception("authored failure");
                                                                                             if (Mode == "swallow") { try { await Render(c, new TemplateReference("beta",1)); } catch (WorkflowEngineException) {} return; }
                                                                                             Rendered = await Render(c, Mode == "other" || Mode == "none" ? new TemplateReference("beta",1) : Template);
                                                                                         }
                                                                                         public string CaptureRecoveryState(IActivityContext c) {
                                                                                             if (Mode == "prepare") { try { Render(c, new TemplateReference("beta",1)).GetAwaiter().GetResult(); } catch (WorkflowEngineException) {} }
                                                                                             return Mode;
                                                                                         }
                                                                                         public async Task<ActivityRecoveryResult<string>> RecoverAsync(string state, IActivityContext c, CancellationToken token = default) {
                                                                                             Log(c).Enqueue("recover:" + Mode);
                                                                                             if (Mode == "recover") { try { await Render(c, new TemplateReference("beta",1)); } catch (WorkflowEngineException) {} }
                                                                                             return new ActivityRecoveryResult<string>.Execute();
                                                                                         }
                                                                                     }
                                                                                     [WorkflowActivity("template.marker")]
                                                                                     public class Marker : IAsyncActivity {
                                                                                         public Task ExecuteAsync(IActivityContext c, CancellationToken token = default) {
                                                                                             ((ConcurrentQueue<string>)c.Services.GetService(typeof(ConcurrentQueue<string>))).Enqueue("handler"); return Task.CompletedTask;
                                                                                         }
                                                                                     }
                                                                                     """)
    }));

    private static TemplateEngines Engines() => new([new LiquidTemplateEngine()]);
    private static WorkflowPublisher Publisher(IWorkflowStateRepository store) => new(store, store, templateEngines: Engines());

    private static string Header(string name) =>
        $"workflow: {{name: {name}, dataType: TemplateProbes.Data}}\nimports: {{catalogs: [template.probes@1.0.0]}}\n";

    private const string Code =
        "code:\n  changed: {language: csharp-expression, source: TemplateProbes.Pins.Reference}\n  alpha: {language: csharp-expression, source: 'new IxIFlow.Templates.TemplateReference(\"alpha\", 1)'}\n  beta: {language: csharp-expression, source: 'new IxIFlow.Templates.TemplateReference(\"beta\", 1)'}\n  gamma: {language: csharp-expression, source: 'new IxIFlow.Templates.TemplateReference(\"gamma\", 1)'}\n";

    private const string OtherSite = "  - {kind: renderTemplate, id: beta, template: beta@1, model: {path: workflow.Model}}\n";

    private static string Probe(string mode, string template = "alpha", string suffix = "") =>
        "{kind: activity, id: probe, activity: template.probe, defaults: {Mode: " + mode + "}, input: [" +
        (mode == "none" ? "" : "{target: Template, from: {kind: function, function: " + template + "}}, ") +
        "{target: Model, from: {path: workflow.Model}}]" + suffix + "}";

    private static async Task Seed(IWorkflowStateRepository store)
    {
        var publisher = Publisher(store);
        await publisher.UploadPackageAsync(new MemoryStream(Package.Value));

        foreach (var key in new[] { "alpha", "beta", "gamma" })
        {
            var yaml =
                $"template: {{key: {key}, version: 1, engine: liquid, format: text, model: TemplateProbes.Model}}\nimports: {{catalogs: [template.probes@1.0.0]}}\ncontent: '{key} {{{{ Value }}}}'\ncultures: {{de: '{key}-de {{{{ Value }}}}'}}\n";

            var draft = await store.SaveDefinitionAsync(new(key, 1, "template", yaml), null);
            Success(await publisher.PublishAsync(draft.Id));
        }
    }

    private static void Success(WorkflowPublishResult result) => Assert.True(result.Success, string.Join("; ", result.Diagnostics));

    private static async Task<WorkflowVersion> Publish(IWorkflowStateRepository store, string name, string yaml)
    {
        var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow", yaml), null);
        var result = await Publisher(store).PublishAsync(draft.Id);
        Success(result);

        return result.Workflow!;
    }

    private static async Task<WorkflowBundle> Read(IWorkflowStateRepository store, WorkflowVersion version)
    {
        await using var stream = await store.OpenAsync(version.ArtifactDigest!.Value);

        return await WorkflowBundleArchive.ReadAsync(stream!, version.ArtifactDigest.Value);
    }

    private static ServiceProvider Host(IWorkflowStateRepository store, string cache, ProfileStore database,
        ConcurrentQueue<string> log, Action? templateRead = null)
    {
        var counted = TypedPublishingScenarios.CatalogProbe.Wrap(store, out var probe);

        probe.Before = (method, _) =>
        {
            if (method.Name.StartsWith("GetTemplate", StringComparison.Ordinal))
                templateRead?.Invoke();
        };

        var services = new ServiceCollection();
        services.AddIxIFlow(options => options.Debugging = true).UseWorkflowBundles(cache);
        services.AddSingleton(store);
        services.AddSingleton(log);
        services.AddSingleton(new TemplateRenderer(counted, Engines()));
        database.ConfigureHost(services);

        return services.BuildServiceProvider();
    }

    private static async Task<WorkflowExecutionResult> Run(ServiceProvider host, ProfileStore database, string name)
    {
        await database.StartHostLinksAsync(host);
        var client = host.GetRequiredService<IWorkflowClient>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var id = await client.StartAsync(name, 1, new { Model = new { Value = "Ana" } }, cancellationToken: timeout.Token);

        return await client.AwaitAsync(id, WorkflowAwaitCondition.Finished, timeout.Token);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ColdHostRendersPinnedPrivateModelWithoutAnyCatalogLookups(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Seed(store);

        var version = await Publish(store, "ColdTemplate", Header("ColdTemplate") + "definitions:\n" +
            "  - {kind: renderTemplate, id: render, template: alpha, model: {path: workflow.Model}, culture: {kind: constant, value: de-AT}, output: [{source: Rendered, to: {path: workflow.Body}}]}\n");

        var bundle = await Read(store, version);
        Assert.Equal(6, bundle.Manifest.FormatVersion);
        Assert.Single(bundle.Manifest.Templates);
        Assert.Single(bundle.Manifest.TemplateBindings);
        var canonical = Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.YamlPath].Span);
        Assert.Contains("alpha@1", canonical);

        Assert.Equal("global::TemplateProbes.Model",
            Assert.IsType<IxIFlow.Dsl.Documents.RenderTemplateStepDocument>(Assert.Single(IxIFlow.Dsl.Documents.WorkflowYamlSerializer
                .Read(canonical).Definitions)).ModelType);

        var newer = await store.SaveDefinitionAsync(new("alpha", 2, "template",
                "template: {key: alpha, version: 2, engine: liquid, format: text, model: TemplateProbes.Model}\nimports: {catalogs: [template.probes@1.0.0]}\ncontent: new"),
            null);

        Success(await Publisher(store).PublishAsync(newer.Id));
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.TemplateBundle", Guid.NewGuid().ToString("N"));
        var reads = 0;

        try
        {
            await using var host = Host(store, cache, database, new(), () => Interlocked.Increment(ref reads));
            var result = await Run(host, database, version.Name);
            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
            var body = (RenderedTemplate)result.WorkflowData!.GetType().GetProperty("Body")!.GetValue(result.WorkflowData)!;
            Assert.Equal("alpha-de Ana", body.Content);
            Assert.Equal(1, body.Version);
            Assert.Equal("de", body.Culture);
            Assert.Equal(0, reads);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    public static IEnumerable<object[]> EnforcementCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "other", "none", "swallow", "prepare", "input", "authored", "arbitrary" }.SelectMany(mode =>
            new[] { "catch", "saga" }.Select(boundary => new object[] { profile, mode, boundary })));

    [Theory, MemberData(nameof(EnforcementCases))]
    public async Task GuardFailuresBypassCatchAndSagaWhileAuthoredExceptionsKeepTheirTreatment(string profile, string mode, string boundary)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Seed(store);
        var authored = mode is "authored" or "arbitrary";

        var body = boundary == "catch"
            ? "  - kind: try\n    steps: [" + Probe(mode, mode == "input" ? "changed" : "alpha") +
            "]\n    catches: [{exception: System.Exception, steps: [{kind: activity, activity: template.marker}]}]\n"
            : "  - kind: saga\n    steps: [" +
            Probe(mode, mode == "input" ? "changed" : "alpha",
                suffix: ", onError: [{then: retry, retry: {maximumAttempts: 2, initialInterval: '00:00:00'}}]") +
            "]\n    onError: [{steps: [{kind: activity, activity: template.marker}], compensate: none, then: continue}]\n";

        var version = await Publish(store, "Enforcement", Header("Enforcement") + Code + "definitions:\n" + body + OtherSite);
        var log = new ConcurrentQueue<string>();
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.TemplateGuard", Guid.NewGuid().ToString("N"));

        try
        {
            await using var host = Host(store, cache, database, log);
            var result = await Run(host, database, version.Name);
            Assert.Equal(authored ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Faulted, result.Status);
            Assert.Equal(authored ? 1 : 0, log.Count(item => item == "handler"));

            Assert.Equal(mode is "prepare" or "input" ? 0 : authored && boundary == "saga" ? 3 : 1,
                log.Count(item => item.StartsWith("execute:", StringComparison.Ordinal)));

            if (!authored)
            {
                Assert.Equal(typeof(WorkflowEngineException).FullName, result.Diagnostic!.ErrorType);
                Assert.Contains("not permitted", result.ErrorMessage);
            }
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    public static IEnumerable<object[]> TamperCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "payload", "model", "binding", "captured", "reconstruction", "missing", "unlisted" }.Select(mutation =>
            new object[] { profile, mutation }));

    [Theory, MemberData(nameof(TamperCases))]
    public async Task ColdLoadRejectsForgedPayloadModelBindingsAndReconstructedConstants(string profile, string mutation)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Seed(store);

        var original = await Publish(store, "Tamper", Header("Tamper") + Code +
            (mutation == "reconstruction" ? "  captured: {language: csharp-expression, source: TemplateProbes.Pins.Captured}\n" : "") +
            "definitions:\n  - " + Probe("good", mutation == "reconstruction" ? "captured" : "alpha") + "\n" + OtherSite);

        var bundle = await Read(store, original);
        var entries = bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var manifest = bundle.Manifest;

        if (mutation == "payload")
            entries[manifest.Templates[0].Entry] = Encoding.UTF8.GetBytes("{}");

        if (mutation == "model")
            manifest = manifest with { Templates = manifest.Templates.Select(pin => pin with { ModelTypeId = "forged" }).ToArray() };

        if (mutation == "binding")
            manifest = manifest with
            {
                TemplateBindings = manifest.TemplateBindings.Select(binding => binding with { Step = binding.Step + "/forged" })
                    .ToArray()
            };

        if (mutation == "captured")
            manifest = manifest with
            {
                TemplateBindings = manifest.TemplateBindings.Select(binding => binding with { Templates = [new("beta", 1)] }).ToArray()
            };

        if (mutation == "missing")
            entries.Remove(manifest.Templates[0].Entry);

        if (mutation == "unlisted")
            entries["templates/extra@1.json"] = entries[manifest.Templates[0].Entry];

        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.TemplateTamper", Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("IXIFLOW_TEMPLATE_RECONSTRUCT");

        try
        {
            if (mutation == "reconstruction")
                Environment.SetEnvironmentVariable("IXIFLOW_TEMPLATE_RECONSTRUCT", "beta");

            var error = await Assert.ThrowsAsync<WorkflowEngineException>(async () =>
            {
                var archive = WorkflowBundleArchive.Create(manifest, entries);
                var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(archive));
                using var loader = new WorkflowBundleLoader(store, new(cache));
                using var lease = await loader.AcquireAsync(digest, default);
            });

            Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("IXIFLOW_TEMPLATE_RECONSTRUCT", previous);

            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task VerifiedInlineTemplatesAreCopiedAndBoundAtEveryGraftedSite(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Seed(store);

        var leaf = await Publish(store, "TemplateLeaf",
            Header("TemplateLeaf") +
            "definitions:\n  - {kind: renderTemplate, id: render, template: alpha@1, model: {path: workflow.Model}, output: [{source: Rendered, to: {path: workflow.Body}}]}\n");

        await Publish(store, "TemplateMiddle",
            Header("TemplateMiddle") + "definitions:\n  - {kind: use, id: nested, workflow: {name: TemplateLeaf, version: 1}}\n");

        var caller = await Publish(store, "TemplateCaller", Header("TemplateCaller") + "definitions:\n" +
            "  - {kind: use, id: first, workflow: {name: TemplateMiddle, version: 1}}\n  - {kind: use, id: second, workflow: {name: TemplateLeaf, version: 1}}\n");

        var bundle = await Read(store, caller);
        var leafBundle = await Read(store, leaf);
        Assert.Equal(leafBundle.Entries["templates/alpha@1.json"].ToArray(), bundle.Entries["templates/alpha@1.json"].ToArray());
        Assert.Single(bundle.Manifest.Templates);
        Assert.Equal(2, bundle.Manifest.TemplateBindings.Count);
        Assert.All(bundle.Manifest.TemplateBindings, binding => Assert.Contains("/inline/", binding.Step));
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.TemplateInline", Guid.NewGuid().ToString("N"));
        var reads = 0;

        try
        {
            await using var host = Host(store, cache, database, new(), () => Interlocked.Increment(ref reads));
            Assert.Equal(WorkflowExecutionStatus.Success, (await Run(host, database, caller.Name)).Status);
            Assert.Equal(0, reads);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    public static IEnumerable<object[]> TopologyCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "parallel", "parallel-denied", "compensation", "inline-compensation", "graph", "state" }.Select(topology =>
            new object[] { profile, topology }));

    [Theory, MemberData(nameof(TopologyCases))]
    public async Task BindingsUseParallelCompensationAndGraphCatalogSites(string profile, string topology)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Seed(store);
        const string render = "{kind: renderTemplate, id: render, template: alpha@1, model: {path: workflow.Model}}";

        var body = topology switch
        {
            "parallel" or "parallel-denied" => "  - kind: parallel\n    branches:\n      - steps: [" +
                Probe(topology == "parallel" ? "good" : "other") + "]\n      - steps: [" +
                Probe("good", "beta").Replace("id: probe", "id: sibling") + "]\n",
            "compensation" or "inline-compensation" => "  - kind: saga\n    steps:\n      - " + Probe("good",
                    suffix:
                    ", compensation: [{activity: template.probe, input: [{target: Template, from: {kind: function, function: gamma}}, {target: Model, from: {path: workflow.Model}}, {target: Mode, from: {kind: constant, value: other}}]}]") +
                "\n      - " + Probe("authored").Replace("id: probe", "id: fail") +
                "\n    onError: [{steps: [{kind: activity, activity: template.marker}], compensate: all, then: continue}]\n",
            "graph" => "  - {kind: flowchart, id: graph, start: render, nodes: [{key: render, steps: [" + render +
                "], transitions: [{to: end}]}]}\n",
            _ => "  - {kind: stateMachine, id: machine, start: rendered, states: [{key: rendered, final: true, entry: [" + render + "]}]}\n"
        };

        var leafName = topology == "inline-compensation" ? "TopologyLeaf" : "Topology";
        var version = await Publish(store, leafName, Header(leafName) + Code + "definitions:\n" + body + OtherSite);

        if (topology == "inline-compensation")
            version = await Publish(store, "Topology",
                Header("Topology") + "definitions: [{kind: use, id: inline, workflow: {name: TopologyLeaf, version: 1}}]");

        var bundle = await Read(store, version);

        if (topology.Contains("compensation", StringComparison.Ordinal))
            Assert.Contains(bundle.Manifest.TemplateBindings, binding => binding.Step.Contains("/compensation/", StringComparison.Ordinal));

        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.TemplateTopology", Guid.NewGuid().ToString("N"));
        var log = new ConcurrentQueue<string>();
        var reads = 0;

        try
        {
            await using var host = Host(store, cache, database, log, () => Interlocked.Increment(ref reads));
            var result = await Run(host, database, version.Name);

            Assert.Equal(
                topology is "parallel-denied" or "compensation" or "inline-compensation"
                    ? WorkflowExecutionStatus.Faulted
                    : WorkflowExecutionStatus.Success, result.Status);

            if (topology is "parallel-denied" or "compensation" or "inline-compensation")
                Assert.Contains("not permitted", result.ErrorMessage);

            Assert.Equal(0, reads);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    public static IEnumerable<object[]> RecoveryCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "good", "recover" }.Select(mode => new object[] { profile, mode }));

    [Theory, MemberData(nameof(RecoveryCases))]
    public async Task RecoveryRestoresTheDispatchBindingBeforeTheCallbackAndBypassesCatch(string profile, string mode)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Seed(store);

        var version = await Publish(store, "Recovery", Header("Recovery") + Code + "definitions:\n  - kind: try\n    steps: [" +
            Probe(mode) +
            "]\n    catches: [{exception: System.Exception, steps: [{kind: activity, activity: template.marker}]}]\n" + OtherSite);

        var log = new ConcurrentQueue<string>();
        var reads = 0;
        var faults = new FaultRepository(store) { FailCommit = _ => log.Any(item => item == "execute:" + mode) };
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.TemplateRecovery", Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid().ToString("N");

        try
        {
            await using (var first = Host(faults, Path.Combine(cache, "first"), database, log, () => Interlocked.Increment(ref reads)))
            {
                await database.StartHostLinksAsync(first);
                using var lease = await first.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync(version.Name, 1);
                var data = JsonSerializer.Deserialize("{\"Model\":{\"Value\":\"Ana\"}}", lease.Definition.WorkflowDataType!)!;

                await Assert.ThrowsAnyAsync<Exception>(() =>
                    first.GetRequiredService<IWorkflowClient>().RunAsync(lease.Definition, data, new() { InstanceId = id }));

                await first.GetRequiredService<WorkflowHost>().StopAsync(default);
            }

            faults.FailCommit = _ => false;
            await using var second = Host(store, Path.Combine(cache, "second"), database, log, () => Interlocked.Increment(ref reads));
            await database.StartHostLinksAsync(second);
            var result = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(id);
            Assert.Equal(mode == "recover" ? WorkflowExecutionStatus.Faulted : WorkflowExecutionStatus.Success, result.Status);
            Assert.Contains("recover:" + mode, log);
            Assert.DoesNotContain("handler", log);
            Assert.Equal(mode == "recover" ? 1 : 2, log.Count(item => item == "execute:" + mode));

            if (mode == "recover")
                Assert.Contains("not permitted", result.ErrorMessage);

            Assert.Equal(0, reads);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    public static IEnumerable<object[]> DebugCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { false, true }.Select(inline => new object[] { profile, inline }));

    [Theory, MemberData(nameof(DebugCases))]
    public async Task BreakpointRestartAndSteppingKeepThePinnedTemplateAfterANewerPublication(string profile, bool inline)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Seed(store);
        var leafName = inline ? "PausedLeaf" : "PausedTemplate";

        var version = await Publish(store, leafName,
            Header(leafName) +
            "definitions:\n  - {kind: renderTemplate, id: render, template: alpha, model: {path: workflow.Model}, output: [{source: Rendered, to: {path: workflow.Body}}]}\n");

        if (inline)
            version = await Publish(store, "PausedTemplate",
                Header("PausedTemplate") +
                "definitions:\n  - {kind: use, id: call, workflow: {name: PausedLeaf, version: 1}, input: [{target: Model, from: {path: workflow.Model}}], output: [{source: Body, to: {path: workflow.Body}}]}\n");

        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.TemplateDebug", Guid.NewGuid().ToString("N"));
        var reads = 0;
        string id;

        try
        {
            await using (var first = Host(store, Path.Combine(cache, "first"), database, new(), () => Interlocked.Increment(ref reads)))
            {
                await database.StartHostLinksAsync(first);

                id = await first.GetRequiredService<IWorkflowClient>().StartAsync(version.Name, 1, new { Model = new { Value = "Ana" } },
                    new() { DebugStopOnEntryActor = "operator" });

                await PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Paused);
                var ops = first.GetRequiredService<IWorkflowOperations>();
                var steps = await ops.GetStepTableAsync(id);
                var render = Assert.Single(steps, step => step.AuthorId == (inline ? "call/render" : "render"));
                Assert.StartsWith(inline ? "inline/PausedLeaf@1/workflow.yaml:" : "workflow.yaml:", render.SourceLocation);
                await ops.SetBreakpointsAsync(id, [new("render-point", render.AuthorId)], "operator");

                if (inline)
                    Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.ContinueAsync(id, "enter")).Status);

                await first.GetRequiredService<WorkflowHost>().StopAsync(default);
            }

            var draft = await store.SaveDefinitionAsync(
                new("alpha", 2, "template",
                    "template: {key: alpha, version: 2, engine: liquid, format: text, model: TemplateProbes.Model}\nimports: {catalogs: [template.probes@1.0.0]}\ncontent: newer"),
                null);

            Success(await Publisher(store).PublishAsync(draft.Id));
            await using var second = Host(store, Path.Combine(cache, "second"), database, new(), () => Interlocked.Increment(ref reads));
            await database.StartHostLinksAsync(second);
            var operations = second.GetRequiredService<IWorkflowOperations>();
            var inspection = (await operations.InspectAsync(id))!;
            var result = await operations.StepAsync(id, Assert.Single(inspection.Threads).ContinuationId, DebugStepKind.Over, "step");

            if (result.Status == WorkflowExecutionStatus.Paused)
                result = await operations.ContinueAsync(id, "continue");

            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);

            Assert.Equal("alpha Ana",
                ((RenderedTemplate)result.WorkflowData!.GetType().GetProperty("Body")!.GetValue(result.WorkflowData)!).Content);

            Assert.Equal(0, reads);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ConstantSelectionMissingTemplateAndModelConflictsAreCuratedWithoutPublishing(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Seed(store);

        var generic = await store.SaveDefinitionAsync(
            new("generic", 1, "template",
                "template: {key: generic, version: 1, engine: liquid, format: text, model: 'TemplateProbes.Envelope<System.String>'}\nimports: {catalogs: [template.probes@1.0.0]}\ncontent: '{{ Value }}'"),
            null);

        Success(await Publisher(store).PublishAsync(generic.Id));

        var cases = new[]
        {
            ("TPL010", Header("Refused") + "definitions: [{kind: renderTemplate, template: absent@1, model: {path: workflow.Model}}]"),
            ("TPL011",
                Header("Refused") +
                "definitions: [{kind: renderTemplate, template: alpha@1, modelType: TemplateProbes.Data, model: {path: workflow.Model}}]"),
            ("TPL013",
                Header("Refused") +
                Code.Replace("new IxIFlow.Templates.TemplateReference(\"alpha\", 1)",
                    "new IxIFlow.Templates.TemplateReference(workflow.Key, 1)") + "definitions:\n  - " + Probe("good")),
            ("TPL017",
                Header("Refused").Replace("template.probes@1.0.0", "template.probes@2.0.0") +
                "definitions: [{kind: renderTemplate, template: alpha@1, model: {path: workflow.Model}}]")
        };

        var revision = await store.GetCatalogRevisionAsync();
        var counted = TypedPublishingScenarios.CatalogProbe.Wrap(store, out var probe);
        probe.ReadOnly = true;

        var equivalent = await new WorkflowPublisher(counted, store, templateEngines: Engines()).ValidateAsync(new("Refused", 1, "workflow",
            Header("Refused") +
            "definitions: [{kind: renderTemplate, template: generic@1, modelType: 'TemplateProbes.Envelope<string>', model: {path: workflow.Generic}}]"));

        Assert.True(equivalent.IsValid, string.Join("; ", equivalent.Diagnostics));

        foreach (var (code, yaml) in cases)
        {
            var report =
                await new WorkflowPublisher(counted, store, templateEngines: Engines()).ValidateAsync(new("Refused", 1, "workflow", yaml));

            Assert.Equal(code, Assert.Single(report.Diagnostics).Code);
            Assert.False(report.IsValid);
        }

        Assert.Equal(0, probe.Writes);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Null(await store.GetWorkflowVersionAsync("Refused", 1));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CallerAndVerifiedCalleeWithDifferentBytesForOneTemplateAreTpl020(string profile)
    {
        var source = new InMemoryWorkflowStateRepository();
        await Seed(source);

        var leaf = await Publish(source, "ConflictingLeaf",
            Header("ConflictingLeaf") + "definitions: [{kind: renderTemplate, template: alpha@1, model: {path: workflow.Model}}]");

        var original = await Read(source, leaf);
        var entries = original.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var template = TemplateJson.Deserialize(entries["templates/alpha@1.json"]);

        entries["templates/alpha@1.json"] = TemplateJson.Serialize(new(template.Key, template.Version, template.Engine, template.Format,
            template.ModelType, template.ModelTypeId, template.ModelPackages, template.DisplayName, template.Description,
            [new("", "different {{ Value }}")]));

        var archive = WorkflowBundleArchive.Create(original.Manifest, entries);
        var digest = ArtifactDigest.Of(archive);
        var verified = await WorkflowBundleArchive.ReadAsync(new MemoryStream(archive), digest);
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Seed(store);
        await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(archive));

        await store.RegisterWorkflowVersionAsync(leaf with
        {
            ArtifactDigest = digest, PublishedDraft = null, ManifestJson = JsonSerializer.Serialize(verified.Manifest)
        });

        var yaml = Header("Conflict") +
            "definitions:\n  - {kind: renderTemplate, template: alpha@1, model: {path: workflow.Model}}\n  - {kind: use, workflow: {name: ConflictingLeaf, version: 1}}\n";

        var report = await Publisher(store).ValidateAsync(new("Conflict", 1, "workflow", yaml));
        Assert.Equal("TPL020", Assert.Single(report.Diagnostics).Code);
        Assert.Null(await store.GetWorkflowVersionAsync("Conflict", 1));
    }
}
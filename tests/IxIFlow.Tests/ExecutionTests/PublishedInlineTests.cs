using System.Reflection;
using System.IO.Compression;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.Infrastructure.PublishedInlineScenario;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-17")]
[Trait("Spec", "F-18")]
[Trait("Spec", "F-10")]
public sealed class PublishedInlineTests
{
    [Fact]
    public async Task NestedDuplicateSitesCopyVerifiedBodiesAndColdLoad()
    {
        var store = new InMemoryWorkflowStateRepository();
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.InlineTests", Guid.NewGuid().ToString("N"));
        try { await ColdLoad(store, cache); }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    [Fact]
    public async Task PublishedExecutableNamesAreStableAcrossSourceEditsAndDistinctAcrossIdentities()
    {
        var first = new InMemoryWorkflowStateRepository();
        var second = new InMemoryWorkflowStateRepository();
        var a = await Publish(first, "Names", Empty("Names"));
        var b = await Publish(second, "Names", Empty("Names") + "# presentation only\n");
        var c = await Publish(second, "Names", Empty("Names", 2), 2);
        var x = await Read(first, a);
        var y = await Read(second, b);
        var z = await Read(second, c);
        Assert.Equal(x.Manifest.EntryType, y.Manifest.EntryType);
        Assert.NotEqual(x.Manifest.EntryType, z.Manifest.EntryType);
        Assert.Equal("IxIFlow.Workflow.Names.v1", ResolvedWorkflowPackages.AssemblyIdentity(x.Entries[WorkflowBundleArchive.AssemblyPath]).Name);
        Assert.Equal(WorkflowBundleIdentity.ClassName("Names", 1), x.Manifest.EntryType.Split('.').Last());
        Assert.NotEqual(WorkflowBundleIdentity.AssemblyName("A/B", 1), WorkflowBundleIdentity.AssemblyName("A%2FB", 1));
    }

    [Fact]
    public async Task LatestPublishedAndExplicitDeprecatedPinsBecomeCanonical()
    {
        var store = new InMemoryWorkflowStateRepository();
        var v1 = await Publish(store, "Leaf", Empty("Leaf"));
        await Publish(store, "Leaf", Empty("Leaf", 2), 2);
        await store.SetWorkflowVersionStateAsync("Leaf", 1, WorkflowVersionState.Deprecated, new(v1.LifecycleRevision));
        var caller = await Publish(store, "Latest", Caller("Latest", "Leaf"));
        Assert.Equal(2, Assert.Single((await Read(store, caller)).Manifest.InlineWorkflows).Version);
        var pinned = await Publish(store, "Pinned", Caller("Pinned", "Leaf", "1"));
        var bundle = await Read(store, pinned);
        Assert.Equal(1, Assert.Single(bundle.Manifest.InlineWorkflows).Version);
        var yaml = Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.YamlPath].Span);
        Assert.Contains("Leaf@1", yaml);
        Assert.Contains("version: 1", yaml);
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("framework")]
    [InlineData("checkpoint")]
    [InlineData("shared")]
    [InlineData("assembly")]
    [InlineData("source")]
    [InlineData("closure")]
    public async Task CalleeProfileAndCanonicalSourcesAreVerified(string mutation)
    {
        var store = new InMemoryWorkflowStateRepository();
        var original = await Publish(store, "Leaf", Empty("Leaf"));
        var bundle = await Read(store, original);
        var manifest = mutation switch
        {
            "engine" => bundle.Manifest with { EngineVersion = "99.0.0.0" },
            "framework" => bundle.Manifest with { TargetFramework = "net99.0" },
            "checkpoint" => bundle.Manifest with { CheckpointContractVersion = 1 },
            "shared" => bundle.Manifest with { SharedAssemblies = new Dictionary<string, string> { ["System.Runtime, Version=99.0.0.0"] = "99.0.0.0" } },
            _ => bundle.Manifest
        };
        var entries = bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        if (mutation == "source") entries[WorkflowBundleArchive.YamlPath] = Encoding.UTF8.GetBytes(Empty("Leaf") + "# noncanonical\n");
        if (mutation == "assembly") entries[WorkflowBundleArchive.AssemblyPath] = File.ReadAllBytes(typeof(FirstData).Assembly.Location);
        if (mutation == "closure") entries.Remove("private/System.Payments.dll");
        var bytes = WorkflowBundleArchive.Create(manifest, entries);
        var digest = ArtifactDigest.Of(bytes);
        var modified = await WorkflowBundleArchive.ReadAsync(new MemoryStream(bytes), digest);
        // An independent catalog permits a valid immutable row with the altered artifact.
        var target = new InMemoryWorkflowStateRepository();
        await target.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
        await target.RegisterWorkflowVersionAsync(original with { ArtifactDigest = digest, PublishedDraft = null, ManifestJson = JsonSerializer.Serialize(modified.Manifest) });
        var draft = await target.SaveDefinitionAsync(new("Caller", 1, "workflow", Caller("Caller", "Leaf")), null);
        var report = await new WorkflowPublisher(target, target, Options()).PublishAsync(draft.Id);
        Assert.Equal(mutation == "source" ? "PUB013" : "PUB009", Assert.Single(report.Diagnostics).Code);
        Assert.Null(await target.GetWorkflowVersionAsync("Caller", 1));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("site")]
    [InlineData("digest")]
    [InlineData("structure")]
    [InlineData("contract")]
    [InlineData("entry")]
    [InlineData("assembly")]
    [InlineData("yaml")]
    [InlineData("cs")]
    [InlineData("dll")]
    public async Task LoaderRejectsMissingPayloadAndForgedInlineRecipes(string mutation)
    {
        var store = new InMemoryWorkflowStateRepository();
        await Publish(store, "Leaf", Empty("Leaf"));
        var caller = await Publish(store, "Caller", Caller("Caller", "Leaf"));
        var bundle = await Read(store, caller);
        var pin = Assert.Single(bundle.Manifest.InlineWorkflows);
        var changed = mutation switch
        {
            "site" => pin with { Sites = ["root/99"] },
            "digest" => pin with { BundleDigest = ArtifactDigest.Of([1, 2, 3]) },
            "structure" => pin with { StructureHash = "forged" },
            "contract" => pin with { ContractHash = "forged" },
            "entry" => pin with { EntryType = "Forged.Entry" },
            "assembly" => pin with { AssemblyName = "Forged.Assembly" },
            _ => pin
        };
        var manifest = bundle.Manifest with { InlineWorkflows = mutation == "missing" ? [] : mutation == "duplicate" ? [pin, pin] : [changed] };
        var entries = bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var prefix = WorkflowBundleIdentity.InlineSourceDirectory("Leaf", 1);
        if (mutation is "yaml" or "cs") entries.Remove(prefix + (mutation == "yaml" ? WorkflowBundleArchive.YamlPath : WorkflowBundleArchive.SourcePath));
        if (mutation == "dll") entries.Remove("private/" + pin.AssemblyName + ".dll");
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.InlineTests", Guid.NewGuid().ToString("N"));
        try
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(async () =>
            {
                var bytes = WorkflowBundleArchive.Create(manifest, entries);
                var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
                using var loader = new WorkflowBundleLoader(store, new(cache));
                using var lease = await loader.AcquireAsync(digest, default);
            });
            Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    [Fact]
    public async Task CodeDefinedByNameUseIsPub012AndDemandConflictIsPub011()
    {
        var store = new InMemoryWorkflowStateRepository();
        var code = Workflow.Create<FirstData>("Code").Input(data => data.Value).Output(data => data.Value).Build();
        var contract = WorkflowDefinitionValidator.GetContract(code);
        await store.RegisterWorkflowVersionAsync(new("Code", 1, WorkflowDefinitionValidator.Inspect(code), contract.ToJson(), contract.Hash,
            WorkflowDescriptor.Of(code), Invocations: []));
        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", Caller("Caller", "Code")), null);
        var refused = await new WorkflowPublisher(store, store, Options()).PublishAsync(draft.Id);
        Assert.Equal("PUB012", Assert.Single(refused.Diagnostics).Code);
        Assert.Contains("useType", refused.Diagnostics[0].Message);
        await Publish(store, "Demanded", Empty("Demanded") + "demands: [{key: region, value: east}]\n");
        var conflict = await store.SaveDefinitionAsync(new("Conflict", 1, "workflow", Caller("Conflict", "Demanded") + "demands: [{key: region, value: west}]\n"), null);
        var failed = await new WorkflowPublisher(store, store, Options()).PublishAsync(conflict.Id);
        Assert.Equal("PUB011", Assert.Single(failed.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidationUsesTheSameVerifiedClosureWithoutWritingTheCaller()
    {
        var store = new InMemoryWorkflowStateRepository();
        await Publish(store, "Leaf", Empty("Leaf"));
        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", Caller("Caller", "Leaf", twice: true)), null);
        var publisher = new WorkflowPublisher(store, store, Options());
        var report = await publisher.ValidateAsync(draft);
        Assert.True(report.IsValid, string.Join("; ", report.Diagnostics));
        Assert.Null(await store.GetWorkflowVersionAsync("Caller", 1));
        Assert.Equal(draft, await store.GetDefinitionAsync("workflow", "Caller", 1));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("contents")]
    public async Task InlinePrivateAssemblyConflictsArePub010AtTheAuthoredUse(string mutation)
    {
        var source = new InMemoryWorkflowStateRepository();
        var leaf = await Publish(source, "Leaf", Empty("Leaf"));
        var bundle = await Read(source, leaf);
        var selected = typeof(FirstData).Assembly.GetName();
        var altered = WorkflowCompilation.Compile("[assembly: System.Reflection.AssemblyVersion(\"" +
            (mutation == "identity" ? "99.0.0.0" : selected.Version!.ToString()) + "\")] public class Different { }",
            WorkflowCompilation.CreateReferences(new()), "workflow.yaml", default, selected.Name);
        Assert.True(altered.Success);
        var entries = bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        entries["private/" + selected.Name + ".dll"] = altered.Assembly;
        var archive = WorkflowBundleArchive.Create(bundle.Manifest, entries);
        var digest = ArtifactDigest.Of(archive);
        var verified = await WorkflowBundleArchive.ReadAsync(new MemoryStream(archive), digest);
        var store = new InMemoryWorkflowStateRepository();
        await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(archive));
        await store.RegisterWorkflowVersionAsync(leaf with { ArtifactDigest = digest, PublishedDraft = null, ManifestJson = JsonSerializer.Serialize(verified.Manifest) });
        var yaml = "# authored offset\n\n" + Caller("Caller", "Leaf");
        var report = await new WorkflowPublisher(store, store, Options()).ValidateAsync(new("Caller", 1, "workflow", yaml));
        var error = Assert.Single(report.Diagnostics);
        Assert.Equal("PUB010", error.Code);
        Assert.Equal(7, error.Line);
        Assert.Equal(WorkflowPublishDiagnosticClass.Authoring, error.Class);
    }

    [Fact]
    public async Task TypedCodeClosurePublishesAndColdLoadsWithoutACalleeArtifact()
    {
        var store = new InMemoryWorkflowStateRepository();
        var definition = InlineConstruction.Typed<PublishedInlineTypedCallee, FirstData>();
        using var catalog = new WorkflowCatalog(store);
        await catalog.RegisterWorkflowAsync(definition);
        var yaml = Caller("Typed", "unused").Replace("kind: use", "kind: useType").Replace("workflow: {name: unused}",
            "workflowType: " + typeof(PublishedInlineTypedCallee).FullName);
        var caller = await Publish(store, "Typed", yaml);
        var bundle = await Read(store, caller);
        var pin = Assert.Single(bundle.Manifest.InlineWorkflows);
        Assert.Null(pin.BundleDigest);
        Assert.Equal(typeof(PublishedInlineTypedCallee).FullName, pin.EntryType);
        Assert.Equal(typeof(PublishedInlineTypedCallee).Assembly.GetName().Name, pin.AssemblyName);
        Assert.Contains("private/" + pin.AssemblyName + ".dll", bundle.Entries.Keys);
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.InlineTyped", Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = PublishedBundleScenario.Host(store, cache);
            var client = host.GetRequiredService<IWorkflowClient>();
            var result = await client.AwaitAsync(await client.StartAsync("Typed", 1, new FirstData { Value = "typed cold" }), WorkflowAwaitCondition.Finished);
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal("typed cold", result.WorkflowData!.GetType().GetProperty("Value")!.GetValue(result.WorkflowData));
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    [Fact]
    public async Task RecursiveTypedInlineIsACuratedPub013()
    {
        var store = new InMemoryWorkflowStateRepository();
        var yaml = "workflow: {name: RecursiveCaller, dataType: Data}\ndefinitions:\n  - kind: useType\n    workflowType: " + typeof(PublishedInlineRecursiveCallee).FullName;
        var report = await new WorkflowPublisher(store, store, Options()).ValidateAsync(new("RecursiveCaller", 1, "workflow", yaml));
        var error = Assert.Single(report.Diagnostics);
        Assert.Equal("PUB013", error.Code);
        Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TypedSharedClosureRequiresTheSameHostProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.InlineShared", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var assemblyName = "Inline.Shared." + Guid.NewGuid().ToString("N");
        var emitted = WorkflowCompilation.Compile("""
            using IxIFlow.Builders.Interfaces;
            using IxIFlow.Core;
            public sealed class InlineProfileData { public string Value { get; set; } = ""; }
            [Workflow("InlineProfileCallee")]
            public sealed class InlineProfileWorkflow : IWorkflow<InlineProfileData>
            { public void Build(IWorkflowBuilder<InlineProfileData> builder) { } }
            """, WorkflowCompilation.CreateReferences(new()), "workflow.yaml", default, assemblyName);
        Assert.True(emitted.Success);
        var file = Path.Combine(root, assemblyName + ".dll");
        await File.WriteAllBytesAsync(file, emitted.Assembly);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
        var store = new InMemoryWorkflowStateRepository();
        using var catalog = new WorkflowCatalog(store);
        await catalog.RegisterWorkflowAsync(new() { Name = "InlineProfileCallee", Version = 1, WorkflowDataType = assembly.GetType("InlineProfileData")! });
        var options = new WorkflowPublisherOptions { SharedContractAssemblies = [assembly] };
        var caller = await Publish(store, "SharedCaller", """
            workflow: {name: SharedCaller, dataType: InlineProfileData}
            definitions: [{kind: useType, workflowType: InlineProfileWorkflow}]
            """, options: options);
        var bundle = await Read(store, caller);
        Assert.Contains(assembly.FullName!, bundle.Manifest.SharedAssemblies.Keys);
        Assert.DoesNotContain("private/" + assemblyName + ".dll", bundle.Entries.Keys);
        try
        {
            using var loader = new WorkflowBundleLoader(store, new(Path.Combine(root, "shared"))
                { SharedProfile = WorkflowSharedAssemblies.CreateProfile([assembly]) });
            using var lease = await loader.AcquireAsync(caller.ArtifactDigest!.Value, default);
            Assert.Single(WorkflowDefinitionValidator.GetInlineWorkflows(lease.Definition));
            using var missing = new WorkflowBundleLoader(store, new(Path.Combine(root, "missing")));
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(async () => { using var ignored = await missing.AcquireAsync(caller.ArtifactDigest.Value, default); });
            Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
        }
        finally
        {
            // The default context retains its declared contract assembly for the test process.
            foreach (var directory in new[] { "shared", "missing" })
                if (Directory.Exists(Path.Combine(root, directory))) Directory.Delete(Path.Combine(root, directory), true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderRequiresTheNewFormatAndAnExplicitInlineInventory(bool oldFormat)
    {
        var store = new InMemoryWorkflowStateRepository();
        var row = await Publish(store, "Leaf", Empty("Leaf"));
        var bundle = await Read(store, row);
        var bytes = WorkflowBundleArchive.Create(bundle.Manifest, bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
        using var changed = new MemoryStream();
        changed.Write(bytes);
        using (var zip = new ZipArchive(changed, ZipArchiveMode.Update, true))
        {
            var entry = zip.GetEntry(WorkflowBundleArchive.ManifestPath)!;
            JsonObject json;
            using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            if (oldFormat) json["FormatVersion"] = 4;
            else json.Remove("InlineWorkflows");
            entry.Delete();
            using var output = new StreamWriter(zip.CreateEntry(WorkflowBundleArchive.ManifestPath).Open());
            await output.WriteAsync(json.ToJsonString());
        }
        var archive = changed.ToArray();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => WorkflowBundleArchive.ReadAsync(new MemoryStream(archive), ArtifactDigest.Of(archive)));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
    }

    [Fact]
    public async Task ConflictingTransitiveAndDirectInlinePinsArePub013()
    {
        var source = new InMemoryWorkflowStateRepository();
        var leaf = await Publish(source, "Leaf", Empty("Leaf"));
        var middle = await Publish(source, "Middle", Caller("Middle", "Leaf"));
        var original = await Read(source, middle);
        var archive = WorkflowBundleArchive.Create(original.Manifest with
        {
            InlineWorkflows = [Assert.Single(original.Manifest.InlineWorkflows) with { StructureHash = "forged nested pin" }]
        }, original.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
        var digest = ArtifactDigest.Of(archive);
        var verified = await WorkflowBundleArchive.ReadAsync(new MemoryStream(archive), digest);
        var store = new InMemoryWorkflowStateRepository();
        await using (var bytes = await source.OpenAsync(leaf.ArtifactDigest!.Value)) await store.PutAsync(ArtifactKind.Bundle, bytes!);
        await store.RegisterWorkflowVersionAsync(leaf);
        await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(archive));
        await store.RegisterWorkflowVersionAsync(middle with { ArtifactDigest = digest, PublishedDraft = null, ManifestJson = JsonSerializer.Serialize(verified.Manifest) });
        var yaml = Caller("Caller", "Middle") + "  - kind: use\n    id: direct\n    workflow: {name: Leaf, version: 1}\n";
        var report = await new WorkflowPublisher(store, store, Options()).ValidateAsync(new("Caller", 1, "workflow", yaml));
        Assert.Equal("PUB013", Assert.Single(report.Diagnostics).Code);
    }

    [Fact]
    public async Task PublishingRefusesACalleeWithAnIncorrectNestedSiteInventory()
    {
        var source = new InMemoryWorkflowStateRepository();
        var leaf = await Publish(source, "Leaf", Empty("Leaf"));
        var middle = await Publish(source, "Middle", Caller("Middle", "Leaf"));
        var bundle = await Read(source, middle);
        var archive = WorkflowBundleArchive.Create(bundle.Manifest with
        {
            InlineWorkflows = [Assert.Single(bundle.Manifest.InlineWorkflows) with { Sites = ["root/99"] }]
        }, bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
        var digest = ArtifactDigest.Of(archive);
        var verified = await WorkflowBundleArchive.ReadAsync(new MemoryStream(archive), digest);
        var store = new InMemoryWorkflowStateRepository();
        await using (var bytes = await source.OpenAsync(leaf.ArtifactDigest!.Value)) await store.PutAsync(ArtifactKind.Bundle, bytes!);
        await store.RegisterWorkflowVersionAsync(leaf);
        await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(archive));
        await store.RegisterWorkflowVersionAsync(middle with { ArtifactDigest = digest, PublishedDraft = null, ManifestJson = JsonSerializer.Serialize(verified.Manifest) });
        var report = await new WorkflowPublisher(store, store, Options()).ValidateAsync(new("Caller", 1, "workflow", Caller("Caller", "Middle")));
        Assert.Equal("PUB013", Assert.Single(report.Diagnostics).Code);
    }
}

[Workflow("InlineCodeCallee")]
public sealed class PublishedInlineTypedCallee : IWorkflow<FirstData>
{
    public void Build(IWorkflowBuilder<FirstData> builder) => builder.Input(data => data.Value).Output(data => data.Value);
}

[Workflow("InlineRecursiveCallee")]
public sealed class PublishedInlineRecursiveCallee : IWorkflow<FirstData>
{
    public void Build(IWorkflowBuilder<FirstData> builder) => builder.Use<PublishedInlineRecursiveCallee, FirstData>();
}

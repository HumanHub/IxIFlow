using System.Text;
using IxIFlow.Authoring;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Tests.Infrastructure;

public static class PublishedInlineScenario
{
    public static byte[] ConflictingPrivateAssembly()
    {
        var compilation = CSharpCompilation.Create("System.Payments", [CSharpSyntaxTree.ParseText("""
            [assembly: System.Reflection.AssemblyVersion("99.0.0.0")]
            namespace IxIFlow.BundleFixtures;
            public sealed class FirstData { public int Value { get; set; } }
            """)], WorkflowCompilation.ReferencePaths().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return output.ToArray();
    }

    public static WorkflowPublisherOptions Options() => new()
    {
        SourceOptions = new()
        {
            Types = { ["Data"] = "global::" + typeof(FirstData).FullName, ["Signal"] = "global::" + typeof(Signal).FullName }
        }
    };

    public static string Empty(string name, int version = 1) =>
        $"workflow: {{name: {name}, version: {version}, dataType: Data, input: [Value], output: [Value]}}\ndefinitions: []\n";

    public static string Caller(string name, string target, string? version = null, bool twice = false) =>
        $"workflow: {{name: {name}, version: 1, dataType: Data, input: [Value], output: [Value]}}\ndefinitions:\n" +
        Site("first", target, version) + (twice ? Site("second", target, version) : "");

    private static string Site(string id, string target, string? version) =>
        "  - kind: use\n    id: " + id + "\n    workflow: {name: " + target + (version == null ? "" : ", version: " + version) + "}\n" +
        "    input: [{target: Value, from: {path: workflow.Value}}]\n    output: [{source: Value, to: {path: workflow.Value}}]\n";

    public static async Task<WorkflowVersion> Publish(IWorkflowStateRepository store, string name, string yaml,
        int version = 1, WorkflowPublisherOptions? options = null)
    {
        var draft = await store.SaveDefinitionAsync(new(name, version, WorkflowDraftKinds.Workflow, yaml), null);
        var result = await new WorkflowPublisher(store, store, options ?? Options()).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return result.Workflow!;
    }

    public static async Task<WorkflowBundle> Read(IWorkflowStateRepository store, WorkflowVersion row)
    {
        await using var stream = await store.OpenAsync(row.ArtifactDigest!.Value);
        return await WorkflowBundleArchive.ReadAsync(stream!, row.ArtifactDigest.Value);
    }

    public static Task<WorkflowVersion> Retire(IWorkflowStateRepository store, WorkflowVersion row) =>
        store.SetWorkflowVersionStateAsync(row.Name, row.Version, WorkflowVersionState.Retired, new CatalogMutationToken(row.LifecycleRevision));

    public static async Task ColdLoad(IWorkflowStateRepository store, string cache,
        Action<IServiceCollection>? configure = null, Func<IServiceProvider, Task>? startLinks = null)
    {
        var leaf = await Publish(store, "InlineLeaf", Empty("InlineLeaf"));
        var middle = await Publish(store, "InlineMiddle", Caller("InlineMiddle", "InlineLeaf"));
        var root = await Publish(store, "InlineRoot", Caller("InlineRoot", "InlineMiddle", twice: true));
        var bundle = await Read(store, root);
        Assert.Equal(6, bundle.Manifest.FormatVersion);
        Assert.Equal(2, bundle.Manifest.InlineWorkflows.Count);
        Assert.All(bundle.Manifest.InlineWorkflows, pin => Assert.Equal(2, pin.Sites.Count));
        Assert.Equal(2, root.Dependencies!.Count);
        Assert.All(root.Dependencies, edge => Assert.Equal(WorkflowDependencyKind.Inline, edge.Kind));
        foreach (var row in new[] { leaf, middle })
        {
            var original = await Read(store, row);
            var prefix = WorkflowBundleIdentity.InlineSourceDirectory(row.Name, row.Version);
            Assert.Equal(original.Entries[WorkflowBundleArchive.YamlPath].ToArray(), bundle.Entries[prefix + WorkflowBundleArchive.YamlPath].ToArray());
            Assert.Equal(original.Entries[WorkflowBundleArchive.SourcePath].ToArray(), bundle.Entries[prefix + WorkflowBundleArchive.SourcePath].ToArray());
            Assert.Equal(original.Entries[WorkflowBundleArchive.AssemblyPath].ToArray(),
                bundle.Entries["private/" + WorkflowBundleIdentity.AssemblyName(row.Name, row.Version) + ".dll"].ToArray());
            Assert.DoesNotContain("inline/" + row.Name + "@1/inline/", string.Join("\n", bundle.Entries.Keys));
        }
        Assert.Single(bundle.Entries.Keys.Where(key => key == "private/System.Payments.dll"));
        Assert.Contains("version: 1", Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.YamlPath].Span));
        Assert.Empty(bundle.Manifest.Invocations);
        await using var host = PublishedBundleScenario.Host(store, cache, configure: configure);
        if (startLinks != null) await startLinks(host);
        using var definition = await host.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync(root.Name, 1);
        Assert.Equal(root.StructureHash, WorkflowDefinitionValidator.Inspect(definition.Definition));
        Assert.Equal(6, definition.Definition.BundleSources!.Count);
        var client = host.GetRequiredService<IWorkflowClient>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var id = await client.StartAsync(root.Name, 1, new FirstData { Value = "copied body" }, cancellationToken: timeout.Token);
        var result = await client.AwaitAsync(id, WorkflowAwaitCondition.Finished, timeout.Token);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("copied body", result.WorkflowData!.GetType().GetProperty("Value")!.GetValue(result.WorkflowData));
    }

    public static async Task PackageClosure(IWorkflowStateRepository store, string cache,
        Action<IServiceCollection>? configure = null, Func<IServiceProvider, Task>? startLinks = null)
    {
        var publisher = new WorkflowPublisher(store, store, Options());
        var packages = ActivityPackageFixture.Create();
        await publisher.UploadPackageAsync(new MemoryStream(packages.Dependency));
        await publisher.UploadPackageAsync(new MemoryStream(packages.Activity));
        var leaf = await Publish(store, "PackageLeaf", """
            workflow: {name: PackageLeaf, dataType: Packaged.Data, input: [Value], output: [Result]}
            imports: {catalogs: [fixture.activities@1.0.0]}
            definitions:
              - kind: activity
                activity: fixture.echo
                input: [{target: Text, from: {path: workflow.Value}}]
                output: [{source: Result, to: {path: workflow.Result}}]
            """);
        var activity = (await store.GetPackageAsync("fixture.activities", "1.0.0"))!;
        await store.SetPackageWithdrawnAsync(activity.Id, activity.Version, true, new(activity.LifecycleRevision));
        var parent = await Publish(store, "PackageParent", Caller("PackageParent", "PackageLeaf").Replace("source: Value", "source: Result"));
        var manifest = (await Read(store, parent)).Manifest;
        Assert.Equal(2, manifest.ActivityPackages.Count);
        Assert.Equal((await Read(store, leaf)).Manifest.ActivityPackages.Select(pin => (pin.Id, pin.Version, pin.Digest)),
            manifest.ActivityPackages.Select(pin => (pin.Id, pin.Version, pin.Digest)));
        Assert.Equal(2, parent.ArtifactReferences!.Count);
        Assert.Empty(parent.AuthoringPackageReferences);
        await using var host = PublishedBundleScenario.Host(store, cache, configure: configure);
        if (startLinks != null) await startLinks(host);
        var client = host.GetRequiredService<IWorkflowClient>();
        var result = await client.AwaitAsync(await client.StartAsync("PackageParent", 1, new FirstData { Value = "closure" }), WorkflowAwaitCondition.Finished);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("package-v1:closure", result.WorkflowData!.GetType().GetProperty("Value")!.GetValue(result.WorkflowData));
    }
}

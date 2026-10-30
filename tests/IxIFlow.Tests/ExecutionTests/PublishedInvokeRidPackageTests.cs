using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "F-18")]
[Trait("Feature", "registration")]
public sealed class PublishedInvokeRidPackageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishPreservesDisjointRuntimeOnlyPackageImplementations(bool callerImportsRoot)
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        await UploadClosure(publisher, "fixture.root", "fixture.win", "fixture.linux");
        var child = await Publish(store, publisher, "Callee", "imports: {catalogs: [fixture.root@1.0.0]}", "definitions: []");

        var caller = await Publish(store, publisher, "Caller", callerImportsRoot ? "imports: {catalogs: [fixture.root@1.0.0]}" : "", """
            definitions:
              - kind: invokeWorkflow
                workflow: {name: Callee, version: 1}
                input: [{target: Value, from: {kind: path, path: workflow.Value}}]
                output: [{source: Value, to: {path: workflow.Value}}]
            """);

        await using var childStream = await store.OpenAsync(child.ArtifactDigest!.Value);
        var childBundle = await WorkflowBundleArchive.ReadAsync(childStream!, child.ArtifactDigest.Value);
        await using var stream = await store.OpenAsync(caller.ArtifactDigest!.Value);
        var bundle = await WorkflowBundleArchive.ReadAsync(stream!, caller.ArtifactDigest.Value);

        Assert.Equal(JsonSerializer.Serialize(childBundle.Manifest.ActivityPackages),
            JsonSerializer.Serialize(bundle.Manifest.ActivityPackages));

        Assert.Equal(3, bundle.Manifest.ActivityPackages.Count);

        foreach (var (id, rid) in new[] { ("fixture.win", "win-x64"), ("fixture.linux", "linux-x64") })
        {
            var pin = bundle.Manifest.ActivityPackages.Single(pin => pin.Id == id);
            Assert.True(pin.AppliesTo(rid));
            Assert.False(pin.AppliesTo(rid == "win-x64" ? "linux-x64" : "win-x64"));
        }

        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.RidInvoke", Guid.NewGuid().ToString("N"));

        try
        {
            await using var host = PublishedBundleScenario.Host(store, cache);
            using var loaded = await host.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync("Caller", 1);
            Assert.Equal("RidInvoke.Data", loaded.Definition.WorkflowDataType!.FullName);
            Assert.Equal(child.ContractJson, Assert.Single(loaded.Definition.Steps).InvokedContract!.ToJson());

            var platform = AssemblyLoadContext.GetLoadContext(loaded.Definition.WorkflowDataType.Assembly)!
                .LoadFromAssemblyName(new("Platform")).GetType("PlatformOnly")!;

            Assert.Equal(RuntimeInformation.RuntimeIdentifier, platform.GetProperty("Rid")!.GetValue(Activator.CreateInstance(platform)));
            var data = Activator.CreateInstance(loaded.Definition.WorkflowDataType)!;
            data.GetType().GetProperty("Value")!.SetValue(data, "pinned");
            var client = host.GetRequiredService<IWorkflowClient>();
            var result = await client.AwaitAsync(await client.StartAsync("Caller", 1, data), WorkflowAwaitCondition.Finished);
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal("pinned", result.WorkflowData!.GetType().GetProperty("Value")!.GetValue(result.WorkflowData));
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, recursive: true);
        }
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public async Task PublishUnionStillRejectsDifferentPackagesThatSupplyTheSameAssemblyWithinOneRid(string conflictingRid)
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        await UploadClosure(publisher, "fixture.first", "fixture.first-win", "fixture.first-linux");

        await UploadClosure(publisher, "fixture.second", "fixture.second-win", "fixture.second-linux", includeData: false,
            conflictingRid: conflictingRid);

        await Publish(store, publisher, "Callee", "imports: {catalogs: [fixture.first@1.0.0]}", "definitions: []");

        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", """
                                                                                 workflow: {name: Caller, dataType: RidInvoke.Data}
                                                                                 imports: {catalogs: [fixture.second@1.0.0]}
                                                                                 definitions:
                                                                                   - kind: invokeWorkflow
                                                                                     workflow: {name: Callee, version: 1}
                                                                                 """), null);

        var result = await publisher.PublishAsync(draft.Id);
        Assert.False(result.Success);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("PUB010", error.Code);
        Assert.Equal(WorkflowPublishStage.Validate, error.Stage);
        Assert.Equal(5, error.Line);
        Assert.Contains("Platform", error.Message);
        Assert.Contains("runtime", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.GetWorkflowVersionAsync("Caller", 1));
        Assert.Equal(draft, await store.GetDefinitionAsync("workflow", "Caller", 1));
    }

    [Fact]
    public async Task PublishUnionDoesNotPromoteExistingRidOnlyReferences()
    {
        var store = new InMemoryWorkflowStateRepository();
        await UploadClosure(new WorkflowPublisher(store, store), "fixture.root", "fixture.win", "fixture.linux");
        var resolver = new WorkflowPackageResolver(store, store, new());
        using var callee = await resolver.ResolveAsync(new List<string> { "fixture.root@1.0.0" }, PackageAcquisition.Persist, [], default);
        using var caller = new ResolvedWorkflowPackages();

        foreach (var pin in callee.Pins)
            caller.Add(pin, callee.Contents[pin.Id], compile: false);

        await resolver.UnionAsync(caller, callee.Pins, new DocumentLocation(1, 1), default);
        var reference = Assert.Single(caller.References);
        Assert.Equal("Invoke.RidData.dll", Path.GetFileName(reference));
        Assert.Equal(3, caller.Pins.Count);
    }

    private static async Task<WorkflowVersion> Publish(InMemoryWorkflowStateRepository store, WorkflowPublisher publisher,
        string name, string imports, string definitions)
    {
        var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow",
            $"workflow: {{name: {name}, dataType: RidInvoke.Data}}\n{imports}\n{definitions}"), null);

        var result = await publisher.PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));

        return result.Workflow!;
    }

    private static async Task UploadClosure(WorkflowPublisher publisher, string root, string windows, string linux,
        bool includeData = true, string? conflictingRid = null)
    {
        foreach (var (id, rid) in new[] { (windows, "win-x64"), (linux, "linux-x64") })
        {
            var assembly = conflictingRid == null || conflictingRid == rid ? "Platform" : "OtherPlatform";

            await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package(id, "1.0.0", new()
            {
                ["lib/net10.0/" + assembly + ".dll"] = Compile(assembly, $$"""
                                                                           public sealed class PlatformOnly { public string Rid => "{{rid}}"; }
                                                                           """)
            })));
        }

        var graph = JsonSerializer.Serialize(new
        {
            runtimes = new Dictionary<string, object>
            {
                ["win-x64"] = new Dictionary<string, object> { [root] = new Dictionary<string, string> { [windows] = "[1.0.0]" } },
                ["linux-x64"] = new Dictionary<string, object> { [root] = new Dictionary<string, string> { [linux] = "[1.0.0]" } }
            }
        });

        var entries = new Dictionary<string, byte[]> { ["runtime.json"] = Encoding.UTF8.GetBytes(graph) };

        if (includeData)
            entries["lib/net10.0/Invoke.RidData.dll"] = Compile("Invoke.RidData", """
                                                                                  namespace RidInvoke;
                                                                                  public sealed class Data
                                                                                  {
                                                                                      [IxIFlow.ActivitySdk.WorkflowInputOutput]
                                                                                      public string Value { get; set; } = "";
                                                                                  }
                                                                                  """);

        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package(root, "1.0.0", entries)));
    }

    private static byte[] Compile(string name, string source)
    {
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
            WorkflowCompilation.ReferencePaths().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var bytes = new MemoryStream();
        var emitted = compilation.Emit(bytes);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));

        return bytes.ToArray();
    }
}
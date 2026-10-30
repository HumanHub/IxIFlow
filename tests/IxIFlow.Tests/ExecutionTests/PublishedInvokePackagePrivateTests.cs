using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "F-18")]
[Trait("Feature", "registration")]
public sealed class PublishedInvokePackagePrivateTests
{
    [Theory]
    [InlineData("matching")]
    [InlineData("identity")]
    [InlineData("contents")]
    public async Task PublishChecksPrivateDependenciesThroughPackageDtoAssemblies(string scenario)
    {
        var references = WorkflowCompilation.ReferencePaths().Select(path => MetadataReference.CreateFromFile(path)).ToArray();

        var payload = Compile("Invoke.PackagePayload", """
                                                       namespace InvokePackage;
                                                       public sealed class Payload
                                                       {
                                                           public IxIFlow.BundleFixtures.FirstData Details { get; set; } = new();
                                                       }
                                                       """, references);

        var data = Compile("Invoke.PackageData", """
                                                 namespace InvokePackage;
                                                 public sealed class Data
                                                 {
                                                     [IxIFlow.ActivitySdk.WorkflowInputOutput]
                                                     public Payload Value { get; set; } = new();
                                                 }
                                                 """, references.Append(MetadataReference.CreateFromImage(payload.Reference)));

        var package = ActivityPackageFixture.Package("fixture.invoke-private", "1.0.0", new()
        {
            ["ref/net10.0/Invoke.PackageData.dll"] = data.Reference,
            ["lib/net10.0/Invoke.PackageData.dll"] = data.Runtime,
            ["ref/net10.0/Invoke.PackagePayload.dll"] = payload.Reference,
            ["lib/net10.0/Invoke.PackagePayload.dll"] = payload.Runtime
        });

        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        await publisher.UploadPackageAsync(new MemoryStream(package));

        var childDraft = await store.SaveDefinitionAsync(new("Original", 1, "workflow", """
                                                                                        workflow: {name: Original, dataType: InvokePackage.Data}
                                                                                        imports: {catalogs: [fixture.invoke-private@1.0.0]}
                                                                                        definitions: []
                                                                                        """), null);

        var child = await publisher.PublishAsync(childDraft.Id);
        Assert.True(child.Success, string.Join("; ", child.Diagnostics));
        await using var stream = await store.OpenAsync(child.Workflow!.ArtifactDigest!.Value);
        var bundle = await WorkflowBundleArchive.ReadAsync(stream!, child.Workflow.ArtifactDigest.Value);
        var selected = typeof(FirstData).Assembly.GetName();
        var entries = bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var privatePath = "private/" + selected.Name + ".dll";
        Assert.True(entries.ContainsKey(privatePath));

        // Both saved contract roots are package-owned; the private type is nested inside Payload.
        Assert.All(WorkflowContract.Parse(child.Workflow.ContractJson).Inputs.Concat(
            WorkflowContract.Parse(child.Workflow.ContractJson).Outputs), member =>
            Assert.Contains("Invoke.PackagePayload", member.Type));

        if (scenario != "matching")
            entries[privatePath] = Compile(selected.Name!, $$"""
                                                             [assembly: System.Reflection.AssemblyVersion("{{(scenario == "identity" ? "99.0.0.0" : selected.Version!.ToString())}}")]
                                                             namespace IxIFlow.BundleFixtures;
                                                             public sealed class FirstData
                                                             {
                                                                 public int Value { get; set; }
                                                                 public string Result { get; set; } = "";
                                                             }
                                                             """, references).Runtime;

        var archive = WorkflowBundleArchive.Create(bundle.Manifest with { Name = "Callee" }, entries);
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(archive));
        var saved = await WorkflowBundleArchive.ReadAsync(new MemoryStream(archive), digest);

        await store.RegisterWorkflowVersionAsync(child.Workflow with
        {
            Name = "Callee", ArtifactDigest = digest, PublishedDraft = null,
            ManifestJson = JsonSerializer.Serialize(saved.Manifest)
        });

        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", """
                                                                                 workflow: {name: Caller, dataType: InvokePackage.Data}
                                                                                 definitions:
                                                                                   - kind: invokeWorkflow
                                                                                     workflow: {name: Callee, version: 1}
                                                                                 """), null);

        var result = await publisher.PublishAsync(draft.Id);

        if (scenario == "matching")
        {
            Assert.True(result.Success, string.Join("; ", result.Diagnostics));

            return;
        }

        Assert.False(result.Success);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("PUB010", error.Code);
        Assert.Equal(WorkflowPublishStage.Validate, error.Stage);
        Assert.Equal(4, error.Line);
        Assert.Contains(selected.Name!, error.Message);
        Assert.Contains(scenario == "identity" ? "conflicting identity" : "conflicting contents", error.Message);
        Assert.Null(result.Workflow);
        Assert.Null(await store.GetWorkflowVersionAsync("Caller", 1));
        Assert.Equal(draft, await store.GetDefinitionAsync("workflow", "Caller", 1));
    }

    private static (byte[] Runtime, byte[] Reference) Compile(string name, string source, IEnumerable<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var runtime = new MemoryStream();
        var emitted = compilation.Emit(runtime);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
        using var reference = new MemoryStream();
        emitted = compilation.Emit(reference, options: new EmitOptions(metadataOnly: true, includePrivateMembers: false));
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));

        return (runtime.ToArray(), reference.ToArray());
    }
}
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Authoring;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "F-18")]
[Trait("Feature", "registration")]
public sealed class PublishedInvokeReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAcceptsMatchingPrivateDtoAssembliesAndDependencies(bool dependency)
    {
        var store = new InMemoryWorkflowStateRepository();
        var options = new WorkflowPublisherOptions();
        options.SourceOptions.Types["Data"] = "global::" + (dependency ? typeof(PrivateInvokeData).FullName : typeof(FirstData).FullName);
        var publisher = new WorkflowPublisher(store, store, options);
        var callee = await store.SaveDefinitionAsync(new("Callee", 1, "workflow",
            "workflow: {name: Callee, dataType: Data, input: [], output: []}\ndefinitions: []"), null);
        var child = await publisher.PublishAsync(callee.Id);
        Assert.True(child.Success, string.Join("; ", child.Diagnostics));
        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow",
            "workflow: {name: Caller, dataType: Data}\ndefinitions:\n  - kind: invokeWorkflow\n    workflow: {name: Callee, version: 1}"), null);
        var result = await publisher.PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublishRefusesPrivateDtoAssemblyConflicts(bool dependency, bool differentVersion)
    {
        var store = new InMemoryWorkflowStateRepository();
        var options = new WorkflowPublisherOptions();
        options.SourceOptions.Types["Data"] = "global::" + (dependency ? typeof(PrivateInvokeData).FullName : typeof(FirstData).FullName);
        var publisher = new WorkflowPublisher(store, store, options);
        var childDraft = await store.SaveDefinitionAsync(new("Original", 1, "workflow",
            "workflow: {name: Original, dataType: Data, input: [], output: []}\ndefinitions: []"), null);
        var child = await publisher.PublishAsync(childDraft.Id);
        Assert.True(child.Success, string.Join("; ", child.Diagnostics));
        await using var stream = await store.OpenAsync(child.Version!.ArtifactDigest!.Value);
        var bundle = await WorkflowBundleArchive.ReadAsync(stream!, child.Version.ArtifactDigest!.Value);
        var selected = typeof(FirstData).Assembly.GetName();
        var source = $$"""
            [assembly: System.Reflection.AssemblyVersion("{{(differentVersion ? "99.0.0.0" : selected.Version!.ToString())}}")]
            namespace IxIFlow.BundleFixtures;
            public sealed class FirstData
            {
                public int Value { get; set; }
                public string Result { get; set; } = "";
            }
            """;
        var compilation = CSharpCompilation.Create(selected.Name, [CSharpSyntaxTree.ParseText(source)],
            WorkflowCompilation.ReferencePaths().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var assembly = new MemoryStream();
        var emitted = compilation.Emit(assembly);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
        var entries = bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var path = "private/" + selected.Name + ".dll";
        Assert.True(entries.ContainsKey(path));
        entries[path] = assembly.ToArray();
        var archive = WorkflowBundleArchive.Create(bundle.Manifest with { Name = "Callee" }, entries);
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(archive));
        var saved = await WorkflowBundleArchive.ReadAsync(new MemoryStream(archive), digest);
        await store.RegisterWorkflowVersionAsync(child.Version with
        {
            Name = "Callee", ArtifactDigest = digest, PublishedDraft = null,
            ManifestJson = JsonSerializer.Serialize(saved.Manifest)
        });
        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", """
            workflow: {name: Caller, dataType: Data}
            definitions:
              - kind: invokeWorkflow
                workflow: {name: Callee, version: 1}
            """), null);
        var result = await publisher.PublishAsync(draft.Id);
        Assert.False(result.Success);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("PUB010", error.Code);
        Assert.Equal(WorkflowPublishStage.Validate, error.Stage);
        Assert.Equal(4, error.Line);
        Assert.True(error.Message.Contains(selected.Name!, StringComparison.Ordinal), error.Message);
        Assert.Null(result.Version);
        Assert.Null(await store.GetWorkflowVersionAsync("Caller", 1));
        Assert.Equal(draft, await store.GetDefinitionAsync("workflow", "Caller", 1));
    }

    [Theory]
    [InlineData(false, "Value", "WF006")]
    [InlineData(false, "Missing", "WF005")]
    [InlineData(true, "Value", "WF006")]
    [InlineData(true, "Missing", "WF005")]
    public async Task PublishDiagnosesConstructedGenericDtoDeclarations(bool nested, string member, string code)
    {
        var store = new InMemoryWorkflowStateRepository();
        var options = GenericOptions(nested);
        var draft = await store.SaveDefinitionAsync(new("Generic", 1, "workflow",
            $"workflow: {{name: Generic, dataType: Data, input: [{member}]}}\ndefinitions: []"), null);
        var result = await new WorkflowPublisher(store, store, options).PublishAsync(draft.Id);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(code, error.Code);
        Assert.Equal(WorkflowPublishStage.Validate, error.Stage);
        Assert.Equal(1, error.Line);
        Assert.True(error.Column > 1);
        Assert.Null(result.Version);
        Assert.Null(await store.GetWorkflowVersionAsync("Generic", 1));
        Assert.Equal(draft, await store.GetDefinitionAsync("workflow", "Generic", 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAcceptsConstructedGenericDtoOutputDeclarations(bool nested)
    {
        var store = new InMemoryWorkflowStateRepository();
        var draft = await store.SaveDefinitionAsync(new("Generic", 1, "workflow",
            "workflow: {name: Generic, dataType: Data, output: [Value]}\ndefinitions: []"), null);
        var result = await new WorkflowPublisher(store, store, GenericOptions(nested)).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(typeof(string), IxIFlow.Core.Runtime.WorkflowTypeIdentity.Resolve(
            Assert.Single(WorkflowContract.Parse(result.Version!.ContractJson).Outputs).Type));
    }

    private static WorkflowPublisherOptions GenericOptions(bool nested)
    {
        var options = new WorkflowPublisherOptions();
        options.SourceOptions.Types["Data"] = nested
            ? "global::IxIFlow.Tests.ExecutionTests.InvokeGenericContainer<int>.Data<string>"
            : "global::IxIFlow.Tests.ExecutionTests.InvokeGenericData<string>";
        return options;
    }
}

public sealed class PrivateInvokeData
{
    public FirstData Payload { get; set; } = new();
}

public sealed class InvokeGenericData<T>
{
    [WorkflowOutput] public T Value { get; set; } = default!;
}

public sealed class InvokeGenericContainer<T>
{
    public sealed class Data<U>
    {
        [WorkflowOutput] public U Value { get; set; } = default!;
    }
}

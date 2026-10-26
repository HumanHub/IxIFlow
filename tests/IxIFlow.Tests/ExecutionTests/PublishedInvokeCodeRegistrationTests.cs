using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "F-18")]
[Trait("Feature", "registration")]
public sealed class PublishedInvokeCodeRegistrationTests
{
    [Theory]
    [InlineData(false, "matching")]
    [InlineData(false, "identity")]
    [InlineData(false, "contents")]
    [InlineData(true, "matching")]
    [InlineData(true, "identity")]
    [InlineData(true, "contents")]
    public async Task PublishChecksExecutableCodeCalleeAssemblies(bool dependency, string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.CodeInvoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await CheckScenario(dependency, scenario, root); }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            // Runtime contract caches can retain the loaded DLL mappings until the test process exits.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task CheckScenario(bool dependency, string scenario, string root)
    {
        var references = WorkflowCompilation.ReferencePaths().Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        string Payload(string version, string code) => $$"""
            [assembly: System.Reflection.AssemblyVersion("{{version}}")]
            namespace Registered;
            public sealed class Payload { public {{code}} Code { get; set; } }
            """;
        var dataSource = $$"""
            namespace Registered;
            {{(dependency ? "public sealed class Wrapper { public Payload Details { get; set; } = new(); }" : "")}}
            public sealed class Data
            {
                [IxIFlow.ActivitySdk.WorkflowInputOutput]
                public {{(dependency ? "Wrapper" : "Payload")}} Value { get; set; } = new();
            }
            """;
        var name = dependency ? "Invoke.RegisteredPayload" : "Invoke.RegisteredData";
        var original = Compile(name, Payload("1.0.0.0", "int") + (dependency ? "" : dataSource.Replace("namespace Registered;", "")), references);
        var selected = scenario == "matching" ? original : Compile(name,
            Payload(scenario == "identity" ? "99.0.0.0" : "1.0.0.0", "System.Guid") +
            (dependency ? "" : dataSource.Replace("namespace Registered;", "")), references);
        var data = dependency ? Compile("Invoke.RegisteredData", dataSource,
            references.Append(MetadataReference.CreateFromImage(original.Reference))) : original;
        var context = new CodeContext(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, name + ".dll"), original.Runtime);
            if (dependency) await File.WriteAllBytesAsync(Path.Combine(root, "Invoke.RegisteredData.dll"), data.Runtime);
            var type = context.LoadFromAssemblyPath(Path.Combine(root, "Invoke.RegisteredData.dll")).GetType("Registered.Data")!;
            var store = new InMemoryWorkflowStateRepository();
            using var catalog = new WorkflowCatalog(store);
            await catalog.RegisterWorkflowAsync(new() { Name = "Callee", Version = 1, WorkflowDataType = type });
            if (dependency)
                Assert.All(WorkflowContract.Parse((await store.GetWorkflowVersionAsync("Callee", 1))!.ContractJson).Inputs,
                    member => Assert.DoesNotContain("Invoke.RegisteredPayload", member.Type));
            var packageEntries = new Dictionary<string, byte[]>
            {
                ["ref/net10.0/" + name + ".dll"] = selected.Reference,
                ["lib/net10.0/" + name + ".dll"] = selected.Runtime
            };
            if (dependency)
            {
                packageEntries["ref/net10.0/Invoke.RegisteredData.dll"] = data.Reference;
                packageEntries["lib/net10.0/Invoke.RegisteredData.dll"] = data.Runtime;
            }
            var publisher = new WorkflowPublisher(catalog, store);
            await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.code-invoke", "1.0.0", packageEntries)));
            var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", """
                workflow: {name: Caller, dataType: Registered.Data}
                imports: {catalogs: [fixture.code-invoke@1.0.0]}
                definitions:
                  - kind: invokeWorkflow
                    workflow: {name: Callee, version: 1}
                    input: [{target: Value, from: {kind: path, path: workflow.Value}}]
                    output: [{source: Value, to: {path: workflow.Value}}]
                """), null);
            var result = await publisher.PublishAsync(draft.Id);
            if (scenario == "matching")
            {
                Assert.True(result.Success, string.Join("; ", result.Diagnostics));
                Assert.Equal(new WorkflowVersionDependency("Callee", 1, WorkflowDependencyKind.Child), Assert.Single(result.Workflow!.Dependencies!));
            }
            else
            {
                Assert.False(result.Success);
                var error = Assert.Single(result.Diagnostics);
                Assert.Equal("PUB010", error.Code);
                Assert.Equal(WorkflowPublishStage.Validate, error.Stage);
                Assert.Equal(5, error.Line);
                Assert.Contains(name, error.Message);
                Assert.Contains(scenario == "identity" ? "conflicting identity" : "conflicting contents", error.Message);
                Assert.Null(result.Workflow);
                Assert.Null(await store.GetWorkflowVersionAsync("Caller", 1));
                Assert.Equal(draft, await store.GetDefinitionAsync("workflow", "Caller", 1));
            }
        }
        finally
        {
            context.Unload();
        }
    }

    private sealed class CodeContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
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

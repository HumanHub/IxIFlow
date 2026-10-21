using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Generators;
using IxIFlow.Builders.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Generators.Tests.StructureHashFixtures;

[Trait("Spec", "F-03")]
public sealed class YamlStructureHashTests
{
    [Theory]
    [InlineData("[Workflow(\"Generated child\", Version = 7)]", true)]
    [InlineData("", false)]
    [InlineData("[Workflow(\"\", Version = 7)]", false)]
    [InlineData("[Workflow(\"Generated child\", Version = 0)]", false)]
    [InlineData("[Workflow(\"Generated child\", Version = -1)]", false)]
    public void GeneratorReadsAndValidatesTheClassAttribute(string attribute, bool valid)
    {
        var compilation = GeneratorRegressionTests.Compilation("""
            using IxIFlow.Builders.Interfaces;
            using IxIFlow.Core;
            using IxIFlow.Generators.Tests.StructureHashFixtures;
            ATTRIBUTE
            public sealed class GeneratedChild : IWorkflow<StructureHashData>
            {
                public GeneratedChild() => throw new System.InvalidOperationException("Must not construct");
                public void Build(IWorkflowBuilder<StructureHashData> builder) => throw new System.InvalidOperationException();
            }
            """.Replace("ATTRIBUTE", attribute));
        const string yaml = """
            workflow: {name: Parent, dataType: IxIFlow.Generators.Tests.StructureHashFixtures.StructureHashData}
            definitions:
              - kind: invokeWorkflowType
                workflowType: GeneratedChild
            """;
        var driver = WorkflowGeneratorHarness.Driver(yaml, "attribute.yaml")
            .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
        if (valid)
        {
            Assert.Empty(diagnostics);
            Assert.Empty(generated.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
            Assert.Contains("Invoke<global::GeneratedChild", Assert.Single(driver.GetRunResult().GeneratedTrees).ToString());
        }
        else
        {
            var error = Assert.Single(diagnostics);
            Assert.Equal("IXIYAML001", error.Id);
            Assert.Contains("requires [Workflow", error.GetMessage());
            Assert.Equal("attribute.yaml", error.Location.GetLineSpan().Path);
        }
    }

    [Fact]
    public void CanonicalYamlAndFluentTwinHaveEqualHashes()
    {
        using var first = Yaml(YamlSource);
        using var second = Yaml("""
            # Same document with different presentation and key order.
            definitions:
              - activity: IxIFlow.Generators.Tests.StructureHashFixtures.StructureHashMultiply
                name: A new step label
                id: renamed
                input:
                  - from: { value: 5, kind: constant }
                    target: Value
                kind: activity
            workflow: { description: 'Reworded', displayName: 'New label', dataType: IxIFlow.Generators.Tests.StructureHashFixtures.StructureHashData, version: 3, name: Canonical }
            """
        );
        using var changed = Yaml(YamlSource.Replace("value: 5", "value: 6"));
        var fluent = Workflow.Create<StructureHashData>("Canonical", 3).Step<StructureHashMultiply>(step => step
            .Input(activity => activity.Value).From(ctx => 5)).Build();
        Assert.Equal(Hash(first.Definition), Hash(second.Definition));
        Assert.Equal(Hash(fluent), Hash(first.Definition));
        Assert.NotEqual(Hash(first.Definition), Hash(changed.Definition));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NamesAreRequiredInBothAuthoringRoutes(string name)
    {
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<StructureHashData>(name).Build());
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("name is required", error.Message);
        Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(
            YamlSource.Replace("name: Canonical", "name: '" + name + "'"), "required.yaml"));
    }

    [Fact]
    public void TypedYamlAndFluentChildrenHaveEqualHashes()
    {
        using var yaml = Yaml("""
            workflow: {name: Parent, version: 1, dataType: IxIFlow.Generators.Tests.StructureHashFixtures.StructureHashData}
            definitions:
              - kind: activity
                activity: IxIFlow.Generators.Tests.StructureHashFixtures.StructureHashMultiply
              - kind: invokeWorkflowType
                workflowType: IxIFlow.Generators.Tests.StructureHashFixtures.YamlStructureHashTests.TypedChild
                dataType: IxIFlow.Generators.Tests.StructureHashFixtures.StructureHashData
            """);
        var fluent = Workflow.Create<StructureHashData>("Parent", 1).Step<StructureHashMultiply>()
            .Invoke<TypedChild, StructureHashData>().Build();
        Assert.Equal(Hash(fluent), Hash(yaml.Definition));
    }

    [IxIFlow.Core.Workflow("Pinned child", Version = 3)]
    public sealed class TypedChild : IWorkflow<StructureHashData>
    {
        public void Build(IWorkflowBuilder<StructureHashData> builder) => builder.Step<StructureHashMultiply>();
    }

    private static string Hash(WorkflowDefinition definition) => new WorkflowScopeCatalog(definition).StructureHash;
    private static Compiled Yaml(string yaml) => new(WorkflowSourceTranslator.Translate(yaml, "canonical.yaml"));

    private sealed class Compiled : IDisposable
    {
        private readonly AssemblyLoadContext _context = new("structure-hash", isCollectible: true);
        public WorkflowDefinition Definition { get; }

        public Compiled(string source, OptimizationLevel optimization = OptimizationLevel.Release,
            LanguageVersion language = LanguageVersion.CSharp14)
        {
            var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Concat([typeof(WorkflowDefinition).Assembly.Location, typeof(IAsyncActivity).Assembly.Location,
                    typeof(StructureHashData).Assembly.Location]).Distinct(StringComparer.OrdinalIgnoreCase);
            var compilation = CSharpCompilation.Create("StructureHashFixture",
                [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(language))],
                paths.Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: optimization));
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            stream.Position = 0;
            var assembly = _context.LoadFromStream(stream);
            Definition = (WorkflowDefinition)assembly.GetTypes().Single(type => type.GetMethod("Build") != null)
                .GetMethod("Build")!.Invoke(null, null)!;
        }

        public void Dispose() => _context.Unload();
    }

    private const string YamlSource = """
        workflow:
          name: Canonical
          version: 3
          description: Original
          dataType: IxIFlow.Generators.Tests.StructureHashFixtures.StructureHashData
        definitions:
          - kind: activity
            activity: IxIFlow.Generators.Tests.StructureHashFixtures.StructureHashMultiply
            input:
              - target: Value
                from: { kind: constant, value: 5 }
        """;

}

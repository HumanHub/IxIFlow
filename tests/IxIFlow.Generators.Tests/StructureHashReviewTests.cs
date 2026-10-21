using System.Runtime.Loader;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.CodeAnalysis;

namespace IxIFlow.Generators.Tests.StructureHashFixtures;

[Trait("Spec", "F-03")]
public sealed class StructureHashReviewTests
{
    [Fact]
    public void GeneratorDefaultsTheWorkflowAttributeVersionToOne()
    {
        var compilation = GeneratorRegressionTests.Compilation("""
            using IxIFlow.Builders.Interfaces;
            using IxIFlow.Core;
            using IxIFlow.Generators.Tests.StructureHashFixtures;
            [Workflow("x")]
            public sealed class DefaultVersionChild : IWorkflow<StructureHashData>
            {
                public DefaultVersionChild() => throw new System.InvalidOperationException("Must not construct");
                public void Build(IWorkflowBuilder<StructureHashData> builder) => throw new System.InvalidOperationException();
            }
            """);
        var driver = WorkflowGeneratorHarness.Driver("""
            workflow: {name: Parent, dataType: IxIFlow.Generators.Tests.StructureHashFixtures.StructureHashData}
            definitions:
              - kind: invokeWorkflowType
                workflowType: DefaultVersionChild
            """, "default-version.yaml")
            .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Empty(generated.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        Assert.Contains("Invoke<global::DefaultVersionChild", Assert.Single(driver.GetRunResult().GeneratedTrees).ToString());
        using var compiled = new Compiled(generated);
        var child = Assert.Single(compiled.Definition.Steps);
        Assert.Equal("x", child.WorkflowName);
        Assert.Equal(1, child.WorkflowVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrderedYamlMappingsMatchFluentAndReorderingChangesTheHash(bool reverse)
    {
        var inputs = reverse
            ? "[{target: Value, from: {kind: constant, value: 2}}, {target: Value, from: {kind: constant, value: 1}}]"
            : "[{target: Value, from: {kind: constant, value: 1}}, {target: Value, from: {kind: constant, value: 2}}]";
        var outputs = reverse
            ? "[{source: Result, to: {path: workflow.Result}}, {source: Result, to: {path: workflow.Value}}]"
            : "[{source: Result, to: {path: workflow.Value}}, {source: Result, to: {path: workflow.Result}}]";
        var yaml = $$"""
            workflow: {name: Ordered, dataType: IxIFlow.Generators.Tests.StructureHashFixtures.StructureHashData}
            definitions:
              - kind: activity
                activity: IxIFlow.Generators.Tests.StructureHashFixtures.StructureHashMultiply
                input: {{inputs}}
                output: {{outputs}}
            """;
        var generated = WorkflowSourceTranslator.Translate(yaml, "ordered.yaml");
        using var compiled = new Compiled(GeneratorRegressionTests.Compilation(generated));
        var fluent = Workflow.Create<StructureHashData>("Ordered")
            .Step<StructureHashMultiply>(step => step
                .Input(activity => activity.Value).From(ctx => 1)
                .Input(activity => activity.Value).From(ctx => 2)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Value)
                .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Result)).Build();
        var original = Hash(fluent);
        if (reverse)
        {
            fluent.Steps[0].InputMappings.Reverse();
            fluent.Steps[0].OutputMappings.Reverse();
            Assert.NotEqual(original, Hash(compiled.Definition));
        }
        Assert.Equal(Hash(fluent), Hash(compiled.Definition));
    }

    private static string Hash(WorkflowDefinition definition) => new WorkflowScopeCatalog(definition).StructureHash;

    private sealed class Compiled : IDisposable
    {
        private readonly AssemblyLoadContext _context = new("review-yaml", isCollectible: true);
        public WorkflowDefinition Definition { get; }
        public Compiled(Compilation compilation)
        {
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            stream.Position = 0;
            var assembly = _context.LoadFromStream(stream);
            Definition = (WorkflowDefinition)assembly.GetTypes()
                .Single(type => type.GetMethod("Build")?.IsStatic == true)
                .GetMethod("Build")!.Invoke(null, null)!;
        }
        public void Dispose() => _context.Unload();
    }
}

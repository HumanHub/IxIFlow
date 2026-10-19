using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using YamlDotNet.Core;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-05")]
public sealed class GeneratorReviewTests
{
    private const string Header = "workflow:\n  name: Test\n  dataType: IxIFlow.Generators.Tests.FlowData\ndefinitions:\n";
    private const string Activity = "  - kind: activity\n    id: multiply\n    activity: IxIFlow.Generators.Tests.Multiply\n    input:\n      - target: Value\n        from: {kind: constant, value: 1}\n";

    [Theory]
    [InlineData("# only a comment", 1, 1)]
    [InlineData("\n\nnull", 3, 1)]
    [InlineData("\n\n~", 3, 1)]
    [InlineData("\n\n[]", 3, 1)]
    [InlineData("\n\nscalar", 3, 1)]
    [InlineData("workflow:\n  name: Test\n  Name: Other", 3, 3)]
    [InlineData("workflow: {name: Test}\ndefinitions:\n  - kind: unknown", 3, 11)]
    [InlineData("workflow: {name: Test}\ndefinitions:\n  - kind: activity\n    input: wrong", 4, 12)]
    [InlineData("workflow: {name: Test}\nactivities:\n  key: {}\n  Key: {}", 4, 3)]
    public void SerializerFailuresArePositionedAtBothEntryPoints(string yaml, int line, int column)
    {
        var parsed = Assert.Throws<YamlException>(() => WorkflowYamlSerializer.Read(yaml));
        Assert.Equal(line, parsed.Start.Line);
        Assert.Equal(column, parsed.Start.Column);
        var translated = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "invalid.yaml"));
        Assert.Equal(line, translated.Line);
        Assert.Equal(column, translated.Column);
        var diagnostic = Assert.Single(WorkflowGeneratorHarness.Generate(yaml).Diagnostics);
        Assert.Equal("IXIYAML001", diagnostic.Id);
        Assert.Equal(new LinePosition(line - 1, column - 1), diagnostic.Location.GetLineSpan().StartLinePosition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeclaredActivityVersionIsEnforcedForStepsAndTemplates(bool template)
    {
        var yaml = template ? Header + "  - {kind: activityRef, ref: reusable}\nactivities:\n  reusable:\n    activity: IxIFlow.Generators.Tests.Multiply\n    activityVersion: '2.0'\n    defaults: {Value: 1}\n"
            : Header + Activity + "    activityVersion: '2.0'\n";
        var options = new WorkflowSourceOptions();
        options.Activities[typeof(Multiply).FullName!] = new(typeof(Multiply).FullName!, "1.0");
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "invalid.yaml", options));
        Assert.Contains("requires version '1.0'", error.Message);
        var diagnostic = Assert.Single(WorkflowGeneratorHarness.Generate(yaml).Diagnostics);
        Assert.Contains("declares '2.0'", diagnostic.GetMessage());
        Assert.Equal(error.Line - 1, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Empty(WorkflowGeneratorHarness.Generate(yaml.Replace("'2.0'", "'1.0'")).Diagnostics);
    }

    [Fact]
    public void GeneratorRunsDocumentRulesAtTheYamlMember()
    {
        var yaml = Header + Activity + "    output:\n      - source: Result\n        to: {path: previousStep.Value}\n";
        var diagnostic = Assert.Single(WorkflowGeneratorHarness.Generate(yaml).Diagnostics);
        Assert.Contains("Output mapping target path", diagnostic.GetMessage());
        Assert.Equal(12, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Theory]
    [InlineData("catalogs")]
    [InlineData("workflows")]
    public void UnresolvedImportsRequirePublishing(string kind)
    {
        var yaml = Header + Activity + "imports:\n  " + kind + ": [example@1.0]\n";
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "invalid.yaml"));
        Assert.Contains("requires publishing", error.Message);
        Assert.True(error.Line > 1);
        Assert.Contains("requires publishing", Assert.Single(WorkflowGeneratorHarness.Generate(yaml).Diagnostics).GetMessage());
    }

    [Fact]
    public void DisplayNameIsPreservedAsMetadataWithoutChangingIdentity()
    {
        var definition = GeneratorRegressionTests.Build((Header + Activity).Replace("  name: Test", "  name: Test\n  displayName: Customer orders"));
        Assert.Equal("Test", definition.Name);
        Assert.Equal("Customer orders", definition.Metadata["DisplayName"]);
    }

    [Theory]
    [InlineData("int", "wrong", "does not match")]
    [InlineData("bool", "3", "does not match")]
    [InlineData("Some.Published.Type", "3", "requires publishing")]
    public void TemplateParameterTypesCannotBeIgnored(string type, string value, string expected)
    {
        var yaml = Header + "  - {kind: stepRef, ref: block}\nsteps:\n  block:\n    parameters:\n      count: {type: " + type + ", default: " + value + "}\n    steps: []\n";
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "invalid.yaml"));
        Assert.Contains(expected, error.Message);
        Assert.True(error.Line > 1);
        Assert.Contains(expected, Assert.Single(WorkflowGeneratorHarness.Generate(yaml).Diagnostics).GetMessage());
    }

    [Fact]
    public void SourceActivityAliasesUseTheAttributeSymbolAndObserveChanges()
    {
        const string source = """
            namespace Local;
            [global::IxIFlow.ActivitySdk.WorkflowActivity("local.multiply", Version = "1.0")]
            public sealed class Multiply : global::IxIFlow.Core.IAsyncActivity
            {
                public int Value { get; set; }
                public global::System.Threading.Tasks.Task ExecuteAsync(global::IxIFlow.Core.IActivityContext context,
                    global::System.Threading.CancellationToken cancellationToken = default) => global::System.Threading.Tasks.Task.CompletedTask;
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = GeneratorRegressionTests.Compilation().AddSyntaxTrees(tree);
        var yaml = Header + Activity.Replace(typeof(Multiply).FullName!, "local.multiply");
        var driver = WorkflowGeneratorHarness.Driver(yaml).RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        Assert.Contains("global::Local.Multiply", Assert.Single(driver.GetRunResult().GeneratedTrees).ToString());
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(source.Replace("local.multiply", "local.changed"))));
        Assert.Contains(driver.GetRunResult().Diagnostics, item => item.GetMessage().Contains("Unknown activity"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdersInTwoAssembliesUseDistinctNamespaces(bool rootNamespace)
    {
        CSharpCompilation Build(string name, MetadataReference? reference = null)
        {
            var compilation = GeneratorRegressionTests.Compilation().WithAssemblyName(name);
            if (reference != null) compilation = compilation.AddReferences(reference);
            var space = rootNamespace ? name + ".Root" : name;
            var driver = WorkflowGeneratorHarness.Driver(Header + Activity, "Orders.workflow.yaml", rootNamespace ? space : null)
                .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
            Assert.Empty(diagnostics);
            Assert.Contains("namespace " + space + ".Generated;", Assert.Single(driver.GetRunResult().GeneratedTrees).ToString());
            return (CSharpCompilation)generated;
        }
        var library = Build("OrdersLibrary");
        using var stream = new MemoryStream();
        Assert.True(library.Emit(stream).Success);
        var app = Build("OrdersApp", MetadataReference.CreateFromImage(stream.ToArray()));
        var space = rootNamespace ? "OrdersApp.Root" : "OrdersApp";
        app = app.AddSyntaxTrees(CSharpSyntaxTree.ParseText("public static class UseOrders { public static object Build() => " + space + ".Generated.Workflow_Orders_workflow.Build(); }"));
        Assert.Empty(app.GetDiagnostics().Where(item => item.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning));
    }
}

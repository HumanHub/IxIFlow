using System.Text.Json;
using IxIFlow.Dsl.Documents;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using YamlDotNet.Core;

namespace IxIFlow.Generators.Tests;

public sealed class StrictDocumentReaderTests
{
    private const string Header = "workflow:\n  name: Strict\n  dataType: IxIFlow.Generators.Tests.FlowData\ndefinitions:\n";
    private const string Activity = "  - kind: activity\n    activity: IxIFlow.Generators.Tests.Multiply\n";

    [Theory]
    [InlineData(Activity + "    ouptut: []\n", 7, 5, "Unknown member 'ouptut'")]
    [InlineData(Activity + "    Output: []\n", 7, 5, "Unknown member 'Output'")]
    [InlineData(Activity + "    input:\n      - target: Value\n        from: {path: workflow.Value, extra: 1}\n", 9, 38, "Unknown member 'extra'")]
    [InlineData(Activity + "    output:\n      - source: Result\n        to: {kind: constant, path: workflow.Result}\n", 9, 20, "Expected workflow document kind 'path'")]
    [InlineData(Activity + "definition: []\n", 7, 1, "Unknown member 'definition'")]
    [InlineData(Activity + "location: {line: 1, column: 1}\n", 7, 1, "Unknown member 'location'")]
    public void UnknownMembersAndMismatchedKindsArePositioned(string body, int line, int column, string message)
    {
        var yaml = Header + body;
        var parsed = Assert.Throws<YamlException>(() => WorkflowYamlSerializer.Read(yaml));
        Assert.Contains(message, parsed.Message);
        Assert.Equal((line, column), ((int)parsed.Start.Line, (int)parsed.Start.Column));
        var diagnostic = Assert.Single(Generate(yaml).Diagnostics);
        Assert.Equal("IXIYAML001", diagnostic.Id);
        Assert.Equal(new LinePosition(line - 1, column - 1), diagnostic.Location.GetLineSpan().StartLinePosition);
    }

    [Fact]
    public void MemberNamesAreCamelCase()
    {
        var error = Assert.Throws<YamlException>(() => WorkflowYamlSerializer.Read("Workflow: {name: Strict, dataType: Data}\n"));
        Assert.Contains("Unknown member 'Workflow'", error.Message);
        Assert.Contains("accepts schemaVersion, workflow, imports", error.Message);
    }

    [Fact]
    public void AFixedPathAcceptsItsOwnKind()
    {
        var document = WorkflowYamlSerializer.Read(Header + Activity +
            "    output:\n      - source: Result\n        to: {kind: path, path: workflow.Result}\n");
        Assert.Equal("workflow.Result", Assert.Single(Assert.IsType<ActivityStepDocument>(document.Definitions[0]).Output).To.Path);
    }

    [Theory]
    [InlineData("""{"workflow":{"name":"Strict","dataType":"Data"},"definition":[]}""")]
    [InlineData("""{"Workflow":{"name":"Strict","dataType":"Data"}}""")]
    [InlineData("""{"workflow":{"name":"Strict","dataType":"Data"},"location":{"line":1,"column":1}}""")]
    [InlineData("""{"definitions":[{"kind":"activity","activity":"Multiply","ouptut":[]}]}""")]
    [InlineData("""{"definitions":[{"kind":"activity","activity":"Multiply","output":[{"source":"Result","to":{"kind":"constant","path":"workflow.Result"}}]}]}""")]
    [InlineData("""{"definitions":[{"kind":"if","condition":{"path":"workflow.Value","extra":1}}]}""")]
    public void JsonRejectsWhatYamlRejects(string json) =>
        Assert.ThrowsAny<JsonException>(() => WorkflowJsonSerializer.Read(json));

    [Fact]
    public void JsonWritesAndChecksTheDeclaredKind()
    {
        var document = new WorkflowDocument { Workflow = new() { Name = "Strict", DataType = "Data" } };
        document.Definitions.Add(new ActivityStepDocument
        {
            Activity = "Multiply",
            Output = [new() { Source = "Result", To = new() { Path = "workflow.Result" } }]
        });
        var json = WorkflowJsonSerializer.Write(document);
        Assert.Contains("\"kind\":\"activity\"", json);
        Assert.Contains("\"to\":{\"kind\":\"path\"", json);
        var step = Assert.IsType<ActivityStepDocument>(Assert.Single(WorkflowJsonSerializer.Read(json).Definitions));
        Assert.Equal("activity", step.Kind);
        Assert.Equal("workflow.Result", Assert.Single(step.Output).To.Path);
    }

    private static GeneratorDriverRunResult Generate(string yaml) =>
        CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()], [new YamlFile("strict.yaml", yaml)],
                optionsProvider: new WorkflowFileOptionsProvider("strict.yaml"))
            .RunGenerators(GeneratorRegressionTests.Compilation()).GetRunResult();

    private sealed class YamlFile(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }
}

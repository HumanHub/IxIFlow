using System.Text.Json;
using IxIFlow.Dsl.Documents;
using Microsoft.CodeAnalysis.Text;
using YamlDotNet.Core;

namespace IxIFlow.Generators.Tests;

public sealed class StrictDocumentReaderTests
{
    private const string Header = "workflow:\n  name: Strict\n  dataType: IxIFlow.Generators.Tests.FlowData\n";
    private const string Activity = "definitions:\n  - kind: activity\n    activity: IxIFlow.Generators.Tests.Multiply\n";
    private const string Loop = "definitions:\n  - kind: forEach\n    items: {path: workflow.Values}\n";

    // The error is expected at the first occurrence of the marker.
    [Theory]
    [InlineData(Activity + "    ouptut: []\n", "ouptut", "Unknown member 'ouptut'")]
    [InlineData(Activity + "    Output: []\n", "Output", "Unknown member 'Output'")]
    [InlineData(Activity + "    input:\n      - target: Value\n        from: {path: workflow.Value, bogus: 1}\n", "bogus", "Unknown member 'bogus'")]
    [InlineData(Activity + "    input:\n      - target: Value\n        from: {kind: constant, value: 1, bogus: 1}\n", "bogus", "ConstantExpressionDocument accepts kind, value")]
    [InlineData(Activity + "    input:\n      - target: Value\n        bogus: 1\n        from: {kind: constant, value: 1}\n", "bogus", "Unknown member 'bogus'")]
    [InlineData(Activity + "    output:\n      - source: Result\n        to: {kind: constant, path: workflow.Result}\n", "constant", "Expected workflow document kind 'path'")]
    [InlineData(Loop + "    result: {kind: constant, value: 1}\n    into: {kind: function, path: workflow.Results}\n", "function", "Expected workflow document kind 'path'")]
    [InlineData(Loop + "    parallel: {join: all, bogus: 1}\n", "bogus", "Unknown member 'bogus'")]
    [InlineData("definitions:\n  - kind: invokeWorkflow\n    workflow: {name: Child, bogus: 1}\n", "bogus", "Unknown member 'bogus'")]
    [InlineData("definitions:\n  - kind: if\n    condition: {kind: constant, value: true}\n    then:\n      - {kind: sequence, bogus: []}\n", "bogus", "Unknown member 'bogus'")]
    [InlineData("activities:\n  reusable:\n    activity: IxIFlow.Generators.Tests.Multiply\n    bogus: 1\n", "bogus", "Unknown member 'bogus'")]
    [InlineData("steps:\n  block:\n    steps: []\n    bogus: 1\n", "bogus", "Unknown member 'bogus'")]
    [InlineData("steps:\n  block:\n    parameters:\n      count: {type: int, bogus: 1}\n", "bogus", "Unknown member 'bogus'")]
    [InlineData("code:\n  clock:\n    language: csharp-expression\n    source: '1'\n    bogus: 1\n", "bogus", "Unknown member 'bogus'")]
    [InlineData("imports:\n  catalogs: []\n  bogus: []\n", "bogus", "Unknown member 'bogus'")]
    [InlineData("definition: []\n", "definition", "Unknown member 'definition'")]
    [InlineData("location: {line: 1, column: 1}\n", "location", "Unknown member 'location'")]
    [InlineData("steps:\n  block:\n    kind: block\n    steps: []\n", "kind: block", "Unknown member 'kind'")]
    [InlineData("definitions:\n  - kind: activity\n    activity: IxIFlow.Generators.Tests.Multiply\n    description: ignored\n", "description", "Unknown member 'description'")]
    public void UnknownMembersAndMismatchedKindsArePositioned(string body, string marker, string message)
    {
        var yaml = Header + body;
        var (line, column) = Position(yaml, marker);
        var parsed = Assert.Throws<YamlException>(() => WorkflowYamlSerializer.Read(yaml));
        Assert.Contains(message, parsed.Message);
        Assert.Equal((line, column), ((int)parsed.Start.Line, (int)parsed.Start.Column));
        var diagnostic = Assert.Single(WorkflowGeneratorHarness.Generate(yaml).Diagnostics);
        Assert.Equal("IXIYAML001", diagnostic.Id);
        Assert.Contains(message, diagnostic.GetMessage());
        Assert.Equal(new LinePosition(line - 1, column - 1), diagnostic.Location.GetLineSpan().StartLinePosition);
    }

    [Fact]
    public void MemberNamesAreCamelCase()
    {
        var error = Assert.Throws<YamlException>(() => WorkflowYamlSerializer.Read("Workflow: {name: Strict, dataType: Data}\n"));
        Assert.Contains("Unknown member 'Workflow'; WorkflowDocument accepts $schema, schemaVersion, workflow, imports", error.Message);
    }

    [Fact]
    public void NamedMapsStayOpenAndTheRootAcceptsASchemaBinding()
    {
        var document = WorkflowYamlSerializer.Read("""
            $schema: ./workflow.schema.json
            workflow: {name: Open, dataType: Data}
            activities:
              Any-Name: {activity: Multiply, defaults: {Factor: 3, Bogus: [1, {nested: true}]}}
            steps:
              Block:
                parameters: {Count: {type: int}, other_name: {default: x}}
                steps: []
            code:
              Clock: {language: csharp-expression, source: '1'}
            definitions:
              - {kind: stepRef, ref: Block, arguments: {Count: {kind: constant, value: 2}, Anything: {path: workflow.Value}}}
              - kind: activity
                activity: Multiply
                output:
                  - {source: Result, to: {kind: path, path: workflow.Result}}
            """);
        Assert.Equal(3L, document.Activities["Any-Name"].Defaults["Factor"]);
        Assert.Contains("Bogus", document.Activities["Any-Name"].Defaults.Keys);
        Assert.Equal(new[] { "Count", "other_name" }, document.Steps["Block"].Parameters.Keys);
        Assert.Contains("Clock", document.Code.Keys);
        Assert.Equal(new[] { "Count", "Anything" }, Assert.IsType<StepReferenceStepDocument>(document.Definitions[0]).Arguments.Keys);
        Assert.Equal("workflow.Result", Assert.Single(Assert.IsType<ActivityStepDocument>(document.Definitions[1]).Output).To.Path);
        Assert.DoesNotContain("$schema", WorkflowYamlSerializer.Write(document));
    }

    [Theory]
    [InlineData("""{"workflow":{"name":"Strict","dataType":"Data"},"definition":[]}""", "'definition' could not be mapped")]
    [InlineData("""{"Workflow":{"name":"Strict","dataType":"Data"}}""", "'Workflow' could not be mapped")]
    [InlineData("""{"workflow":{"name":"Strict","dataType":"Data"},"location":{"line":1,"column":1}}""", "'location' could not be mapped")]
    [InlineData("""{"workflow":{"name":"Strict","dataType":"Data","$schema":"x"}}""", "'$schema' could not be mapped")]
    [InlineData("""{"definitions":[{"kind":"activity","activity":"Multiply","ouptut":[]}]}""", "'ouptut' could not be mapped")]
    [InlineData("""{"definitions":[{"kind":"activity","activity":"Multiply","output":[{"source":"Result","to":{"kind":"constant","path":"workflow.Result"}}]}]}""", "Expected workflow document kind 'path'")]
    [InlineData("""{"definitions":[{"kind":"if","condition":{"path":"workflow.Value","extra":1}}]}""", "'extra' could not be mapped")]
    [InlineData("""{"definitions":[{"kind":"if","condition":{"kind":"unknown"}}]}""", "Unknown workflow document kind 'unknown'")]
    [InlineData("""{"steps":{"block":{"kind":"block","steps":[]}}}""", "'kind' could not be mapped")]
    [InlineData("""{"definitions":[{"kind":"activity","activity":"Multiply","description":"ignored"}]}""", "'description' could not be mapped")]
    public void JsonRejectsWhatYamlRejects(string json, string message) =>
        Assert.Contains(message, Assert.ThrowsAny<JsonException>(() => WorkflowJsonSerializer.Read(json)).Message);

    [Theory]
    [InlineData("\"extra\": 1", "'extra' could not be mapped")]
    [InlineData("\"kind\": \"constant\"", "Expected workflow document kind 'path'")]
    public void JsonErrorsArePositionedInTheDocument(string member, string message)
    {
        var json = $$"""
            {
              "workflow": {"name": "Strict", "dataType": "Data"},
              "definitions": [
                {
                  "kind": "if",
                  "condition": {"path": "workflow.Flag"},
                  "then": [
                    {
                      "kind": "activity",
                      "activity": "Multiply",
                      "output": [
                        {
                          "source": "Result",
                          "to": {
                            {{member}},
                            "path": "workflow.Result"
                          }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        var error = Assert.ThrowsAny<JsonException>(() => WorkflowJsonSerializer.Read(json));
        Assert.Contains(message, error.Message);
        Assert.Equal(Position(json, member).Line - 1, error.LineNumber);
    }

    [Fact]
    public void JsonAcceptsARootSchemaBindingAndWritesAndChecksTheDeclaredKind()
    {
        var document = WorkflowJsonSerializer.Read("""
            {"$schema":"./workflow.schema.json","workflow":{"name":"Strict","dataType":"Data"},
             "definitions":[{"kind":"activity","activity":"Multiply","output":[{"source":"Result","to":{"path":"workflow.Result"}}]}]}
            """);
        var json = WorkflowJsonSerializer.Write(document);
        Assert.DoesNotContain("$schema", json);
        Assert.Contains("{\"kind\":\"activity\"", json);
        Assert.Contains("\"to\":{\"kind\":\"path\"", json);
        var step = Assert.IsType<ActivityStepDocument>(Assert.Single(WorkflowJsonSerializer.Read(json).Definitions));
        Assert.Equal("activity", step.Kind);
        Assert.Equal("workflow.Result", Assert.Single(step.Output).To.Path);
    }

    private static (int Line, int Column) Position(string text, string marker)
    {
        var offset = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"Marker '{marker}' not found");
        var start = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        return (text.Take(offset).Count(character => character == '\n') + 1, offset - start + 1);
    }
}

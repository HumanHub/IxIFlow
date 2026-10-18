using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;

namespace IxIFlow.Generators.Tests;

[Trait("Feature", "authoring"), Trait("Spec", "F-05"), Trait("Spec", "F-25")]
public sealed class ForEachDiagnosticHardeningTests
{
    [Theory]
    [InlineData("alias", "EACH001", "definitions[0].steps[0].as", "      as: entry")]
    [InlineData("resultType", "EACH006", "definitions[0].parallel", "    join: conditionally")]
    [InlineData("condition", "EACH006", "definitions[0].parallel", "    join: conditionally")]
    public async Task InvalidForEachFormsGiveTheirPositionedDiagnosticThroughBothTranslators(
        string defect, string code, string member, string marker)
    {
        var body = defect == "alias" ? """
            - kind: forEach
              items: {path: workflow.Values}
              as: entry
              steps:
                - kind: forEach
                  items: {path: workflow.Values}
                  as: entry
                  steps: []
            """ : """
            - kind: forEach
              items: {path: workflow.Values}
              as: entry
              result: {path: entry}
              parallel:
                join: conditionally
            """ + (defect == "resultType" ? "\n    condition: {path: results.Count > 0}\n" : "\n    resultType: int\n");
        var yaml = "workflow: {name: InvalidEach, dataType: IxIFlow.Generators.Tests.FlowData}\ndefinitions:\n" + body;
        var document = WorkflowYamlSerializer.Read(yaml);
        var validation = await new WorkflowDocumentValidator().ValidateAsync(document, new Contracts());
        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(member, diagnostic.Path);
        Assert.Equal(WorkflowDiagnosticSeverity.Error, diagnostic.Severity);
        var expected = DocumentModel.GetLocation(document, member);
        var lines = yaml.Split('\n');
        var expectedLine = Array.FindLastIndex(lines, line => line == marker);
        Assert.True(expectedLine >= 0, marker);
        Assert.Equal(expectedLine + 1, expected.Line);
        Assert.True(expected.Column > 0);
        foreach (var translate in new Func<string>[] { () => WorkflowSourceTranslator.Translate(yaml, "invalid-each.yaml"),
                     () => WorkflowSourceTranslator.Translate(document, "invalid-each.yaml") })
        {
            var error = Assert.Throws<WorkflowSourceException>(translate);
            Assert.Equal(diagnostic.Message, error.Detail);
            Assert.Equal("invalid-each.yaml", error.Path);
            Assert.Equal(expected.Line, error.Line);
            Assert.Equal(expected.Column, error.Column);
        }
        var generated = WorkflowGeneratorHarness.Generate(yaml, "invalid-each.yaml");
        var positioned = Assert.Single(generated.Diagnostics);
        Assert.Equal("IXIYAML001", positioned.Id);
        Assert.Equal(diagnostic.Message, positioned.GetMessage());
        Assert.Equal(expected.Line - 1, positioned.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Equal(expected.Column - 1, positioned.Location.GetLineSpan().StartLinePosition.Character);
    }

    private sealed class Contracts : IWorkflowValidationContext
    {
        public Task<ActivityDocumentContract?> FindActivityAsync(string key, CancellationToken token) => Task.FromResult<ActivityDocumentContract?>(new(key, "1"));
        public Task<bool> HasDataTypeAsync(string key, CancellationToken token) => Task.FromResult(true);
        public Task<bool> HasEventAsync(string key, CancellationToken token) => Task.FromResult(true);
        public Task<bool> HasWorkflowAsync(string name, int? version, CancellationToken token) => Task.FromResult(true);
        public Task<bool> HasStepTemplateAsync(string key, CancellationToken token) => Task.FromResult(true);
    }
}

using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-05")]
[Trait("Spec", "F-23")]
public sealed class WorkflowSourcePositionRegressionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DefaultTypeErrorsPointAtEachYamlValue(bool template, bool block)
    {
        var yaml = "workflow:\n  name: Positions\n  dataType: IxIFlow.Generators.Tests.FlowData\ndefinitions:\n" +
            (template
                ? "  - {kind: activityRef, ref: echo}\nactivities:\n  echo:\n    activity: "
                : "  - kind: activity\n    activity: ") +
            "IxIFlow.Generators.Tests.WorkflowSourcePositionRegressionTests.StringActivity\n" +
            (block ? "    defaults:\n      Value: true\n      Other: false\n" : "    defaults: {Value: true, Other: false}\n");
        var document = WorkflowYamlSerializer.Read(yaml);
        DocumentNode activity = template ? document.Activities["echo"] : document.Definitions[0];
        var result = WorkflowCompilation.Compile(WorkflowSourceTranslator.Translate(document, "positions.yaml"));
        var errors = result.Diagnostics.Where(item => item.Code == "CS0029").ToArray();
        Assert.Equal(2, errors.Length);
        foreach (var (property, token) in new[] { ("Value", "true"), ("Other", "false") })
        {
            var lines = yaml.Split('\n');
            var line = Array.FindIndex(lines, text => text.Contains(token, StringComparison.Ordinal)) + 1;
            var column = lines[line - 1].IndexOf(token, StringComparison.Ordinal) + 1;
            Assert.Equal(new DocumentLocation(line, column), activity.Members["defaults." + property]);
            Assert.Contains(errors, error => error.Path == "positions.yaml" && error.Line == line && error.Column == column);
        }
    }

    [Theory]
    [InlineData("Input", 2)]
    [InlineData("Input", 8)]
    [InlineData("act", 4)]
    public void TargetErrorsPointAtThePropertyToken(string target, int indentation)
    {
        var indent = new string(' ', indentation);
        var yaml = $"workflow:\n{indent}name: Positions\n{indent}dataType: IxIFlow.Generators.Tests.FlowData\ndefinitions:\n" +
            $"{indent}- kind: activity\n{indent}  activity: IxIFlow.Generators.Tests.WorkflowSourcePositionRegressionTests.StringActivity\n" +
            $"{indent}  input:\n{indent}    - target: {target}\n{indent}      from: {{kind: constant, value: text}}\n";
        var result = WorkflowCompilation.Compile(WorkflowSourceTranslator.Translate(yaml, "positions.yaml"));
        var error = Assert.Single(result.Diagnostics, item => item.Code == "CS1061");
        Assert.Equal("positions.yaml", error.Path);
        Assert.Equal(8, error.Line);
        Assert.Equal(indentation + 15, error.Column);
    }

    [Fact]
    public void OutputNamedLikeTheGeneratedMethodPointsAtThePropertyToken()
    {
        var yaml = "workflow:\n  name: Positions\n  dataType: IxIFlow.Generators.Tests.FlowData\ndefinitions:\n" +
            "  - kind: activity\n    activity: IxIFlow.Generators.Tests.WorkflowSourcePositionRegressionTests.StringActivity\n" +
            "    output:\n      - source: Output\n        to: {path: workflow.Note}\n";
        var result = WorkflowCompilation.Compile(WorkflowSourceTranslator.Translate(yaml, "positions.yaml"));
        var error = Assert.Single(result.Diagnostics, item => item.Code == "CS1061");
        Assert.Equal("positions.yaml", error.Path);
        Assert.Equal(8, error.Line);
        Assert.Equal(17, error.Column);
    }

    public sealed class StringActivity : IAsyncActivity
    {
        public string Value { get; set; } = "";
        public string Other { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

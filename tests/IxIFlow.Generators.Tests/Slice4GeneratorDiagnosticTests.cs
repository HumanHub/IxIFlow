using Microsoft.CodeAnalysis.Text;

namespace IxIFlow.Generators.Tests;

[Trait("Feature", "authoring")]
public sealed class Slice4GeneratorDiagnosticTests
{
    [Fact]
    public void MissingConditionDiagnosticPinsTheSourceLocationAndMemberArgument()
    {
        var yaml = YamlParityTests.Document("- kind: whileDo\n  steps: []");
        var generated = WorkflowGeneratorHarness.Generate(yaml, "missing-condition.yaml");
        var diagnostic = Assert.Single(generated.Diagnostics, item => item.Id == "IXIYAML001");
        var span = diagnostic.Location.GetLineSpan();
        Assert.Equal("missing-condition.yaml", span.Path);
        Assert.Equal(new LinePosition(2, 2), span.StartLinePosition);
        Assert.Equal("Missing 'condition'", diagnostic.GetMessage());
    }
}
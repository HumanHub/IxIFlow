using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-23")]
public sealed class InvalidActivityInputTypeTests
{
    [Fact]
    public void StringConstantIntoAnIntInputFailsCompilationAtTheYamlMapping()
    {
        const string yaml = """
            workflow:
              name: InvalidInputType
              dataType: IxIFlow.Generators.Tests.FlowData
            definitions:
              - kind: activity
                id: multiply
                activity: IxIFlow.Generators.Tests.Multiply
                input:
                  - target: Value
                    from: {kind: constant, value: wrong}
            """;
        WorkflowGeneratorHarness.Driver(yaml, "wrong-input.yaml").RunGeneratorsAndUpdateCompilation(
            GeneratorRegressionTests.Compilation(), out var output, out var generatorDiagnostics);
        Assert.Empty(generatorDiagnostics);
        var errors = output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        var conversion = Assert.Single(errors, diagnostic => diagnostic.Id == "CS0029");
        Assert.Contains("string", conversion.GetMessage());
        Assert.Contains("int", conversion.GetMessage());
        Assert.Contains(errors, diagnostic => diagnostic.Id == "CS1662");
        Assert.All(errors, diagnostic =>
        {
            var mapped = diagnostic.Location.GetMappedLineSpan();
            Assert.Equal("wrong-input.yaml", mapped.Path);
            // The constant's YAML position (line 10, column 39), not the target's line.
            Assert.Equal(new LinePosition(9, 38), mapped.StartLinePosition);
        });
        using var stream = new MemoryStream();
        Assert.False(output.Emit(stream).Success);
    }
}

using IxIFlow.Core;
using IxIFlow.Generators.Tests.Generated;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "G-04")]
[Trait("Spec", "F-05")]
public sealed class BreakpointSourceTests
{
    [Fact]
    public void YamlSourceBreakpointResolvesToGeneratedStepAndVerifiedLine()
    {
        var definition = Workflow_arithmetic.Build();
        var step = definition.StepTable.Single(s => s.AuthorId == "multiply");
        var colon = step.SourceLocation!.LastIndexOf(':');
        var path = step.SourceLocation[..colon];
        var line = int.Parse(step.SourceLocation[(colon + 1)..]);
        Assert.EndsWith("arithmetic.yaml", path);
        var bp = Assert.Single(BreakpointResolver.Resolve(definition, [new("yaml", Source: new(path, line - 1))]));
        Assert.Equal(step.CatalogId, bp.StepId);
        Assert.Equal(new(path, line), bp.VerifiedSource);
        Assert.Equal(line - 1, bp.Source!.Line);
    }
}

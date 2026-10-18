using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace IxIFlow.Generators.Tests;

/// <summary>Runs the workflow generator over in-memory YAML against the test compilation.</summary>
internal static class WorkflowGeneratorHarness
{
    internal static GeneratorDriverRunResult Generate(string yaml, string path = "invalid.yaml") =>
        CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()], [new WorkflowText(path, yaml)],
                optionsProvider: new WorkflowFileOptionsProvider(path))
            .RunGenerators(GeneratorRegressionTests.Compilation()).GetRunResult();
}

/// <summary>A workflow file supplied to the generator as an additional text.</summary>
internal sealed class WorkflowText(string path, string text) : AdditionalText
{
    public override string Path => path;
    public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
}

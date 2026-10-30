using IxIFlow.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Generators.Tests;

public sealed class PrereleaseSlice2bCompilationTests
{
    private static IEnumerable<string> Samples() => Directory.EnumerateFiles(
            Path.Combine(WorkflowSchemaFiles.RepositoryRoot(), "samples"), "*.yaml", SearchOption.AllDirectories)
        .Where(path => !path.EndsWith(".activity.yaml", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".template.yaml", StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.Ordinal);

    [Fact]
    public void EveryWorkflowSampleCompilesAgainstThePublicApi()
    {
        var files = Samples().Select(path => new WorkflowText(path, File.ReadAllText(path))).ToArray();

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([
                typeof(WorkflowDefinition).Assembly.Location, typeof(IAsyncActivity).Assembly.Location,
                typeof(IxIFlow.Samples.ScopeData).Assembly.Location
            ])
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create("SampleApi_" + Guid.NewGuid().ToString("N"),
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create([new WorkflowSourceGenerator().AsSourceGenerator()], files,
            optionsProvider: new WorkflowFileOptionsProvider(files.Select(file => file.Path).ToArray()) { RootNamespace = "Slice2bApi" });

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var errors = output.GetDiagnostics().Concat(diagnostics)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();

        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
    }
}
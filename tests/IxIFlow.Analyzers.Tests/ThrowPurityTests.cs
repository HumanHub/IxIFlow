using System.Collections.Immutable;
using IxIFlow.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IxIFlow.Analyzers.Tests;

[Trait("Feature", "authoring")]
[Trait("Spec", "W-05")]
public sealed class ThrowPurityTests
{
    [Theory]
    [InlineData("new Failure { Code = ctx.WorkflowData.Value }", false)]
    [InlineData("new Failure { Code = DateTime.UtcNow.Day }", true)]
    [InlineData("new Failure { Code = ctx.WorkflowData.Items.Remove(1) ? 1 : 0 }", true)]
    [InlineData("Make(ctx.WorkflowData.Items)", true)]
    [InlineData("new Failure { Values = new List<int> { 1, 2 } }", false)]
    public async Task ThrowFactoriesCheckInitializersAndSourceVisibleMutation(string expression, bool impure)
    {
        var source = "using System; using System.Collections.Generic; using IxIFlow.Builders; " +
            "public class Data { public int Value {get;set;} public List<int> Items {get;set;} = new(); } " +
            "public class Failure : Exception { public int Code {get;init;} public List<int> Values {get;init;} = new(); } " +
            "public class Author { public void Build() => Workflow.Create<Data>(\"Throw\").Throw<Failure>(ctx => " + expression +
            "); private static Failure Make(List<int> items) { var alias = items; alias.Clear(); return new Failure(); } }";

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(Directory.GetFiles(AppContext.BaseDirectory, "IxIFlow*.dll")).Distinct()
            .Select(p => MetadataReference.CreateFromFile(p));

        var compilation = CSharpCompilation.Create("ThrowFactory", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new PurityAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();

        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
        Assert.Equal(impure, diagnostics.Any(d => d.Id == "IXIF0101"));
    }
}
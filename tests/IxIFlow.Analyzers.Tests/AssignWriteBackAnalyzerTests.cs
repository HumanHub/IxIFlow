using System.Collections.Immutable;
using IxIFlow.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IxIFlow.Analyzers.Tests;

[Trait("Feature", "authoring")]
[Trait("Spec", "W-04")]
public sealed class AssignWriteBackAnalyzerTests
{
    [Theory]
    [InlineData("Point.X", "System.IO.File.WriteAllText(\"ignored\", \"ignored\");", true)]
    [InlineData("Point.X", "var ignored = System.DateTime.Now;", true)]
    [InlineData("Point.X", "Items.Clear();", true)]
    [InlineData("Nested.Point.X", "var ignored = System.DateTime.Now;", true)]
    [InlineData("Points[0].X", "var ignored = System.DateTime.Now;", false)]
    [InlineData("Point.Node.Number", "var ignored = System.DateTime.Now;", false)]
    [InlineData("Point.Nodes[0].Number", "Items.Clear();", false)]
    [InlineData("Point.X", "", false)]
    public async Task OnlySettersExecutedByTheLocationCompilerAreAnalyzed(string target, string setter, bool impure)
    {
        var source = $$"""
                       using System.Collections.Generic;
                       using IxIFlow.Builders;
                       public class Node { public int Number { get; set; } }
                       public struct Point
                       {
                           public int X { get; set; }
                           public Node Node { get; set; }
                           public List<Node> Nodes { get; set; }
                       }
                       public struct Nested
                       {
                           public List<int> Items { get; set; }
                           public Point Point { get => new(); set { {{setter}} } }
                       }
                       public class Data
                       {
                           public List<int> Items { get; set; } = new();
                           public Point Point { get => new(); set { {{setter}} } }
                           public Nested Nested { get; set; }
                           public List<Point> Points { get; set; } = new();
                       }
                       public class Author
                       {
                           public void Build() => Workflow.Create<Data>("Assign").Assign(ctx => ctx.WorkflowData.{{target}}, ctx => 1);
                       }
                       """;

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(Directory.GetFiles(AppContext.BaseDirectory, "IxIFlow*.dll")).Distinct()
            .Select(path => MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create("AssignWriteBack", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new PurityAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
        Assert.Equal(impure, diagnostics.Any(diagnostic => diagnostic.Id == "IXIF0101"));

        if (impure)
            Assert.Contains(diagnostics,
                diagnostic => diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan)
                    .StartsWith("ctx.WorkflowData."));
    }
}
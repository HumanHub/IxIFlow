using System.Collections.Immutable;
using IxIFlow.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IxIFlow.Analyzers.Tests;

[Trait("Feature", "authoring")]
[Trait("Spec", "W-04")]
public sealed class AssignAnalyzerTests
{
    [Theory]
    [InlineData("ctx => ctx.WorkflowData.Removed", "ctx => ctx.WorkflowData.Items.Remove(1)", true)]
    [InlineData("ctx => ctx.WorkflowData.Number", "ctx => Remove(ctx.WorkflowData.Items)", true)]
    [InlineData("ctx => ctx.WorkflowData.Items[Remove(ctx.WorkflowData.Items)]", "ctx => 3", true)]
    [InlineData("ctx => ctx.WorkflowData.GetNode(Remove(ctx.WorkflowData.Items)).Number", "ctx => 3", true)]
    [InlineData("ctx => ctx.WorkflowData.Number", "ctx => ctx.WorkflowData.Number + 3", false)]
    [InlineData("ctx => ctx.WorkflowData.Items", "ctx => new List<int> { 1, 2 }", false)]
    [InlineData("ctx => ctx.WorkflowData.Items", "ctx => Fresh(1)", false)]
    [InlineData("ctx => ctx.WorkflowData.GetterImpureSetterPure", "ctx => 1", false)]
    [InlineData("ctx => ctx.WorkflowData.GetterPureSetterImpure", "ctx => 1", true)]
    [InlineData("ctx => ctx.WorkflowData.SetterMutatesCollection", "ctx => 1", true)]
    public async Task AssignSeparatesTheDesignatedDestinationFromReceiverIndexValueAndSetterEffects(string target, string value,
        bool impure)
    {
        var source = $$"""
                       using System;
                       using System.Collections.Generic;
                       using IxIFlow.Builders;
                       public class Data
                       {
                           public int Number { get; set; }
                           public bool Removed { get; set; }
                           public List<int> Items { get; set; } = new();
                           public Data GetNode(int ignored) => this;
                           public int GetterImpureSetterPure { get => DateTime.Now.Second; set { } }
                           public int GetterPureSetterImpure { get => 0; set { var ignored = DateTime.Now; } }
                           public int SetterMutatesCollection { get => 0; set { var alias = Items; alias.Clear(); } }
                       }
                       public class Author
                       {
                           public void Build() => Workflow.Create<Data>("Assign").Assign({{target}}, {{value}});
                           private static int Remove(List<int> items) { var alias = items; alias.Clear(); return 0; }
                           private static List<int> Fresh(int value) { var list = new List<int>(); var alias = list; alias.Add(value); return list; }
                       }
                       """;

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(Directory.GetFiles(AppContext.BaseDirectory, "IxIFlow*.dll")).Distinct()
            .Select(path => MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create("AssignAnalysis", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new PurityAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
        Assert.Equal(impure, diagnostics.Any(diagnostic => diagnostic.Id == "IXIF0101"));
    }
}
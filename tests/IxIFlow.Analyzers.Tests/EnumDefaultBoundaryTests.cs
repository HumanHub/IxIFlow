using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Analyzers.Tests;

[Trait("Feature", "authoring"), Trait("Spec", "F-24"), Trait("Spec", "F-22")]
public sealed class EnumDefaultBoundaryTests
{
    [Theory]
    [InlineData("byte", "Unknown", false, false, "")]
    [InlineData("byte", "Read, Write", false, true, "3")]
    [InlineData("byte", "255", false, true, "255")]
    [InlineData("sbyte", "-128", false, true, "-128")]
    [InlineData("byte", null, true, true, "null")]
    [InlineData("byte", "Read, Write", true, true, "3")]
    [InlineData("byte", "255", true, true, "255")]
    [InlineData("byte", "Unknown", true, false, "")]
    public Task AnalyzerAndRuntimeAgreeOnEnumDefaults(string underlying, string? value, bool nullable, bool accepted, string expected) =>
        Verify(underlying, value, nullable, accepted, expected);

    [Theory]
    [InlineData("byte", "256", false, false, "")]
    [InlineData("sbyte", "128", false, false, "")]
    [InlineData("ushort", "65536", false, false, "")]
    [InlineData("short", "32768", false, false, "")]
    [InlineData("uint", "4294967296", false, false, "")]
    [InlineData("int", "2147483648", false, false, "")]
    [InlineData("byte", "-1", false, false, "")]
    [InlineData("ushort", "-1", false, false, "")]
    [InlineData("uint", "-1", false, false, "")]
    [InlineData("ulong", "-1", false, false, "")]
    [InlineData("byte", "256", true, false, "")]
    [InlineData("ulong", "18446744073709551615", false, true, "18446744073709551615")]
    public Task NumericEnumRangesMustAgree(string underlying, string? value, bool nullable, bool accepted, string expected) =>
        Verify(underlying, value, nullable, accepted, expected);

    [Theory]
    [InlineData("long", "9223372036854775808", false, false, "")]
    [InlineData("ulong", "18446744073709551616", false, false, "")]
    public Task ValuesBeyondAllNumericRangesAreRejected(string underlying, string? value, bool nullable, bool accepted, string expected) =>
        Verify(underlying, value, nullable, accepted, expected);

    private static async Task Verify(string underlying, string? value, bool nullable, bool accepted, string expected)
    {
        var literal = value == null ? "null" : Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, true);
        var source = VerifyUsings + $$"""
            [Flags] public enum Choice : {{underlying}} { None=0, Read=1, Write=2 }
            public sealed class Data { public Choice{{(nullable ? "?" : "")}} Value { get; set; } }
            public sealed class Contract : IAsyncActivity
            {
                [ActivityInputOutput, DefaultValue({{literal}})] public Choice{{(nullable ? "?" : "")}} Value { get; set; }
                public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
            }
            public static class Flow
            {
                public static WorkflowDefinition Build() => Workflow.Create<Data>("EnumDefault")
                    .Step<Contract>(step => step.Output(a => a.Value).To(ctx => ctx.WorkflowData.Value)).Build();
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(Directory.GetFiles(AppContext.BaseDirectory, "IxIFlow*.dll"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("EnumDefault_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new MappingAnalyzer())).GetAnalyzerDiagnosticsAsync();
        var invalidDefaults = diagnostics.Where(d => d.Id == "IXIF0009").ToArray();
        Assert.Empty(diagnostics.Where(d => d.Id != "IXIF0009"));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        stream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        WorkflowDefinition? definition = null;
        Exception? runtimeError = null;
        try { definition = (WorkflowDefinition)assembly.GetType("Flow")!.GetMethod("Build")!.Invoke(null, null)!; }
        catch (TargetInvocationException error) { runtimeError = error.InnerException; }
        var runtimeAccepted = runtimeError == null;
        Assert.Equal(accepted, runtimeAccepted);
        if (!accepted) Assert.Equal(WorkflowErrorKind.InvalidDefinition, Assert.IsType<WorkflowEngineException>(runtimeError).Kind);
        // Run the actual analyzer and runtime on the same declaration, without reproducing conversion rules.
        Assert.True(runtimeAccepted == (invalidDefaults.Length == 0),
            $"{underlying} default '{value ?? "null"}' nullable={nullable}: runtime accepted={runtimeAccepted}, IXIF0009 count={invalidDefaults.Length}");
        if (!accepted)
        {
            var diagnostic = Assert.Single(invalidDefaults);
            Assert.Contains("Contract.Value", diagnostic.GetMessage());
            return;
        }
        Assert.Empty(invalidDefaults);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition!, Activator.CreateInstance(assembly.GetType("Data")!)!);
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var actual = result.WorkflowData!.GetType().GetProperty("Value")!.GetValue(result.WorkflowData);
        Assert.Equal(expected, actual == null ? "null" : Convert.ToString(Convert.ChangeType(actual, Enum.GetUnderlyingType(actual.GetType())), System.Globalization.CultureInfo.InvariantCulture));
    }

    private const string VerifyUsings = """
        using System;
        using System.ComponentModel;
        using System.Threading;
        using System.Threading.Tasks;
        using IxIFlow.ActivitySdk;
        using IxIFlow.Builders;
        using IxIFlow.Core;

        """;
}

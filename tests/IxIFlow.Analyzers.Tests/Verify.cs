using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace IxIFlow.Analyzers.Tests;

/// <summary>
/// Compiles a workflow source against .NET 10 and the engine, runs an analyzer or a code fix on it, and compares the
/// diagnostics with the expected ones, messages included. Every source starts with <see cref="Usings"/>.
/// </summary>
internal static class Verify
{
    public const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using IxIFlow.ActivitySdk;
        using IxIFlow.Builders;
        using IxIFlow.Core;

        """;

    private static readonly ReferenceAssemblies Net10 = new("net10.0",
        new PackageIdentity("Microsoft.NETCore.App.Ref", "10.0.0"), Path.Combine("ref", "net10.0"));

    // The engine and the extension abstractions its signatures use, as this test project builds them.
    private static readonly MetadataReference[] Engine = Directory
        .GetFiles(AppContext.BaseDirectory, "*.dll")
        .Where(file => Path.GetFileName(file) is var name &&
                       (name.StartsWith("IxIFlow.", StringComparison.Ordinal) &&
                        !name.StartsWith("IxIFlow.Analyzers", StringComparison.Ordinal) ||
                        name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)))
        .Select(file => (MetadataReference)MetadataReference.CreateFromFile(file))
        .ToArray();

    public static DiagnosticResult Diagnostic(DiagnosticDescriptor descriptor, int location, params object[] arguments) =>
        new DiagnosticResult(descriptor).WithLocation(location).WithArguments(arguments);

    public static Task Analyzer<TAnalyzer>(string source, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = Usings + source,
            ReferenceAssemblies = Net10
        };
        test.TestState.AdditionalReferences.AddRange(Engine);
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    public static Task CodeFix<TAnalyzer, TFix>(string source, string fixedSource, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TFix : CodeFixProvider, new()
    {
        var test = new CSharpCodeFixTest<TAnalyzer, TFix, DefaultVerifier>
        {
            TestCode = Usings + source,
            FixedCode = Usings + fixedSource,
            ReferenceAssemblies = Net10
        };
        test.TestState.AdditionalReferences.AddRange(Engine);
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }
}

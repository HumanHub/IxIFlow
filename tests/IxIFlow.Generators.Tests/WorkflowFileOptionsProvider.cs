using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IxIFlow.Generators.Tests;

internal sealed class WorkflowFileOptionsProvider(params string[] paths) : AnalyzerConfigOptionsProvider
{
    private readonly HashSet<string> _paths = new(paths, StringComparer.OrdinalIgnoreCase);
    private static readonly AnalyzerConfigOptions Empty = new Options(false);
    public string? RootNamespace { get; init; }
    public override AnalyzerConfigOptions GlobalOptions => new Options(false, RootNamespace);
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Options(_paths.Contains(textFile.Path));

    private sealed class Options(bool enabled, string? rootNamespace = null) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            if (key == "build_property.RootNamespace" && rootNamespace != null)
            { value = rootNamespace; return true; }
            value = enabled ? "true" : "false";
            return key == "build_metadata.AdditionalFiles.IxIFlowWorkflow";
        }
    }
}

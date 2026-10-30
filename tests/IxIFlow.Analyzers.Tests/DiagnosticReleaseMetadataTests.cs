using IxIFlow.Analyzers;
using Microsoft.CodeAnalysis.Diagnostics;

namespace IxIFlow.Analyzers.Tests;

public sealed class DiagnosticReleaseMetadataTests
{
    [Fact]
    public void ShippedDiagnosticsHaveReadableTitlesAndStableHelpLinks()
    {
        DiagnosticAnalyzer[] analyzers = [new MappingAnalyzer(), new ScopeAnalyzer(), new PurityAnalyzer()];
        var descriptors = analyzers.SelectMany(analyzer => analyzer.SupportedDiagnostics).DistinctBy(item => item.Id).ToArray();
        Assert.Equal(24, descriptors.Length);

        foreach (var descriptor in descriptors)
        {
            Assert.Contains(" ", descriptor.Title.ToString());

            Assert.Equal("https://github.com/HumanHub/IxIFlow/blob/main/docs/diagnostics.md#" + descriptor.Id.ToLowerInvariant(),
                descriptor.HelpLinkUri);
        }
    }
}
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>A process without a shared compatibility profile neither publishes nor hosts bundles (F-02, D-49).</summary>
[Trait("Feature", "registration")]
public sealed class BundleProfileGuardTests
{
    [Fact]
    public void AFrameworkDependentProcessHasAUsableProfileThatSharesTheEngine()
    {
        Assert.True(WorkflowSharedAssemblies.IsUsable);
        Assert.True(WorkflowSharedAssemblies.IsShared(typeof(WorkflowDefinition).Assembly.GetName()));
    }

    [Fact]
    public async Task APublishedManifestAlwaysDeclaresTheEngineShared()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(version.ManifestJson!)!;
        Assert.Contains(manifest.SharedAssemblies.Keys, name => name.StartsWith("IxIFlow,", StringComparison.Ordinal));
        Assert.DoesNotContain(manifest.Entries.Keys, path => path.EndsWith("/IxIFlow.dll", StringComparison.OrdinalIgnoreCase));
    }
}

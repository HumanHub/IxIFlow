using System.Reflection;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Every execution runs in the one public <see cref="WorkflowHost"/> (B-01, D-16). The application reaches it as a
/// hosted service; its slots, queues and per-instance work stay the engine's.
/// </summary>
[Trait("Feature", "host")]
[Trait("Spec", "B-01")]
public sealed class WorkflowHostTests
{
    [Fact]
    public void AddIxIFlowRegistersOneHostAsAHostedService()
    {
        using var provider = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();

        var host = provider.GetRequiredService<WorkflowHost>();

        Assert.Same(host, provider.GetRequiredService<WorkflowHost>());
        Assert.Contains(provider.GetServices<IHostedService>(), service => ReferenceEquals(service, host));
    }

    [Fact]
    public void TheHostIsPublicOnlyAsAHostedService()
    {
        var declared = typeof(WorkflowHost)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .Order()
            .ToArray();

        Assert.True(typeof(WorkflowHost).IsPublic);
        Assert.Equal(["Dispose", "StartAsync", "StopAsync"], declared);
        Assert.Empty(typeof(WorkflowHost).GetConstructors());
        Assert.Empty(typeof(WorkflowHost).GetProperties(BindingFlags.Public | BindingFlags.Instance));
    }
}

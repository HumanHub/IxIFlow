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
        using var provider = new ServiceCollection().AddLogging().AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring).Services.BuildServiceProvider();

        var host = provider.GetRequiredService<WorkflowHost>();
        var hosted = provider.GetServices<IHostedService>().ToArray();

        Assert.Same(host, provider.GetRequiredService<WorkflowHost>());
        // Hosted services stop in reverse order: the host comes last, so it drains before the recovery scanner stops.
        Assert.Same(host, hosted[^1]);
        Assert.Single(hosted, service => ReferenceEquals(service, host));
        Assert.Contains(hosted[..^1], service => service is WorkflowRecoveryService);
    }

    [Fact]
    public void EachWorkItemScopeHasItsOwnInstanceOperations()
    {
        using var provider = new ServiceCollection().AddLogging().AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring).Services.BuildServiceProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(first.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>(),
            first.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>(),
            second.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<WorkflowHost.SlotClient>(),
            second.ServiceProvider.GetRequiredService<WorkflowHost.SlotClient>());
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
        Assert.Empty(typeof(WorkflowHost).GetNestedTypes(BindingFlags.Public));
        Assert.Empty(typeof(WorkflowHost).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
        Assert.Empty(typeof(WorkflowHost).GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));
    }
}

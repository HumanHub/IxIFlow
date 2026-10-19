using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "J-02")]
[Trait("Spec", "C-08")]
[Trait("Spec", "B-04")]
public sealed class PlacementScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.MultipleHosts), MemberType = typeof(StoreProfiles))]
    public async Task DemandedInstanceRunsOnlyOnTheMatchingHost(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var ran = new ConcurrentQueue<string>();
        ServiceProvider Host(string name)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddIxIFlow(options =>
            {
                options.Slots = 1;
                options.Capabilities["gpu"] = name == "matching" ? "cuda" : "metal";
                options.Capabilities["region"] = "eu";
            });
            services.Replace(ServiceDescriptor.Singleton(store.NewRepository()));
            services.Replace(ServiceDescriptor.Singleton(new InProcessInstanceGate()));
            services.AddSingleton(new Probe(name, ran));
            store.ConfigureHost(services);
            return services.BuildServiceProvider();
        }
        await using var wrong = Host("wrong");
        await using var matching = Host("matching");
        var definition = Workflow.Create<Data>("DistributedPlacement").Demand("gpu", "cuda").Demand("region", "eu")
            .Step<RecordHost>().WaitFor<Go>("go").Step<RecordHost>().Build();
        await wrong.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        await matching.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        await store.StartHostLinksAsync(wrong);
        await store.StartHostLinksAsync(matching);
        var first = wrong.GetRequiredService<IWorkflowClient>();
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var id = await first.StartAsync(definition, new Data(), cancellationToken: patience.Token);
        var wrongScanner = wrong.GetServices<IHostedService>().OfType<WorkflowRecoveryService>().Single();
        var matchingScanner = matching.GetServices<IHostedService>().OfType<WorkflowRecoveryService>().Single();
        await wrongScanner.RecoverOnceAsync(patience.Token);
        Assert.Empty(ran);
        await Task.WhenAll(wrongScanner.RecoverOnceAsync(patience.Token), matchingScanner.RecoverOnceAsync(patience.Token));
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await first.AwaitAsync(id, cancellationToken: patience.Token)).Status);
        Assert.Equal(new[] { "matching" }, ran);
        var send = first.ResumeAsync(id, "go", new Go(), patience.Token);
        var repository = wrong.GetRequiredService<IWorkflowStateRepository>();
        while ((await repository.GetPendingEventsAsync(id, 0, 10)).Events.Count == 0)
            await Task.Delay(10, patience.Token);
        await wrongScanner.RecoverOnceAsync(patience.Token);
        Assert.Equal(new[] { "matching" }, ran);
        await matchingScanner.RecoverOnceAsync(patience.Token);
        Assert.Equal(WorkflowExecutionStatus.Success, (await send).Status);
        Assert.Equal(new[] { "matching", "matching" }, ran);
        await wrong.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
        await matching.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
    }

    public sealed class Data;
    public sealed class Go;
    public sealed record Probe(string Host, ConcurrentQueue<string> Ran);
    public sealed class RecordHost(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { probe.Ran.Enqueue(probe.Host); return Task.CompletedTask; }
    }
}

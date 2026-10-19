using System.Threading.Channels;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "C-06")]
[Trait("Spec", "C-14")]
public sealed class HostRegistryTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(5000)]
    [InlineData(2000)]
    public async Task HostRegistersHeartbeatsRepublishesRuntimeDefinitionsAndUnregistersOnStop(int intervalMs)
    {
        var storeTime = new FakeTimeProvider();
        var hostTime = new TimerRecordingClock(storeTime.GetUtcNow().AddDays(2));
        var store = new ObservedStore(storeTime);
        var interval = TimeSpan.FromMilliseconds(intervalMs);
        var definition = Workflow.Create<RegistryData>("RegistryStart", 3).Step<Noop>().Build();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(hostTime);
        builder.Services.AddSingleton<IWorkflowStateRepository>(store);
        builder.Services.AddSingleton(definition);
        builder.Services.AddIxIFlow(options =>
        {
            options.HostName = "orders-display";
            options.Slots = 7;
            options.HeartbeatInterval = interval;
            options.Capabilities["region"] = "eu";
        });
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var initial = Assert.Single(await store.GetLiveMembersAsync());
            var settings = host.Services.GetRequiredService<HostSettings>();
            Assert.Equal(settings.Id, initial.Id);
            Assert.NotEqual(Guid.Empty, initial.Id);
            Assert.Equal("orders-display", initial.Name);
            Assert.Equal(7, initial.Slots); // Ports the registry's advertised health/load metadata.
            Assert.Equal(interval, initial.HeartbeatInterval);
            Assert.Equal(storeTime.GetUtcNow().UtcDateTime, initial.StartedAtUtc);
            Assert.Equal(initial.Id.ToString("D"), initial.Capabilities["host"]);
            Assert.Equal("eu", initial.Capabilities["region"]);
            Assert.False(string.IsNullOrWhiteSpace(initial.EngineVersion));
            foreach (var key in new[] { "os", "arch", "runtime" })
                Assert.False(string.IsNullOrWhiteSpace(initial.Capabilities[key]));
            Assert.Equal(new WorkflowScopeCatalog(definition).Fingerprint,
                initial.Capabilities["workflow:RegistryStart@3"]);

            await hostTime.WaitForTimerAsync(timer => timer.Period == interval && timer.Pending);
            storeTime.Advance(interval);
            hostTime.Advance(interval);
            Assert.True(await store.Beats.Reader.ReadAsync().AsTask().WaitAsync(Patience));
            var renewed = Assert.Single(await store.GetLiveMembersAsync());
            Assert.Equal(initial.StartedAtUtc, renewed.StartedAtUtc);
            Assert.Equal(storeTime.GetUtcNow().UtcDateTime, renewed.HeartbeatAtUtc);

            var child = Workflow.Create<RegistryData>("RuntimeChild", 2).Step<Noop>().Build();
            await host.Services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(child);
            Assert.False(renewed.Capabilities.ContainsKey("workflow:RuntimeChild@2"));
            storeTime.Advance(interval);
            hostTime.Advance(interval);
            Assert.True(await store.Beats.Reader.ReadAsync().AsTask().WaitAsync(Patience));
            Assert.Equal(new WorkflowScopeCatalog(child).Fingerprint,
                Assert.Single(await store.GetLiveMembersAsync()).Capabilities["workflow:RuntimeChild@2"]);
        }
        finally { await host.StopAsync(); }
        Assert.Empty(await store.GetLiveMembersAsync());
        hostTime.Advance(interval * 3);
        Assert.False(store.Beats.Reader.TryRead(out _));
        Assert.Empty(await store.GetLiveMembersAsync());
    }

    [Fact]
    public async Task AFailedHeartbeatIsRetriedAndAPrunedHostRegistersAgain()
    {
        var time = new TimerRecordingClock();
        var storeTime = new FakeTimeProvider(time.GetUtcNow());
        var store = new ObservedStore(storeTime);
        var abandoned = MemberRegistryContract.Registration();
        await store.RegisterMemberAsync(abandoned);
        var settings = HostSettings.Default;
        using var service = new WorkflowHeartbeatService(store, new WorkflowVersionRegistry(), settings, time,
            NullLogger<WorkflowHeartbeatService>.Instance);
        await service.StartAsync(default);
        var initial = await store.Registrations.Reader.ReadAsync().AsTask().WaitAsync(Patience);
        await time.WaitForTimerAsync(timer => timer.Period == settings.HeartbeatInterval && timer.Pending);
        storeTime.Advance(TimeSpan.FromSeconds(16));
        store.FailNextHeartbeat = true;
        time.Advance(settings.HeartbeatInterval);
        Assert.False(await store.Beats.Reader.ReadAsync().AsTask().WaitAsync(Patience));
        time.Advance(settings.HeartbeatInterval);
        Assert.True(await store.Beats.Reader.ReadAsync().AsTask().WaitAsync(Patience));
        Assert.Equal(initial.Id, Assert.Single(await store.GetLiveMembersAsync()).Id);

        storeTime.Advance(TimeSpan.FromHours(2));
        time.Advance(settings.HeartbeatInterval);
        var restarted = await store.Registrations.Reader.ReadAsync().AsTask().WaitAsync(Patience);
        Assert.Equal(initial.Id, restarted.Id);
        Assert.Equal(storeTime.GetUtcNow().UtcDateTime, Assert.Single(await store.GetLiveMembersAsync()).StartedAtUtc);
        Assert.False(await store.HeartbeatAsync(abandoned.Id, abandoned.Capabilities));
        await service.StopAsync(default);
        Assert.Empty(await store.GetLiveMembersAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSameDisplayNameGetsANewIdInEachHost(bool configured)
    {
        var services = new ServiceCollection().AddLogging();
        if (configured) services.AddIxIFlow(options => options.HostName = "same-name");
        else services.AddIxIFlow();
        using var first = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var second = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var one = first.GetRequiredService<HostSettings>();
        var two = second.GetRequiredService<HostSettings>();
        Assert.Equal(one.Name, two.Name);
        Assert.NotEqual(one.Id, two.Id);
        Assert.Same(one, first.GetRequiredService<HostSettings>());
        Assert.Single(first.GetServices<IHostedService>().OfType<WorkflowHeartbeatService>());
    }

    [Fact]
    public void DefaultHeartbeatIsFiveSecondsAndConfiguredCapabilitiesAreCopied()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), new IxIFlowOptions().HeartbeatInterval);
        var input = new Dictionary<string, string> { ["region"] = "eu" };
        var services = new ServiceCollection();
        services.AddIxIFlow(options => options.Capabilities = input);
        input["region"] = "changed";
        using var provider = services.BuildServiceProvider();
        Assert.Equal("eu", provider.GetRequiredService<HostSettings>().Capabilities["region"]);
    }

    [Theory]
    [InlineData("os")]
    [InlineData("arch")]
    [InlineData("runtime")]
    [InlineData("host")]
    [InlineData("workflow:Forged@1")]
    [InlineData(" ")]
    public void InvalidOrAutomaticConfiguredCapabilitiesAreRefused(string key) =>
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddIxIFlow(options => options.Capabilities[key] = "fake"));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void InvalidHeartbeatIntervalIsRefused(long ticks) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddIxIFlow(options =>
            options.HeartbeatInterval = TimeSpan.FromTicks(ticks)));

    [Fact]
    public async Task DecoratorsForwardRegistryOperations()
    {
        foreach (var store in new IWorkflowStateRepository[]
                 { GuardedStateRepository.Wrap(new InMemoryWorkflowStateRepository()), new FaultInjectingRepository() })
        {
            var member = MemberRegistryContract.Registration();
            await store.RegisterMemberAsync(member);
            Assert.Equal(member.Id, Assert.Single(await store.GetLiveMembersAsync()).Id);
            Assert.True(await store.HeartbeatAsync(member.Id, new Dictionary<string, string> { ["new"] = "value" }));
            Assert.Equal("value", Assert.Single(await store.GetLiveMembersAsync()).Capabilities["new"]);
            await store.UnregisterMemberAsync(member.Id);
            Assert.Empty(await store.GetLiveMembersAsync());
        }
    }

    private sealed class ObservedStore(TimeProvider time) : InMemoryWorkflowStateRepository(time), IWorkflowStateRepository
    {
        public Channel<bool> Beats { get; } = Channel.CreateUnbounded<bool>();
        public Channel<MemberRegistration> Registrations { get; } = Channel.CreateUnbounded<MemberRegistration>();
        public bool FailNextHeartbeat { get; set; }
        async Task IWorkflowStateRepository.RegisterMemberAsync(MemberRegistration member, CancellationToken token)
        {
            await base.RegisterMemberAsync(member, token);
            Registrations.Writer.TryWrite(member);
        }
        async Task<bool> IWorkflowStateRepository.HeartbeatAsync(Guid id, IReadOnlyDictionary<string, string> capabilities,
            CancellationToken token)
        {
            if (FailNextHeartbeat)
            {
                FailNextHeartbeat = false;
                Beats.Writer.TryWrite(false);
                throw new IOException("heartbeat unavailable");
            }
            var result = await base.HeartbeatAsync(id, capabilities, token);
            Beats.Writer.TryWrite(result);
            return result;
        }
    }

    public sealed class RegistryData { }
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

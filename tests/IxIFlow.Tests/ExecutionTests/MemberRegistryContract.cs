using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

public enum MemberRegistryScenario
{
    Registration, HeartbeatAndLiveness, CapabilityRepublish, Pruning, Unregister, MemberKinds,
    AtomicSnapshots, CancelledAndInvalidWrites, ConfiguredInterval
}

internal sealed class MemberRegistryContract(IWorkflowStateRepository store, Func<Guid, TimeSpan, Task> age,
    Func<Task<DateTime>> now, bool sqlite = false, Func<Guid, Task>? assertRowsRemoved = null)
{
    private readonly List<Guid> _ids = [];
    internal static MemberRegistration Registration(Guid? id = null, MemberKind kind = MemberKind.Host) =>
        new(id ?? Guid.NewGuid(), kind, "orders", kind == MemberKind.Coordinator ? "https://coordinator:7443" : null,
            kind == MemberKind.Host ? 8 : 0, "1.0.0", TimeSpan.FromSeconds(5),
            new Dictionary<string, string> { ["region"] = "eu", ["gpu"] = "yes" });

    private async Task<MemberRegistration> Register(MemberRegistration? member = null)
    {
        member ??= Registration();
        _ids.Add(member.Id);
        await store.RegisterMemberAsync(member);
        return member;
    }

    private async Task<StoreMember> Read(Guid id) => Assert.Single(await store.GetLiveMembersAsync(), m => m.Id == id);

    internal async Task VerifyAsync(MemberRegistryScenario scenario)
    {
        try
        {
            switch (scenario)
            {
                case MemberRegistryScenario.Registration: await RegisterAndRead(); break;
                case MemberRegistryScenario.HeartbeatAndLiveness: await HeartbeatAndLiveness(); break;
                case MemberRegistryScenario.CapabilityRepublish: await CapabilityRepublish(); break;
                case MemberRegistryScenario.Pruning: await Pruning(); break;
                case MemberRegistryScenario.Unregister: await Unregister(); break;
                case MemberRegistryScenario.MemberKinds: await MemberKinds(); break;
                case MemberRegistryScenario.AtomicSnapshots: await AtomicSnapshots(); break;
                case MemberRegistryScenario.CancelledAndInvalidWrites: await CancelledAndInvalidWrites(); break;
                case MemberRegistryScenario.ConfiguredInterval: await ConfiguredInterval(); break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }
        finally
        {
            foreach (var id in _ids) await store.UnregisterMemberAsync(id);
        }
    }

    private async Task RegisterAndRead()
    {
        var before = await now();
        var member = await Register();
        var after = await now();
        var saved = await Read(member.Id);
        Assert.Equal(member.Kind, saved.Kind);
        Assert.Equal(member.Name, saved.Name);
        Assert.Equal(member.Endpoint, saved.Endpoint);
        Assert.Equal(member.Slots, saved.Slots);
        Assert.Equal(member.EngineVersion, saved.EngineVersion);
        Assert.Equal(member.HeartbeatInterval, saved.HeartbeatInterval);
        Assert.Equal(member.Capabilities.OrderBy(p => p.Key), saved.Capabilities.OrderBy(p => p.Key));
        Assert.InRange(saved.StartedAtUtc, before, after);
        Assert.Equal(DateTimeKind.Utc, saved.StartedAtUtc.Kind);
        Assert.Equal(saved.StartedAtUtc, saved.HeartbeatAtUtc);
        await age(member.Id, TimeSpan.FromSeconds(1));
        await store.RegisterMemberAsync(member with { Slots = 16 });
        var renewed = await Read(member.Id);
        Assert.Equal(saved.StartedAtUtc, renewed.StartedAtUtc);
        Assert.Equal(16, renewed.Slots);
        Assert.True(renewed.HeartbeatAtUtc >= saved.HeartbeatAtUtc);
    }

    // Ports HostRegistry_Should_Track_Host_Heartbeats and registry heartbeat renewal.
    private async Task HeartbeatAndLiveness()
    {
        var member = await Register();
        var first = await Read(member.Id);
        await age(member.Id, TimeSpan.FromSeconds(10));
        await Read(member.Id); // Two missed beats are live.
        var before = await now();
        Assert.True(await store.HeartbeatAsync(member.Id, member.Capabilities));
        var renewed = await Read(member.Id);
        Assert.Equal(first.StartedAtUtc, renewed.StartedAtUtc);
        Assert.InRange(renewed.HeartbeatAtUtc, before, await now());
        await age(member.Id, TimeSpan.FromSeconds(16));
        Assert.DoesNotContain(await store.GetLiveMembersAsync(), m => m.Id == member.Id);
        Assert.True(await store.HeartbeatAsync(member.Id, member.Capabilities)); // Stale is not yet pruned.
        await Read(member.Id);
    }

    private async Task CapabilityRepublish()
    {
        var source = new Dictionary<string, string> { ["region"] = "eu", ["old"] = "old" };
        var member = await Register(Registration() with { Capabilities = source });
        source["region"] = "mutated";
        var oldSnapshot = await Read(member.Id);
        Assert.Equal("eu", oldSnapshot.Capabilities["region"]);
        var replacement = new Dictionary<string, string> { ["region"] = "us", ["new"] = "new", ["GPU"] = "A", ["gpu"] = "B" };
        Assert.True(await store.HeartbeatAsync(member.Id, replacement));
        replacement["region"] = "mutated";
        var updated = await Read(member.Id);
        Assert.Equal("us", updated.Capabilities["region"]);
        Assert.False(updated.Capabilities.ContainsKey("old"));
        Assert.Equal("new", updated.Capabilities["new"]);
        Assert.Equal("A", updated.Capabilities["GPU"]);
        Assert.Equal("B", updated.Capabilities["gpu"]);
        Assert.Equal("eu", oldSnapshot.Capabilities["region"]);
        Assert.True(await store.HeartbeatAsync(member.Id, new Dictionary<string, string>()));
        Assert.Empty((await Read(member.Id)).Capabilities);
    }

    // Ports stale-host background maintenance. Metrics history belongs to later observability work.
    private async Task Pruning()
    {
        var member = await Register();
        await age(member.Id, TimeSpan.FromMinutes(59));
        Assert.DoesNotContain(await store.GetLiveMembersAsync(), m => m.Id == member.Id);
        Assert.True(await store.HeartbeatAsync(member.Id, member.Capabilities));
        await age(member.Id, TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(await store.GetLiveMembersAsync(), m => m.Id == member.Id);
        if (assertRowsRemoved != null) await assertRowsRemoved(member.Id);
        Assert.False(await store.HeartbeatAsync(member.Id, member.Capabilities));
        await store.RegisterMemberAsync(member);
        await Read(member.Id);
    }

    private async Task Unregister()
    {
        var member = await Register();
        await store.UnregisterMemberAsync(member.Id);
        await store.UnregisterMemberAsync(member.Id);
        Assert.DoesNotContain(await store.GetLiveMembersAsync(), m => m.Id == member.Id);
        Assert.False(await store.HeartbeatAsync(member.Id, member.Capabilities));
        if (assertRowsRemoved != null) await assertRowsRemoved(member.Id);
    }

    private async Task MemberKinds()
    {
        var host = await Register();
        var coordinator = Registration(kind: MemberKind.Coordinator);
        _ids.Add(coordinator.Id);
        if (sqlite)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RegisterMemberAsync(coordinator));
            Assert.Contains("one host and no coordinator", error.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.RegisterMemberAsync(Registration()));
            await Read(host.Id);
            await store.UnregisterMemberAsync(host.Id);
            await Register(); // A stopped host releases the advertisement slot on the owned repository.
            return;
        }
        await store.RegisterMemberAsync(coordinator);
        var saved = await Read(coordinator.Id);
        Assert.Equal(MemberKind.Coordinator, saved.Kind);
        Assert.Equal(coordinator.Endpoint, saved.Endpoint);
        Assert.Equal(0, saved.Slots);
        await age(host.Id, TimeSpan.FromSeconds(16));
        Assert.True(await store.HeartbeatAsync(coordinator.Id, coordinator.Capabilities));
        Assert.DoesNotContain(await store.GetLiveMembersAsync(), m => m.Id == host.Id);
        await Read(coordinator.Id);
        await age(coordinator.Id, TimeSpan.FromSeconds(16));
        Assert.DoesNotContain(await store.GetLiveMembersAsync(), m => m.Id == coordinator.Id);
        Assert.True(await store.HeartbeatAsync(coordinator.Id, coordinator.Capabilities));
        await Read(coordinator.Id);
    }

    private async Task AtomicSnapshots()
    {
        static Dictionary<string, string> Snapshot(int generation) => Enumerable.Range(0, 24)
            .ToDictionary(i => $"cap-{i}", _ => generation.ToString());
        var member = await Register(Registration() with { Capabilities = Snapshot(0) });
        var writes = Task.Run(async () =>
        {
            for (var i = 1; i <= 16; i++) Assert.True(await store.HeartbeatAsync(member.Id, Snapshot(i)));
        });
        for (var i = 0; i < 16; i++)
        {
            var saved = await Read(member.Id);
            Assert.Equal(24, saved.Capabilities.Count);
            Assert.Single(saved.Capabilities.Values.Distinct());
        }
        await writes;
        Assert.All((await Read(member.Id)).Capabilities.Values, value => Assert.Equal("16", value));
    }

    private async Task CancelledAndInvalidWrites()
    {
        var member = await Register();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UnregisterMemberAsync(member.Id, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetLiveMembersAsync(cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.HeartbeatAsync(member.Id,
            new Dictionary<string, string>(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RegisterMemberAsync(member, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.HeartbeatAsync(member.Id,
            new Dictionary<string, string> { [" "] = "invalid" }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RegisterMemberAsync(member with
            { HeartbeatInterval = TimeSpan.FromMinutes(20) + TimeSpan.FromTicks(1) }));
        Assert.Equal(2, (await Read(member.Id)).Capabilities.Count);
    }

    private async Task ConfiguredInterval()
    {
        var member = await Register(Registration() with { HeartbeatInterval = TimeSpan.FromSeconds(20) });
        await age(member.Id, TimeSpan.FromSeconds(40));
        await Read(member.Id);
        await age(member.Id, TimeSpan.FromSeconds(21));
        Assert.DoesNotContain(await store.GetLiveMembersAsync(), m => m.Id == member.Id);
    }
}

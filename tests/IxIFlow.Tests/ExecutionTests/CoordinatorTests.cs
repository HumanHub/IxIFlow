using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using IxIFlow.Builders;
using IxIFlow.Coordinator;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Grpc.AspNetCore.Server.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "recovery")]
[Trait("Spec", "C-04")]
public sealed class CoordinatorTests
{
    [Fact]
    public void MatchingRequiresEveryDemandAndCapacity()
    {
        var time = new FakeTimeProvider();
        var matcher = new CoordinatorMatcher(new(), time);
        var gpu = new FakePeer(matcher, new() { ["gpu"] = "", ["region"] = "us" }, 8);
        var region = new FakePeer(matcher, new() { ["region"] = "eu" }, 8);
        var both = new FakePeer(matcher, new() { ["gpu"] = "", ["region"] = "eu" }, 0);
        matcher.Notify(Notice("work", [new("gpu"), new("region", "eu")]));
        Assert.Empty(gpu.Messages); Assert.Empty(region.Messages); Assert.Empty(both.Messages);
        Assert.Equal(1, matcher.HeldCount);
        matcher.Ready(both.Peer, 1);
        Assert.Equal("work", Assert.Single(Assert.Single(both.Messages).InstanceIds!));
        Assert.Equal(0, matcher.HeldCount);
    }

    [Fact]
    public void AffinityThenCapacityThenLeastRecentlyWoken()
    {
        var matcher = new CoordinatorMatcher(new(), new FakeTimeProvider());
        var first = new FakePeer(matcher, new(), 2);
        var second = new FakePeer(matcher, new(), 8);
        matcher.Notify(Notice("affinity") with { LastHostId = first.Peer.Hello.HostId });
        Assert.Single(first.Messages); Assert.Empty(second.Messages);
        matcher.Notify(Notice("capacity"));
        Assert.Equal("capacity", Assert.Single(Assert.Single(second.Messages).InstanceIds!));
        matcher.Ready(first.Peer, 2); matcher.Ready(second.Peer, 2);
        matcher.Notify(Notice("oldest"));
        Assert.Equal("oldest", Assert.Single(first.Messages.Last().InstanceIds!));
    }

    [Fact]
    public void HoldIsBoundedExpiresAndDeduplicatesWithoutExtendingExpiry()
    {
        var time = new FakeTimeProvider();
        var matcher = new CoordinatorMatcher(new() { NoticeCapacity = 1, NoticeLifetime = TimeSpan.FromSeconds(10) }, time);
        matcher.Notify(Notice("held")); matcher.Notify(Notice("overflow"));
        Assert.Equal(1, matcher.HeldCount);
        time.Advance(TimeSpan.FromSeconds(9)); matcher.Notify(Notice("held"));
        var peer = new FakePeer(matcher, new(), 8);
        Assert.Single(peer.Messages);
        matcher.Notify(Notice("held")); Assert.Equal(2, peer.Messages.Count);
        time.Advance(TimeSpan.FromSeconds(1)); matcher.Notify(Notice("held"));
        Assert.Equal(3, peer.Messages.Count);
        var empty = new CoordinatorMatcher(new(), time);
        empty.Notify(Notice("expired")); time.Advance(TimeSpan.FromSeconds(11));
        var late = new FakePeer(empty, new(), 8);
        Assert.Empty(late.Messages); Assert.Equal(0, empty.HeldCount);
    }

    [Fact]
    public void LaterNoticeForTheSameInstanceWakesAgainBeforeHoldExpiry()
    {
        var time = new FakeTimeProvider();
        var matcher = new CoordinatorMatcher(new() { NoticeCapacity = 1 }, time);
        var peer = new FakePeer(matcher, new(), 0);
        matcher.Notify(Notice("again"));
        matcher.Notify(Notice("again"));
        Assert.Equal(1, matcher.HeldCount);
        matcher.Ready(peer.Peer, 1);
        Assert.Single(peer.Messages);
        matcher.Notify(Notice("again"));
        Assert.Equal(1, matcher.HeldCount);
        matcher.Ready(peer.Peer, 1);
        Assert.Equal(2, peer.Messages.Count);
        Assert.Equal(0, matcher.HeldCount);
        Assert.Equal(time.Start, time.GetUtcNow());
    }

    [Fact]
    public void PinExpiresAndDisconnectedHostCannotReceiveAHint()
    {
        var time = new FakeTimeProvider();
        var matcher = new CoordinatorMatcher(new(), time);
        var peer = new FakePeer(matcher, new() { ["host"] = Guid.NewGuid().ToString("D") }, 8);
        matcher.Notify(Notice("pin", [new("host", Guid.NewGuid().ToString("D"), time.GetUtcNow().UtcDateTime.AddSeconds(1))]));
        Assert.Empty(peer.Messages);
        time.Advance(TimeSpan.FromSeconds(1)); matcher.Ready(peer.Peer, 8);
        Assert.Single(peer.Messages);
        matcher.Disconnect(peer.Peer); matcher.Notify(Notice("gone"));
        Assert.Single(peer.Messages); Assert.Equal(1, matcher.HeldCount);
    }

    [Fact]
    public void FullQueuesDropHintsWithoutBlockingAndTryTheNextCandidate()
    {
        var matcher = new CoordinatorMatcher(new(), new FakeTimeProvider());
        var full = new SendQueue<CoordinatorMessage>(1);
        Assert.True(full.TrySend(new(CoordinatorMessageKind.Heartbeat)));
        var blocked = matcher.Connect(new(Guid.NewGuid(), "full", new(), 8, LinkProtocol.Version), full.TrySend, () => { });
        matcher.Ready(blocked, 8);
        var peer = new FakePeer(matcher, new(), 1);
        matcher.Notify(Notice("next")); Assert.Single(peer.Messages);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10000; i++) Assert.False(full.TrySend(new(CoordinatorMessageKind.Heartbeat)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void AwaitIsInstalledBeforeItsAcknowledgementAndChangedIsAnIdOnly()
    {
        var matcher = new CoordinatorMatcher(new(), new FakeTimeProvider());
        var peer = new FakePeer(matcher, new(), 8);
        matcher.Subscribe(peer.Peer, "awaited", 17, true);
        Assert.Equal(17, Assert.Single(peer.Messages).Subscription);
        matcher.Notify(new("awaited", false, true, [], null));
        Assert.Equal(CoordinatorMessageKind.Changed, peer.Messages.Last().Kind);
        Assert.Equal("awaited", peer.Messages.Last().InstanceId);
        matcher.Subscribe(peer.Peer, "awaited", 0, false);
        matcher.Notify(new("awaited", false, true, [], null));
        Assert.Equal(2, peer.Messages.Count);
    }

    [Fact]
    public async Task AnEmptyWakePassRestoresCapacityBeforeTheNextNotice()
    {
        var time = new FakeTimeProvider();
        var inner = new InMemoryWorkflowStateRepository(time);
        var id = await SeedAsync(time, inner);
        var store = DispatchProxy.Create<IWorkflowStateRepository, CoordinatorCapabilityReviewTests.CountingForwarder>();
        var calls = (CoordinatorCapabilityReviewTests.CountingForwarder)(object)store;
        calls.Inner = inner;
        await using var server = await Server.StartAsync(time, inner);
        await using var worker = await Host.StartAsync(Services(time, store, true, server.Endpoint, slots: 1));
        await ConnectedAsync(worker, 1);
        await Task.Delay(50);
        var before = calls.Count("GetRunnableInstancesAsync");
        server.Matcher.Notify(Notice("stale"));
        await EventuallyAsync(() => calls.Count("GetRunnableInstancesAsync") > before);
        await Task.Delay(50);
        await AppendAsync(inner, id);
        server.Matcher.Notify(Notice(id, (await inner.GetWorkflowDemandsAsync(id)).ToArray()));
        await EventuallyAsync(async () => (await inner.GetWorkflowInstanceSummaryAsync(id))?.Status == WorkflowStatus.Completed);
        Assert.Equal(time.Start, time.GetUtcNow());
    }

    [Fact]
    public async Task AWakeClaimsAndAnAwaitCompletesWithAStationaryPollingClock()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var id = await SeedAsync(time, store);
        await using var server = await Server.StartAsync(time, store);
        await using var worker = await Host.StartAsync(Services(time, store, true, server.Endpoint));
        await using var caller = await Host.StartAsync(Services(time, store, false, server.Endpoint));
        await ConnectedAsync(worker, 1); await ConnectedAsync(caller, 1);
        var awaiting = caller.Client.AwaitAsync(id, WorkflowAwaitCondition.Finished);
        await EventuallyAsync(() => caller.Provider.GetRequiredService<WorkflowHost>().AwaitedInstances.Contains(id));
        // The writer cannot satisfy region=eu, so it must hand this event off through its notifier.
        var resumed = caller.Client.ResumeAsync(id, "approval", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, (await awaiting.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Equal(WorkflowExecutionStatus.Success, (await resumed.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Equal(time.Start, time.GetUtcNow());
    }

    [Fact]
    public async Task CompletionBetweenTheReadAndSubscriptionIsNotMissed()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var id = await SeedAsync(time, store);
        await using var server = await Server.StartAsync(time, store);
        await using var writer = Services(time, store, true); // foreground writer; no polling or link
        var services = Services(time, store, false, server.Endpoint);
        var workflowHost = services.GetRequiredService<WorkflowHost>();
        Assert.Equal(WorkflowExecutionStatus.Suspended, await services.GetRequiredService<IWorkflowClient>().GetStatusAsync(id));
        // Installed before the link hook: terminal commit and its change notice happen before Await
        // can be enqueued. No Changed can reach the new subscriber, so the post-ack read must find it.
        workflowHost.AwaitingChanged += (instance, watching) =>
        {
            if (!watching || instance != id) return;
            writer.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "approval", new Approval()).GetAwaiter().GetResult();
            server.Matcher.Notify(new(id, false, true, [], null));
        };
        await using var caller = await Host.StartAsync(services);
        await ConnectedAsync(caller, 1);
        var result = await caller.Client.AwaitAsync(id, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(time.Start, time.GetUtcNow());
    }

    [Fact]
    [Trait("Spec", "C-16")]
    public async Task LosingTheLastCoordinatorScansImmediatelyAndResumesTheStoreSource()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var id = await SeedAsync(time, store);
        await using var server = await Server.StartAsync(time, store);
        var source = new WakeSource();
        await using var worker = await Host.StartAsync(Services(time, store, true, server.Endpoint, source));
        await ConnectedAsync(worker, 1);
        await EventuallyAsync(() => source.Paused);
        await AppendAsync(store, id); // writer crashed after commit, before notice
        await server.StopAsync();
        await EventuallyAsync(async () => (await store.GetWorkflowInstanceAsync(id))!.Status == WorkflowStatus.Completed);
        await EventuallyAsync(() => source.Attempts >= 2 && !source.Paused);
        Assert.Equal(time.Start, time.GetUtcNow());
    }

    [Fact]
    [Trait("Spec", "C-16")]
    public async Task BackstopCoversAWriterCrashingBetweenCommitAndNotice()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var id = await SeedAsync(time, store);
        await using var server = await Server.StartAsync(time, store);
        await using var worker = await Host.StartAsync(Services(time, store, true, server.Endpoint));
        await ConnectedAsync(worker, 1);
        // Wait for connection-triggered scan and establish a barrier before the writer commits.
        await Task.Delay(100);
        await AppendAsync(store, id);
        Assert.Equal(WorkflowStatus.Suspended, (await store.GetWorkflowInstanceAsync(id))!.Status);
        time.Advance(TimeSpan.FromSeconds(10));
        await EventuallyAsync(async () => (await store.GetWorkflowInstanceAsync(id))!.Status == WorkflowStatus.Completed);
    }

    [Fact]
    [Trait("Spec", "C-15")]
    public async Task BothCoordinatorsCarryNoticesAndLosingOneKeepsTheOtherUsable()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var firstId = await SeedAsync(time, store); var secondId = await SeedAsync(time, store);
        await using var first = await Server.StartAsync(time, store);
        await using var second = await Server.StartAsync(time, store);
        await using var worker = await Host.StartAsync(Services(time, store, true, first.Endpoint));
        await using var caller = await Host.StartAsync(Services(time, store, false, second.Endpoint));
        // Registry discovery merges with the static endpoint and links both hosts to both services.
        await ConnectedAsync(worker, 2); await ConnectedAsync(caller, 2);
        var waiting = caller.Client.AwaitAsync(firstId, WorkflowAwaitCondition.Finished);
        await caller.Client.ResumeAsync(firstId, "approval", new Approval()).WaitAsync(TimeSpan.FromSeconds(10));
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        await first.StopAsync();
        await ConnectedAsync(worker, 1); await ConnectedAsync(caller, 1);
        Assert.True(worker.Provider.GetRequiredService<CoordinationState>().Connected);
        waiting = caller.Client.AwaitAsync(secondId, WorkflowAwaitCondition.Finished);
        await caller.Client.ResumeAsync(secondId, "approval", new Approval()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Success, (await waiting.WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }

    [Fact]
    [Trait("Spec", "C-16")]
    public async Task ASilentPeerIsDisconnectedWithinTheApplicationHeartbeatBound()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        await using var server = await Server.StartAsync(time, store);
        using var channel = GrpcChannel.ForAddress(server.Endpoint);
        using var call = channel.CreateCallInvoker().AsyncDuplexStreamingCall(LinkProtocol.Link, null, new());
        await call.RequestStream.WriteAsync(new(HostMessageKind.Hello, new(Guid.NewGuid(), "silent", new(), 1, LinkProtocol.Version)));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(CoordinatorMessageKind.Welcome, call.ResponseStream.Current.Kind);
        time.Advance(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<RpcException>(async () =>
        {
            while (await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))) { }
        });
    }

    [Fact]
    public async Task ProtocolMismatchIsRefusedBeforeAWelcome()
    {
        var time = new FakeTimeProvider();
        await using var server = await Server.StartAsync(time, new InMemoryWorkflowStateRepository(time));
        using var channel = GrpcChannel.ForAddress(server.Endpoint);
        using var call = channel.CreateCallInvoker().AsyncDuplexStreamingCall(LinkProtocol.Link, null, new());
        await call.RequestStream.WriteAsync(new(HostMessageKind.Hello, new(Guid.NewGuid(), "old", new(), 1, LinkProtocol.Version + 1)));
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
    }

    [Fact]
    [Trait("Spec", "C-16")]
    public async Task AHostDetectsASilentCoordinatorAndReconnectsWithBackoff()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;
        await using var server = await ScriptedServer.StartAsync(async (input, output, context) =>
        {
            Assert.True(await input.MoveNext(context.CancellationToken));
            Interlocked.Increment(ref attempts);
            await output.WriteAsync(new(CoordinatorMessageKind.Welcome, Guid.NewGuid(), LinkProtocol.Version));
            // Transport stays open and consumes requests, but the application never heartbeats.
            while (await input.MoveNext(context.CancellationToken)) { }
        });
        await using var host = await Host.StartAsync(Services(time, new InMemoryWorkflowStateRepository(time), false, server.Endpoint));
        await ConnectedAsync(host, 1);
        time.Advance(TimeSpan.FromSeconds(15));
        await ConnectedAsync(host, 0);
        await Task.Delay(50); // allow the failed call to settle and arm its fake-time retry
        Assert.Equal(1, Volatile.Read(ref attempts));
        time.Advance(TimeSpan.FromMilliseconds(999)); await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref attempts));
        time.Advance(TimeSpan.FromMilliseconds(1));
        await ConnectedAsync(host, 1);
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    [Trait("Spec", "C-16")]
    public async Task RefusedHandshakesNeverBackOffPollingAndRetriesGrow()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;
        await using var server = await ScriptedServer.StartAsync(async (input, output, context) =>
        {
            await input.MoveNext(context.CancellationToken);
            Interlocked.Increment(ref attempts);
            await output.WriteAsync(new(CoordinatorMessageKind.Welcome, Guid.NewGuid(), LinkProtocol.Version + 1));
        });
        await using var host = await Host.StartAsync(Services(time, new InMemoryWorkflowStateRepository(time), false, server.Endpoint));
        await EventuallyAsync(() => Volatile.Read(ref attempts) == 1); await Task.Delay(100);
        Assert.False(host.Provider.GetRequiredService<CoordinationState>().Connected);
        time.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => Volatile.Read(ref attempts) == 2); await Task.Delay(100);
        time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(50);
        Assert.Equal(2, Volatile.Read(ref attempts));
        time.Advance(TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => Volatile.Read(ref attempts) == 3);
        Assert.False(host.Provider.GetRequiredService<CoordinationState>().Connected);
    }

    [Fact]
    [Trait("Spec", "C-16")]
    public async Task ReconnectedSubscriptionsRereadAfterAcknowledgementWithoutAPoll()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var id = await SeedAsync(time, store);
        var firstAwait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = 0;
        await using var server = await ScriptedServer.StartAsync(async (input, output, context) =>
        {
            var number = Interlocked.Increment(ref connection);
            Assert.True(await input.MoveNext(context.CancellationToken));
            await output.WriteAsync(new(CoordinatorMessageKind.Welcome, Guid.NewGuid(), LinkProtocol.Version));
            while (await input.MoveNext(context.CancellationToken))
            {
                if (input.Current.Kind != HostMessageKind.Await) continue;
                await output.WriteAsync(new(CoordinatorMessageKind.Awaited, InstanceId: input.Current.InstanceId,
                    Subscription: input.Current.Subscription));
                if (number == 1) { firstAwait.TrySetResult(); await drop.Task.WaitAsync(context.CancellationToken); return; }
            }
        });
        await using var caller = await Host.StartAsync(Services(time, store, false, server.Endpoint,
            configure: o => o.ReconnectMinDelay = TimeSpan.FromMilliseconds(100)));
        await ConnectedAsync(caller, 1);
        var awaited = caller.Client.AwaitAsync(id, WorkflowAwaitCondition.Finished);
        await firstAwait.Task.WaitAsync(TimeSpan.FromSeconds(10));
        drop.TrySetResult(); await ConnectedAsync(caller, 0); await Task.Delay(100);
        // The completion happens during the outage. No Changed is sent on either session.
        await using (var writer = Services(time, store, true))
            await writer.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "approval", new Approval());
        Assert.False(awaited.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(100)); // reconnect, before the one-second fallback poll
        Assert.Equal(WorkflowExecutionStatus.Success, (await awaited.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Equal(2, Volatile.Read(ref connection));
    }

    [Fact]
    [Trait("Spec", "C-16")]
    public async Task MissingSubscriptionAcknowledgementCannotSuppressTheBackstop()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var id = await SeedAsync(time, store);
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beats = 0;
        await using var server = await ScriptedServer.StartAsync(async (input, output, context) =>
        {
            await input.MoveNext(context.CancellationToken);
            await output.WriteAsync(new(CoordinatorMessageKind.Welcome, Guid.NewGuid(), LinkProtocol.Version));
            while (await input.MoveNext(context.CancellationToken))
            {
                if (input.Current.Kind == HostMessageKind.Await) subscribed.TrySetResult();
                if (input.Current.Kind == HostMessageKind.Heartbeat)
                { await output.WriteAsync(new(CoordinatorMessageKind.Heartbeat)); Interlocked.Increment(ref beats); }
            }
        });
        await using var caller = await Host.StartAsync(Services(time, store, false, server.Endpoint));
        await ConnectedAsync(caller, 1);
        var awaited = caller.Client.AwaitAsync(id, WorkflowAwaitCondition.Finished);
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var writer = Services(time, store, true))
            await writer.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "approval", new Approval());
        for (var i = 1; i <= 2; i++)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            var expected = i; await EventuallyAsync(() => Volatile.Read(ref beats) >= expected);
            await Task.Delay(20);
            Assert.False(awaited.IsCompleted);
        }
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowExecutionStatus.Success, (await awaited.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        await ConnectedAsync(caller, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Spec", "C-15")]
    public async Task StaleDiscoveredRowsAreDroppedButStaticEndpointsRemain(bool isStatic)
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        // The coordinator's process clock stops, while store time and discovery continue.
        await using var server = await Server.StartAsync(new FakeTimeProvider(), store, o => o.HeartbeatInterval = TimeSpan.FromSeconds(1));
        await using var host = await Host.StartAsync(Services(time, store, true, isStatic ? server.Endpoint : null, discover: true));
        await ConnectedAsync(host, 1);
        time.Advance(TimeSpan.FromSeconds(5));
        if (isStatic)
        { await Task.Delay(100); Assert.Equal(1, host.Provider.GetRequiredService<CoordinatorLinks>().ConnectedCount); }
        else await ConnectedAsync(host, 0);
    }

    [Fact]
    public void ReconnectBackoffIsExponentialAndBounded()
    {
        var options = new CoordinatorClientOptions();
        Assert.Equal(new[] { 1d, 2d, 4d, 8d, 16d, 30d, 30d },
            Enumerable.Range(0, 7).Select(i => CoordinatorLink.ReconnectDelay(options, i).TotalSeconds));
    }

    [Theory]
    [InlineData(false, "http://127.0.0.1:5088")]
    [InlineData(true, "http://192.0.2.1:5088")]
    [InlineData(true, "http://0.0.0.0:5088")]
    public async Task UntrustedPlaintextIsRefusedAtStartup(bool allow, string endpoint)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow().UseCoordinators(o => { o.AllowInsecure = allow; o.Coordinators.Add(new(endpoint)); });
        await using var provider = services.BuildServiceProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<CoordinatorLinks>().StartAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task SqliteRefusesBothCoordinatorRolesAtStartup(bool service, bool allowInsecure)
    {
        var path = Path.Combine(Path.GetTempPath(), "c6-sqlite-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var services = new ServiceCollection().AddLogging();
            var builder = services.AddIxIFlow().UseSqlite("Data Source=" + path);
            if (service) builder.AddCoordinator(o => o.AllowInsecure = allowInsecure);
            else builder.UseCoordinators(o => o.AllowInsecure = allowInsecure);
            await using var provider = services.BuildServiceProvider();
            IHostedService role = service ? provider.GetRequiredService<CoordinatorService>() : provider.GetRequiredService<CoordinatorLinks>();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => role.StartAsync(CancellationToken.None));
            Assert.Contains("SQLite accepts one host and no coordinator", error.Message);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", ".ixiflow-owner" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static InstanceNotice Notice(string id, WorkflowDemand[]? demands = null) => new(id, true, false, demands ?? [], null);
    private sealed class FakePeer
    {
        internal readonly List<CoordinatorMessage> Messages = [];
        internal CoordinatorMatcher.Peer Peer { get; }
        internal FakePeer(CoordinatorMatcher matcher, Dictionary<string, string> capabilities, int slots)
        {
            Peer = matcher.Connect(new(Guid.NewGuid(), "fake", capabilities, 8, LinkProtocol.Version),
                message => { Messages.Add(message); return true; }, () => { });
            matcher.Ready(Peer, slots);
        }
    }
    private static WorkflowDefinition Definition() => Workflow.Create<Data>("CoordinatorWake")
        .Demand("region", "eu").Step<Noop>().WaitFor<Approval>("approval").Step<Noop>().Build();
    private static ServiceProvider Services(TimeProvider time, IWorkflowStateRepository store, bool eligible,
        Uri? endpoint = null, IRunnableWakeSource? source = null, bool discover = false,
        Action<CoordinatorClientOptions>? configure = null, int slots = HostSettings.DefaultSlots)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(time); services.AddSingleton(store);
        if (source != null) services.AddSingleton(source);
        var builder = services.AddIxIFlow(o =>
        { o.PollInterval = TimeSpan.FromMinutes(1); o.Slots = slots; o.Capabilities["region"] = eligible ? "eu" : "us"; });
        if (endpoint != null || discover) builder.UseCoordinators(o => { o.AllowInsecure = true; if (endpoint != null) o.Coordinators.Add(endpoint); configure?.Invoke(o); });
        services.AddTransient<Noop>();
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(Definition()).GetAwaiter().GetResult();
        return provider;
    }
    private static async Task<string> SeedAsync(TimeProvider time, IWorkflowStateRepository store)
    {
        await using var provider = Services(time, store, true);
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(Definition(), new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        return result.InstanceId;
    }
    private static async Task AppendAsync(IWorkflowStateRepository store, string id) =>
        Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(new(id, Guid.NewGuid().ToString("N"),
            typeof(Approval).AssemblyQualifiedName!, JsonSerializer.Serialize(new Approval()), Key: "approval"))).Status);
    private static Task ConnectedAsync(Host host, int count) => EventuallyAsync(() => host.Provider.GetRequiredService<CoordinatorLinks>().ConnectedCount == count);
    private static Task EventuallyAsync(Func<bool> predicate) => EventuallyAsync(() => Task.FromResult(predicate()));
    private static async Task EventuallyAsync(Func<Task<bool>> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!await predicate()) { Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Condition did not become true"); await Task.Delay(10); }
    }
    private sealed class Host(ServiceProvider provider, IHostedService[] hosted) : IAsyncDisposable
    {
        internal ServiceProvider Provider => provider;
        internal IWorkflowClient Client => provider.GetRequiredService<IWorkflowClient>();
        internal static async Task<Host> StartAsync(ServiceProvider provider)
        {
            var hosted = provider.GetServices<IHostedService>().ToArray();
            foreach (var service in hosted) await service.StartAsync(CancellationToken.None);
            return new(provider, hosted);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var service in hosted.Reverse()) await service.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }
    private sealed class Server(ServiceProvider provider, CoordinatorService service) : IAsyncDisposable
    {
        private bool _stopped;
        internal Uri Endpoint => service.Endpoint!;
        internal CoordinatorMatcher Matcher => provider.GetRequiredService<CoordinatorMatcher>();
        internal static async Task<Server> StartAsync(TimeProvider time, IWorkflowStateRepository store, Action<CoordinatorOptions>? configure = null)
        {
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton(time);
            var guard = DispatchProxy.Create<IWorkflowStateRepository, RegistryOnly>();
            ((RegistryOnly)(object)guard).Inner = store;
            services.AddSingleton(guard);
            services.AddIxIFlow().AddCoordinator(o => { o.AllowInsecure = true; o.ListenEndpoint = new("http://127.0.0.1:0"); configure?.Invoke(o); });
            var provider = services.BuildServiceProvider();
            var service = provider.GetRequiredService<CoordinatorService>();
            await service.StartAsync(CancellationToken.None);
            return new(provider, service);
        }
        internal async Task StopAsync() { if (_stopped) return; _stopped = true; await service.StopAsync(CancellationToken.None); }
        public async ValueTask DisposeAsync() { await StopAsync(); await provider.DisposeAsync(); }
    }
    private sealed class ScriptedRpc(Func<IAsyncStreamReader<HostMessage>, IServerStreamWriter<CoordinatorMessage>, ServerCallContext, Task> link)
    {
        internal Task Link(IAsyncStreamReader<HostMessage> input, IServerStreamWriter<CoordinatorMessage> output, ServerCallContext context) => link(input, output, context);
    }
    private sealed class ScriptedMethods : IServiceMethodProvider<ScriptedRpc>
    {
        public void OnServiceMethodDiscovery(ServiceMethodProviderContext<ScriptedRpc> context) =>
            context.AddDuplexStreamingMethod(LinkProtocol.Link, [], (service, input, output, call) => service.Link(input, output, call));
    }
    private sealed class ScriptedServer(WebApplication app, Uri endpoint) : IAsyncDisposable
    {
        internal Uri Endpoint => endpoint;
        internal static async Task<ScriptedServer> StartAsync(Func<IAsyncStreamReader<HostMessage>, IServerStreamWriter<CoordinatorMessage>, ServerCallContext, Task> link)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(server => server.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(new ScriptedRpc(link));
            builder.Services.AddSingleton<IServiceMethodProvider<ScriptedRpc>, ScriptedMethods>();
            var app = builder.Build(); app.MapGrpcService<ScriptedRpc>(); await app.StartAsync();
            return new(app, new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()));
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
    public class RegistryOnly : DispatchProxy
    {
        internal IWorkflowStateRepository Inner = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Contains(targetMethod!.Name, new[] { "RegisterMemberAsync", "HeartbeatAsync", "UnregisterMemberAsync" });
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        }
    }
    private sealed class WakeSource : IRunnableWakeSource
    {
        internal int Attempts; internal volatile bool Paused;
        public async Task ListenAsync(Action wake, CancellationToken token)
        {
            Interlocked.Increment(ref Attempts); Paused = false;
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Paused = true; }
        }
    }
    public sealed class Data;
    public sealed class Approval;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

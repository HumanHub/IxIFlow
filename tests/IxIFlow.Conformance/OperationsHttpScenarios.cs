using System.Net;
using System.Net.Http.Json;
using IxIFlow.AspNetCore;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.Conformance.OperationsHttpFactory;

namespace IxIFlow.Tests.Conformance;

public sealed class OperationsHttpScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task SearchPagesCanBeContinuedOnAnotherHost(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var registry = new WorkflowVersionRegistry();
        await using var first = new OperationsHttpFactory(store.NewRepository(), registry);
        // SQLite has one owning host. Its HTTP page contract still runs in this profile.
        await using var second = profile == StoreProfiles.LocalSqlite ? null : new OperationsHttpFactory(store.NewRepository(), registry);
        using var left = first.Operator();
        using var right = (second ?? first).Operator();
        var ids = new List<string>();
        for (var i = 0; i < 3; i++) ids.Add((await first.Engine.RunAsync(Waiting(), new Data())).InstanceId);
        var page = await left.GetFromJsonAsync<InstancePage>("/ops/instances?limit=1");
        var found = new List<string> { Assert.Single(page!.Items).InstanceId };
        while (page.NextCursor != null)
        {
            page = await right.GetFromJsonAsync<InstancePage>("/ops/instances?limit=1&after=" + Uri.EscapeDataString(page.NextCursor));
            found.Add(Assert.Single(page!.Items).InstanceId);
        }
        Assert.Equal(ids.Order(), found.Order());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task SuspendedInstancesExposeWaitsWithoutCapabilities(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var factory = new OperationsHttpFactory(store.NewRepository());
        using var http = factory.Operator("read");
        var id = (await factory.Engine.RunAsync(Waiting(), new Data())).InstanceId;
        using var response = await http.GetAsync($"/ops/instances/{id}/waits");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var wait = Assert.Single((await response.Content.ReadFromJsonAsync<PendingWait[]>())!);
        Assert.Equal("approval", wait.Key);
        Assert.DoesNotContain("waitId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        var secret = Assert.Single(await factory.Engine.GetOpenWaitsAsync(new(InstanceId: id)));
        Assert.DoesNotContain(secret.WaitId, json);
        Assert.DoesNotContain(secret.Token, json);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task HttpTerminationIsAcknowledgedByTheRunningOwner(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var registry = new WorkflowVersionRegistry();
        await using var owner = new OperationsHttpFactory(store.NewRepository(), registry);
        await using var other = profile == StoreProfiles.LocalSqlite ? null : new OperationsHttpFactory(store.NewRepository(), registry);
        using var http = (other ?? owner).Operator();
        var id = Guid.NewGuid().ToString("N");
        var run = owner.Engine.RunAsync(Workflow.Create<Data>("HttpRunningTerminate").Step<Block>().Step<Noop>().Build(), new Data(), new WorkflowOptions { InstanceId = id });
        try
        {
            await owner.ActivityProbe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var response = await http.PostAsJsonAsync($"/ops/instances/{id}/terminate", new { reasonCode = "operator" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            owner.ActivityProbe.Exit.TrySetResult();
            Assert.Equal(WorkflowExecutionStatus.Terminated, (await run.WaitAsync(TimeSpan.FromSeconds(30))).Status);
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((await http.GetFromJsonAsync<InstanceSummary>($"/ops/instances/{id}", patience.Token))!.Status != WorkflowStatus.Terminated)
                await Task.Delay(30, patience.Token);
        }
        finally { owner.ActivityProbe.Exit.TrySetResult(); }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task QuarantinedInstancesAreListedAndReleased(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        await using var factory = new OperationsHttpFactory(repository) { Wait = TimeSpan.FromSeconds(10) };
        using var http = factory.Operator();
        await OperationsHttpTests.SeedQuarantine(factory, repository, Workflow.Create<Data>("HttpQuarantine").Step<Noop>().Build(), "quarantined");
        var page = await http.GetFromJsonAsync<InstancePage>("/ops/instances?status=NeedsResolution&stop=Quarantine");
        Assert.Equal(3, Assert.Single(page!.Items).CrashCount);
        using var response = await http.PostAsync("/ops/instances/quarantined/release", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<WorkflowOperationResult>())!;
        Assert.True(result.Status == WorkflowExecutionStatus.Success, System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Empty((await http.GetFromJsonAsync<InstancePage>("/ops/instances?stop=Quarantine"))!.Items);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task GivenUpDeliveryIsRetriedAndDelivered(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var publisher = new Publisher();
        await using var factory = new OperationsHttpFactory(repository)
        {
            ConfigureServices = services => services.AddSingleton<IOutboxPublisher>(publisher)
        };
        using var http = factory.Operator();
        var id = (await factory.Engine.RunAsync(Workflow.Create<Data>("HttpOutbox").Step<Noop>().Build(), new Data())).InstanceId;
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        WorkflowDeliveryResponse? delivery = null;
        while (delivery == null)
        {
            delivery = (await http.GetFromJsonAsync<WorkflowDeliveryPage>("/ops/outbox/deliveries?status=GivenUp&instanceId=" + id, patience.Token))!.Items.SingleOrDefault();
            if (delivery == null) await Task.Delay(30, patience.Token);
        }
        Assert.Equal(OutboxGivenUpReason.Exhausted, delivery.GivenUpReason);
        publisher.Deliver = true;
        using var response = await http.PostAsync($"/ops/outbox/deliveries/{delivery.Event.Id}/{publisher.Name}/retry", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        while ((await repository.GetDeliveryAsync(delivery.Event.Id, publisher.Name))!.Status != OutboxDeliveryStatus.Delivered)
            await Task.Delay(30, patience.Token);
        var delivered = (await repository.GetDeliveryAsync(delivery.Event.Id, publisher.Name))!;
        Assert.Equal("operator", delivered.RetriedBy);
        Assert.Equal(1, delivered.Attempts);
    }

    private sealed class Publisher : IOutboxPublisher
    {
        public string Name => "http-publisher";
        public IReadOnlyCollection<string> EventTypes => [OutboxEvent.WorkflowCompleted];
        public volatile bool Deliver;
        public Task<OutboxPublishResult> PublishAsync(OutboxPublishContext delivery, CancellationToken cancellationToken) =>
            Task.FromResult(Deliver ? OutboxPublishResult.Delivered() : OutboxPublishResult.GiveUp("unavailable"));
    }
}

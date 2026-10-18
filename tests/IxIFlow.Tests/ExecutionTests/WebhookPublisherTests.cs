using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Webhooks;
using Microsoft.Extensions.Time.Testing;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "D-12")]
[Trait("Feature", "durability")]
public sealed class WebhookPublisherTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static WebhookEndpoint Endpoint => new("orders", new Uri("https://receiver.example/events"),
        "whsec_" + Convert.ToBase64String(Key), [OutboxEvent.WorkflowCompleted]);
    private static OutboxPublishContext Delivery(int attempts = 1) => new(
        new OutboxEvent("fixed_event_id", OutboxEvent.WorkflowCompleted, "order-42", "Orders", 1, "correlation",
            new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc), "{\"status\":\"Completed\"}"),
        attempts, DateTime.UnixEpoch);

    [Fact]
    public async Task HeadersAndCloudEventsBodyVerifyWithAnIndependentStandardWebhooksImplementation()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        using var handler = new Handler(HttpStatusCode.Accepted);
        var http = new Factory(handler);
        var publisher = new WebhookOutboxPublisher(Endpoint, http, time, new FixedRandom(0));
        Assert.Equal(OutboxPublishOutcome.Delivered, (await publisher.PublishAsync(Delivery() with { StoreNowUtc = time.GetUtcNow().UtcDateTime }, default)).Outcome);
        var sent = Assert.Single(handler.Sent);
        Assert.Equal("fixed_event_id", sent.Id);
        Assert.Equal(time.GetUtcNow().ToUnixTimeSeconds().ToString(), sent.Timestamp);
        Assert.Equal("application/cloudevents+json", sent.ContentType);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(Endpoint.Url, sent.Url);
        // Independent verifier: instance HMAC API, decode the signature, sign the exact captured wire body.
        using var verifier = new HMACSHA256(Convert.FromBase64String(Endpoint.Secret.Split('_', 2)[1]));
        var expected = verifier.ComputeHash(Encoding.UTF8.GetBytes(string.Join('.', sent.Id, sent.Timestamp, sent.Body)));
        var pieces = sent.Signature.Split(',');
        Assert.Equal("v1", pieces[0]);
        Assert.True(CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(pieces[1])));
        using var json = JsonDocument.Parse(sent.Body);
        var e = json.RootElement;
        Assert.Equal("1.0", e.GetProperty("specversion").GetString());
        Assert.Equal(sent.Id, e.GetProperty("id").GetString());
        Assert.Equal("urn:ixiflow:workflows", e.GetProperty("source").GetString());
        Assert.Equal(OutboxEvent.WorkflowCompleted, e.GetProperty("type").GetString());
        Assert.Equal("order-42", e.GetProperty("subject").GetString());
        Assert.Equal(Delivery().Event.OccurredAtUtc, e.GetProperty("time").GetDateTime());
        Assert.Equal("application/json", e.GetProperty("datacontenttype").GetString());
        Assert.Equal("Completed", e.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task RetriesKeepTheIdAndBodyAndFollowTheStandardSchedule()
    {
        var time = new FakeTimeProvider();
        using var handler = new Handler(HttpStatusCode.ServiceUnavailable);
        var http = new Factory(handler);
        var publisher = new WebhookOutboxPublisher(Endpoint, http, time, new FixedRandom(0));
        double[] seconds = [5, 300, 1800, 7200, 18000, 36000, 50400, 72000, 86400];
        for (var i = 0; i < seconds.Length; i++)
        {
            var result = await publisher.PublishAsync(Delivery(i + 1) with { StoreNowUtc = time.GetUtcNow().UtcDateTime }, default);
            Assert.Equal(OutboxPublishOutcome.RetryLater, result.Outcome);
            Assert.Equal(TimeSpan.FromSeconds(seconds[i]), result.RetryAfter);
            time.Advance(result.RetryAfter!.Value);
        }
        Assert.Equal(OutboxPublishOutcome.GiveUp, (await publisher.PublishAsync(Delivery(10) with { StoreNowUtc = time.GetUtcNow().UtcDateTime }, default)).Outcome);
        Assert.Single(handler.Sent.Select(x => x.Id).Distinct());
        Assert.Single(handler.Sent.Select(x => x.Body).Distinct());
        Assert.Equal(10, handler.Sent.Select(x => x.Timestamp).Distinct().Count());
        Assert.Equal(10, http.Names.Count);
        Assert.All(http.Names, name => Assert.Equal("IxIFlow.Webhooks.orders", name));
    }

    [Theory]
    [InlineData(200, true)] [InlineData(204, true)] [InlineData(299, true)]
    [InlineData(301, false)] [InlineData(400, false)] [InlineData(429, false)] [InlineData(500, false)]
    public async Task Only2xxIsDelivered(int status, bool delivered)
    {
        using var handler = new Handler((HttpStatusCode)status);
        var http = new Factory(handler);
        var result = await new WebhookOutboxPublisher(Endpoint, http).PublishAsync(Delivery(), default);
        Assert.Equal(delivered ? OutboxPublishOutcome.Delivered : OutboxPublishOutcome.RetryLater, result.Outcome);
    }

    [Fact]
    public async Task GoneDisablesTheEndpointInTheStoreAndStopsFutureDelivery()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        using var handler = new Handler(HttpStatusCode.Gone);
        var http = new Factory(handler);
        var publisher = new WebhookOutboxPublisher(Endpoint, http, time, new FixedRandom(0));
        await store.RegisterPublisherAsync(publisher.Name, publisher.EventTypes);
        await OutboxStoreContract.CompleteAsync(store);
        var claimed = Assert.Single(await store.ClaimDeliveriesAsync(publisher.Name, "owner", TimeSpan.FromMinutes(1), 1));
        var result = await publisher.PublishAsync(new(claimed.Event, claimed.Attempts, claimed.StoreNowUtc), default);
        Assert.True(result.DisablePublisher);
        Assert.True(await store.SettleDeliveryAsync(claimed.Event.Id, publisher.Name, "owner", result));
        await OutboxStoreContract.CompleteAsync(store);
        var restarted = new WebhookOutboxPublisher(Endpoint, http, time, new FixedRandom(0));
        await store.RegisterPublisherAsync(restarted.Name, restarted.EventTypes);
        Assert.Empty(await store.ClaimDeliveriesAsync(restarted.Name, "restarted", TimeSpan.FromMinutes(1), 100));
        Assert.NotNull((await store.GetPublisherAsync(publisher.Name))!.DisabledAtUtc);
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task RetryAfterCanExtendTheSchedule()
    {
        using var handler = new Handler(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(8));
        var http = new Factory(handler);
        var result = await new WebhookOutboxPublisher(Endpoint, http).PublishAsync(Delivery(), default);
        Assert.Equal(TimeSpan.FromMinutes(8), result.RetryAfter);
    }

    [Fact]
    public async Task AStoreRetryKeepsTheOriginalIdAndUsesFreshStoreTime()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        using var handler = new Handler(HttpStatusCode.InternalServerError);
        var http = new Factory(handler);
        // A skewed host clock must not decide the attempt timestamp or the next attempt deadline.
        var hostTime = new FakeTimeProvider(time.GetUtcNow().AddYears(10));
        var publisher = new WebhookOutboxPublisher(Endpoint, http, hostTime, new FixedRandom(0));
        await store.RegisterPublisherAsync(publisher.Name, publisher.EventTypes);
        await OutboxStoreContract.CompleteAsync(store);
        var first = Assert.Single(await store.ClaimDeliveriesAsync(publisher.Name, "one", TimeSpan.FromMinutes(1), 1));
        var failure = await publisher.PublishAsync(new(first.Event, first.Attempts, first.StoreNowUtc), default);
        Assert.True(await store.SettleDeliveryAsync(first.Event.Id, publisher.Name, "one", failure));
        time.Advance(TimeSpan.FromSeconds(5));
        var retry = Assert.Single(await store.ClaimDeliveriesAsync(publisher.Name, "two", TimeSpan.FromMinutes(1), 1));
        handler.Status = HttpStatusCode.OK;
        Assert.Equal(OutboxPublishOutcome.Delivered, (await publisher.PublishAsync(new(retry.Event, retry.Attempts, retry.StoreNowUtc), default)).Outcome);
        Assert.Equal(handler.Sent[0].Id, handler.Sent[1].Id);
        Assert.Equal(first.Event.Id, handler.Sent[1].Id);
        Assert.Equal(handler.Sent[0].Body, handler.Sent[1].Body);
        Assert.Equal(new DateTimeOffset(first.StoreNowUtc).ToUnixTimeSeconds().ToString(), handler.Sent[0].Timestamp);
        Assert.Equal(time.GetUtcNow().ToUnixTimeSeconds().ToString(), handler.Sent[1].Timestamp);
    }

    [Fact]
    public async Task SubscriptionsFilterBeforeCreatingDeliveriesAndExplicitReplayBackfillsRetainedEvents()
    {
        var store = new InMemoryWorkflowStateRepository(new FakeTimeProvider());
        await store.RegisterPublisherAsync("unrelated", ["io.example.other"]);
        await OutboxStoreContract.CompleteAsync(store);
        Assert.Empty(await store.ClaimDeliveriesAsync("unrelated", "owner", TimeSpan.FromMinutes(1), 100));
        await store.RegisterPublisherAsync("new", [OutboxEvent.WorkflowCompleted], replayRetained: true);
        var item = Assert.Single(await store.ClaimDeliveriesAsync("new", "owner", TimeSpan.FromMinutes(1), 100));
        Assert.Equal(OutboxEvent.WorkflowCompleted, item.Event.Type);
        Assert.True(await store.SettleDeliveryAsync(item.Event.Id, "new", "owner", OutboxPublishResult.Delivered()));
        await store.RegisterPublisherAsync("new", [OutboxEvent.WorkflowCompleted]);
        Assert.Empty(await store.ClaimDeliveriesAsync("new", "again", TimeSpan.FromMinutes(1), 100));
    }

    [Theory]
    [InlineData(1, 0.25)]
    [InlineData(9, 60)]
    public async Task RetryJitterIsInjectedAndBounded(int attempt, double maximumSeconds)
    {
        using var handler = new Handler(HttpStatusCode.ServiceUnavailable);
        var factory = new Factory(handler);
        var minimum = await new WebhookOutboxPublisher(Endpoint, factory, random: new FixedRandom(0))
            .PublishAsync(Delivery(attempt), default);
        var maximum = await new WebhookOutboxPublisher(Endpoint, factory, random: new FixedRandom(1))
            .PublishAsync(Delivery(attempt), default);
        Assert.Equal(TimeSpan.FromSeconds(maximumSeconds), maximum.RetryAfter - minimum.RetryAfter);
        Assert.Equal(["IxIFlow.Webhooks.orders", "IxIFlow.Webhooks.orders"], factory.Names);
    }

    [Fact]
    public void CompletionSummaryHasAnErrorKindWithoutExceptionTextAndEndpointRedactsItsSecret()
    {
        var item = OutboxEvent.Completion(new WorkflowInstance { Status = WorkflowStatus.Failed,
            LastError = "Password=super-secret", LastErrorStackTrace = "private stack" }, DateTime.UtcNow);
        using var json = JsonDocument.Parse(item.DataJson);
        Assert.Equal("Failed", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("WorkflowFailure", json.RootElement.GetProperty("errorKind").GetString());
        Assert.DoesNotContain("super-secret", item.DataJson);
        Assert.False(json.RootElement.TryGetProperty("error", out _));
        Assert.DoesNotContain(Endpoint.Secret, Endpoint.ToString());
    }

    [Fact]
    public async Task BuilderConfiguresTheNamedHandlerWithoutRedirectsAndWithConnectionRotation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow().AddWebhook(Endpoint);
        await using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("IxIFlow.Webhooks.orders");
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.AllowAutoRedirect);
        Assert.Equal(TimeSpan.FromMinutes(5), sockets.PooledConnectionLifetime);
    }

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];
        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed record SentRequest(string Id, string Timestamp, string Signature, string Body,
        string? ContentType, HttpMethod Method, Uri? Url);
    private sealed class Handler(HttpStatusCode status, TimeSpan? retryAfter = null) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public List<SentRequest> Sent { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sent.Add(new SentRequest(request.Headers.GetValues("webhook-id").Single(),
                request.Headers.GetValues("webhook-timestamp").Single(), request.Headers.GetValues("webhook-signature").Single(),
                await request.Content!.ReadAsStringAsync(token), request.Content.Headers.ContentType?.MediaType,
                request.Method, request.RequestUri));
            var response = new HttpResponseMessage(Status);
            if (retryAfter != null)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter.Value);
            return response;
        }
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using IxIFlow.Core;

namespace IxIFlow.Webhooks;

/// <summary>Name is the stable endpoint identity. A 410 disables it durably under that name.</summary>
public sealed record WebhookEndpoint(string Name, Uri Url, string Secret, IReadOnlyCollection<string> EventTypes)
{
    public override string ToString() => $"WebhookEndpoint {Name} ({Url}), Secret = [REDACTED]";
}

/// <summary>Standard Webhooks v1 signing, with a CloudEvents 1.0 structured JSON body.</summary>
public sealed class WebhookOutboxPublisher : IOutboxPublisher
{
    private readonly WebhookEndpoint _endpoint;
    private readonly IHttpClientFactory _http;
    private readonly Random _random;
    private readonly TimeProvider _time;
    private readonly byte[] _secret;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2),
        TimeSpan.FromHours(5), TimeSpan.FromHours(10), TimeSpan.FromHours(14), TimeSpan.FromHours(20), TimeSpan.FromHours(24)
    ];

    public WebhookOutboxPublisher(WebhookEndpoint endpoint, IHttpClientFactory http, TimeProvider? timeProvider = null,
        Random? random = null)
    {
        _secret = Validate(endpoint);
        _endpoint = endpoint with { EventTypes = Array.AsReadOnly(endpoint.EventTypes.ToArray()) };
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _time = timeProvider ?? TimeProvider.System;
        _random = random ?? Random.Shared;
    }

    internal static string ClientName(WebhookEndpoint endpoint) => "IxIFlow.Webhooks." + endpoint.Name;

    internal static byte[] Validate(WebhookEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Name);
        ArgumentNullException.ThrowIfNull(endpoint.Url);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Secret);
        if (endpoint.Name.Length > 92 || !endpoint.Url.IsAbsoluteUri ||
            endpoint.Url.Scheme is not ("https" or "http") || endpoint.Url.UserInfo.Length != 0)
            throw new ArgumentException("A webhook needs a name of at most 92 characters and an absolute HTTP(S) URL without credentials");
        ArgumentNullException.ThrowIfNull(endpoint.EventTypes);
        if (endpoint.EventTypes.Count == 0 || endpoint.EventTypes.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A webhook needs at least one event type");
        return StandardWebhookSignature.DecodeSecret(endpoint.Secret);
    }

    public string Name => "webhook:" + _endpoint.Name;
    public IReadOnlyCollection<string> EventTypes => _endpoint.EventTypes;

    public async Task<OutboxPublishResult> PublishAsync(OutboxPublishContext delivery, CancellationToken cancellationToken)
    {
        var item = delivery.Event;
        var started = _time.GetTimestamp();
        var storeNow = new DateTimeOffset(DateTime.SpecifyKind(delivery.StoreNowUtc, DateTimeKind.Utc));
        if (!EventTypes.Contains(item.Type, StringComparer.Ordinal))
            return OutboxPublishResult.Delivered();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            specversion = "1.0", id = item.Id, source = "urn:ixiflow:workflows", type = item.Type,
            subject = item.InstanceId, time = item.OccurredAtUtc, datacontenttype = "application/json",
            data = JsonSerializer.Deserialize<JsonElement>(item.DataJson)
        });
        var timestamp = storeNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = StandardWebhookSignature.Sign(_secret, item.Id, timestamp, body);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint.Url);
        request.Headers.Add("webhook-id", item.Id);
        request.Headers.Add("webhook-timestamp", timestamp);
        request.Headers.Add("webhook-signature", signature);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/cloudevents+json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var http = _http.CreateClient(ClientName(_endpoint));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            if (response.IsSuccessStatusCode)
                return OutboxPublishResult.Delivered();
            if (response.StatusCode == HttpStatusCode.Gone)
                return OutboxPublishResult.GiveUp("HTTP 410 Gone", disablePublisher: true);
            var retryAfter = response.Headers.RetryAfter;
            var requested = retryAfter?.Delta ?? (retryAfter?.Date - (storeNow + _time.GetElapsedTime(started)));
            return Retry(delivery.Attempts, "HTTP " + (int)response.StatusCode, requested);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Retry(delivery.Attempts, "Webhook request timed out");
        }
        catch (HttpRequestException error) { return Retry(delivery.Attempts, error.Message); }
    }

    private OutboxPublishResult Retry(int attempts, string error, TimeSpan? requested = null)
    {
        if (attempts > RetryDelays.Length)
            return OutboxPublishResult.GiveUp(error);
        var delay = RetryDelays[Math.Max(0, attempts - 1)];
        // Positive jitter of at most 5%, capped at one minute, never shortens Retry-After.
        delay += TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 0.05) * _random.NextDouble());
        if (requested > delay)
            delay = requested.Value > TimeSpan.FromDays(30) ? TimeSpan.FromDays(30) : requested.Value;
        return OutboxPublishResult.RetryLater(error, delay);
    }
}

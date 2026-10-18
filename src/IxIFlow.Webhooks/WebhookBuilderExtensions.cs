using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Webhooks;

public static class WebhookBuilderExtensions
{
    /// <summary>Adds an independently retried endpoint. Redirects are failures; update the configured URL instead.</summary>
    public static IIxIFlowBuilder AddWebhook(this IIxIFlowBuilder builder, WebhookEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(endpoint.EventTypes);
        endpoint = endpoint with { EventTypes = Array.AsReadOnly(endpoint.EventTypes.ToArray()) };
        WebhookOutboxPublisher.Validate(endpoint);
        var clientName = WebhookOutboxPublisher.ClientName(endpoint);
        builder.Services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() =>
            new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        return builder.AddOutboxPublisher(services => new WebhookOutboxPublisher(endpoint,
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<TimeProvider>(), services.GetService<Random>()));
    }
}

using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Extensions;

public static class OutboxBuilderExtensions
{
    /// <summary>Adds a singleton publisher. Its name identifies durable delivery state across hosts and restarts.</summary>
    public static IIxIFlowBuilder AddOutboxPublisher<TPublisher>(this IIxIFlowBuilder builder)
        where TPublisher : class, IOutboxPublisher
    {
        builder.Services.AddSingleton<IOutboxPublisher, TPublisher>();
        return builder;
    }

    public static IIxIFlowBuilder AddOutboxPublisher(this IIxIFlowBuilder builder,
        Func<IServiceProvider, IOutboxPublisher> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        builder.Services.AddSingleton(create);
        return builder;
    }

    /// <summary>Explicitly opts a publisher into retained events when this host registers it.</summary>
    public static IIxIFlowBuilder ReplayRetainedOutboxEvents(this IIxIFlowBuilder builder, string publisherName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherName);
        builder.Services.AddSingleton(new OutboxPublisherStart(publisherName, ReplayRetained: true));
        return builder;
    }

    /// <summary>Removes the durable subscription and gives up its pending deliveries on host startup.</summary>
    public static IIxIFlowBuilder UnregisterOutboxPublisher(this IIxIFlowBuilder builder, string publisherName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherName);
        builder.Services.AddSingleton(new OutboxPublisherRemoval(publisherName));
        return builder;
    }

    public static IIxIFlowBuilder ConfigureOutbox(this IIxIFlowBuilder builder, int concurrency = 8,
        TimeSpan? leaseDuration = null, TimeSpan? retryHorizon = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(concurrency, 1000);
        var options = new OutboxSettings(concurrency, leaseDuration, retryHorizon);
        builder.Services.Replace(ServiceDescriptor.Singleton(options));
        return builder;
    }
}

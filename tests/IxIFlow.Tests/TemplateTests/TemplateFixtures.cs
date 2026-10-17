using System.Collections.Concurrent;
using System.Globalization;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using IxIFlow.Templates.Razor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Tests.TemplateTests;

/// <summary>Models, services and probes the template tests share.</summary>
public static class TemplateFixtures
{
    public static ServiceProvider Services(IWorkflowStateRepository? store = null,
        ITemplateMessageSender? sender = null, ITemplateEngine? engine = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        if (store != null)
            services.Replace(ServiceDescriptor.Singleton(store));
        var templates = services.AddIxIFlowTemplates();
        if (engine != null)
            services.AddSingleton(engine);
        else
            templates.AddLiquid().AddRazor();
        services.AddSingleton(sender ?? new RecordingSender());
        return services.BuildServiceProvider();
    }

    public static Task PublishAsync<TModel>(this IServiceProvider services, string key, int version, string engine,
        TemplateFormat format, params TemplateVariant[] variants) =>
        services.GetRequiredService<TemplatePublisher>()
            .PublishAsync<TModel>(TemplateVersion.Create<TModel>(key, version, engine, format, variants));

    public static TemplateVariant Base(string content) => new(TemplateVersion.BaseCulture, content);

    public static TemplateVariant In(string culture, string content) => new(culture, content);

    public static Order SampleOrder() => new()
    {
        Number = "A-17",
        Customer = new Customer { Name = "Ana", Email = "ana@example.com" },
        Total = 2.5m,
        Lines = [new OrderLine { Sku = "pen", Quantity = 2 }, new OrderLine { Sku = "ink", Quantity = 1 }]
    };

    public class Order
    {
        public string Number { get; set; } = "";
        public Customer Customer { get; set; } = new();
        public decimal Total { get; set; }
        public List<OrderLine> Lines { get; set; } = [];
        public Dictionary<string, string> Labels { get; set; } = [];
        public object? Extra { get; set; }
    }

    public sealed class Customer
    {
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
    }

    public sealed class OrderLine
    {
        public string Sku { get; set; } = "";
        public int Quantity { get; set; }
    }

    /// <summary>Workflow data for the activity tests.</summary>
    public sealed class OrderData
    {
        public Order Order { get; set; } = SampleOrder();
        public string? Culture { get; set; }
        public RenderedTemplate? Body { get; set; }
        public string? MessageId { get; set; }
        public List<string> Recipients { get; set; } = [];
    }

    /// <summary>A sender that delivers each message ID once, as the contract asks, and records every call.</summary>
    public sealed class RecordingSender : ITemplateMessageSender
    {
        private readonly ConcurrentDictionary<string, TemplateMessage> _delivered = new();

        public ConcurrentQueue<TemplateMessage> Calls { get; } = new();

        public IReadOnlyCollection<TemplateMessage> Delivered => _delivered.Values.ToList();

        /// <summary>Calls that fail after the message was delivered, as a lost acknowledgment does.</summary>
        public int FailAfterDelivery { get; set; }

        public Task SendAsync(TemplateMessage message, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(message);
            _delivered.TryAdd(message.MessageId, message);
            if (FailAfterDelivery-- > 0)
                throw new InvalidOperationException("The provider's acknowledgment was lost");
            return Task.CompletedTask;
        }
    }

    /// <summary>An engine that counts renders, rendering through Liquid.</summary>
    public sealed class CountingEngine : ITemplateEngine
    {
        private readonly LiquidTemplateEngine _inner = new();
        private int _renders;

        public int Renders => Volatile.Read(ref _renders);

        public string Name => LiquidTemplateEngine.EngineName;

        public ValueTask<IReadOnlyList<TemplateDiagnostic>> ValidateAsync(TemplateSource template, Type modelType,
            CancellationToken cancellationToken = default) =>
            _inner.ValidateAsync(template, modelType, cancellationToken);

        public ValueTask<string> RenderAsync<TModel>(TemplateSource template, TModel model, CultureInfo culture,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _renders);
            return _inner.RenderAsync(template, model, culture, cancellationToken);
        }
    }

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class Reply;
}

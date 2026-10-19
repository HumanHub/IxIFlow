using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Extensions;

public static class PayloadStoreBuilderExtensions
{
    public static IIxIFlowBuilder UsePayloadStore(this IIxIFlowBuilder builder, IPayloadStore store)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);
        builder.Services.Replace(ServiceDescriptor.Singleton(store));
        return builder;
    }

    public static IIxIFlowBuilder UsePayloadStore(this IIxIFlowBuilder builder, Func<IServiceProvider, IPayloadStore> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);
        builder.Services.Replace(ServiceDescriptor.Singleton(factory));
        return builder;
    }
}

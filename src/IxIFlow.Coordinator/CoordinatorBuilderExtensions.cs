using IxIFlow.Coordinator;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Extensions;

public static class CoordinatorBuilderExtensions
{
    /// <summary>Runs a stateless coordinator on its own HTTP/2 listener inside this application.</summary>
    public static IIxIFlowBuilder AddCoordinator(this IIxIFlowBuilder builder, Action<CoordinatorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new CoordinatorOptions();
        configure(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<CoordinatorIdentity>();
        builder.Services.AddSingleton<CoordinatorMatcher>();
        builder.Services.AddSingleton<CoordinatorService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<CoordinatorService>());
        return builder;
    }

    /// <summary>Links to every discovered coordinator and every configured static endpoint.</summary>
    public static IIxIFlowBuilder UseCoordinators(this IIxIFlowBuilder builder, Action<CoordinatorClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new CoordinatorClientOptions();
        configure(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<CoordinatorLinks>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IRunnableNotifier>(provider => provider.GetRequiredService<CoordinatorLinks>()));
        builder.Services.AddHostedService(provider => provider.GetRequiredService<CoordinatorLinks>());
        return builder;
    }
    internal static void RefuseSqlite(IWorkflowStateRepository store)
    {
        // Avoid a dependency on a provider. Walk base types to cover provider-derived repositories.
        for (var type = store.GetType(); type != null; type = type.BaseType)
            if (type.FullName == "IxIFlow.Core.SqliteWorkflowStateRepository")
                throw new InvalidOperationException("SQLite accepts one host and no coordinator.");
    }
}

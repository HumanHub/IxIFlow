using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IxIFlow.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Debugging;

public static class ServiceCollectionExtensions
{
    public static IIxIFlowBuilder AddDebugging(this IIxIFlowBuilder builder, Action<DebuggingOptions> configure)
    {
        builder.Services.AddDebugging(configure);
        return builder;
    }

    public static IServiceCollection AddDebugging(this IServiceCollection services, Action<DebuggingOptions> configure)
    {
        var options = new DebuggingOptions();
        configure(options);
        var endpoint = options.Snapshot();
        services.AddBreakpointConditions();
        services.AddSingleton<IHostedService>(provider => new DebugAdapterService(provider.GetRequiredService<IWorkflowOperations>(),
            endpoint, provider.GetRequiredService<ILogger<DebugAdapterService>>()));
        return services;
    }
    /// <summary>Registers the optional condition compiler. Enable host debugging separately with AddIxIFlow.</summary>
    public static IServiceCollection AddBreakpointConditions(this IServiceCollection services)
    {
        services.TryAddSingleton<IBreakpointConditionCompiler, BreakpointConditionCompiler>();
        return services;
    }
}

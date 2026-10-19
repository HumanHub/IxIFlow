using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Debugging;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the optional condition compiler. Enable host debugging separately with AddIxIFlow.</summary>
    public static IServiceCollection AddBreakpointConditions(this IServiceCollection services)
    {
        services.TryAddSingleton<IBreakpointConditionCompiler, BreakpointConditionCompiler>();
        return services;
    }
}

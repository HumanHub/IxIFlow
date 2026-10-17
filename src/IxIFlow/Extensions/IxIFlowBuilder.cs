using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Extensions;

/// <summary>
/// What <c>AddIxIFlow</c> returns: the place to choose the store, such as <c>.UseSqlServer(connectionString)</c>, and,
/// later, the other choices that do not depend on it (D-21).
/// </summary>
public interface IIxIFlowBuilder
{
    /// <summary>The service collection IxIFlow was added to, to go on registering other services.</summary>
    IServiceCollection Services { get; }
}

internal sealed class IxIFlowBuilder(IServiceCollection services) : IIxIFlowBuilder
{
    public IServiceCollection Services { get; } = services;
}

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

    /// <summary>
    /// Records that a store was chosen, naming it, and refuses a second choice: two would silently end on the last.
    /// For a store package's <c>Use...</c> method.
    /// </summary>
    void ChooseStore(string store);
}

internal sealed class IxIFlowBuilder(IServiceCollection services, string? chosenStore = null) : IIxIFlowBuilder
{
    private string? _store = chosenStore;

    public IServiceCollection Services { get; } = services;

    public void ChooseStore(string store)
    {
        if (_store != null)
            throw new InvalidOperationException($"IxIFlow already uses {_store} as its store; choose one store");
        _store = store;
    }
}

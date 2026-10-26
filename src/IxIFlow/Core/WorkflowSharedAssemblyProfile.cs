using System.Reflection;
using System.Runtime.Loader;

namespace IxIFlow.Core;

// An immutable host/publisher snapshot. Application implementations are never walked
// into this profile: every non-SDK dependency must be declared as a contract too.
internal sealed class WorkflowSharedAssemblyProfile
{
    internal IReadOnlyDictionary<string, AssemblyName> Identities { get; }
    internal IReadOnlyList<Assembly> Contracts { get; }

    internal WorkflowSharedAssemblyProfile(IReadOnlyDictionary<string, AssemblyName> baseline, IEnumerable<Assembly> contracts)
    {
        var declared = contracts.Select(assembly => assembly ?? throw new ArgumentException("A shared contract assembly is null", nameof(contracts)))
            .Distinct().OrderBy(a => a.FullName, StringComparer.Ordinal).ToArray();
        var declaredNames = declared.Select(assembly => assembly.GetName().Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, AssemblyName>(baseline, StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in declared)
        {
            if (assembly.IsDynamic || assembly.Location.Length == 0 || AssemblyLoadContext.GetLoadContext(assembly) != AssemblyLoadContext.Default)
                throw new ArgumentException("Shared contracts must be file-backed default-context assemblies", nameof(contracts));
            var identity = assembly.GetName();
            if (identities.TryGetValue(identity.Name!, out var existing) && existing.Version != null && existing.FullName != identity.FullName)
                throw new ArgumentException("Shared contract identity conflicts with the SDK profile", nameof(contracts));
            identities[identity.Name!] = identity;
        }
        foreach (var assembly in declared)
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (!identities.TryGetValue(reference.Name!, out var supplied) ||
                    supplied.Version != null && (reference.Version > supplied.Version ||
                        declaredNames.Contains(reference.Name!) && reference.Version != supplied.Version ||
                        !reference.GetPublicKeyToken().AsSpan().SequenceEqual(supplied.GetPublicKeyToken()) ||
                        (reference.CultureName ?? "") != (supplied.CultureName ?? "")))
                    throw new ArgumentException($"Shared contract '{assembly.GetName().Name}' depends on '{reference}' outside the shared profile", nameof(contracts));
            }
        Identities = identities;
        // Re-declaring an SDK/framework assembly does not turn it into an
        // application-supplied contract or remove its NuGet dependency.
        Contracts = Array.AsReadOnly(declared.Where(assembly => !baseline.ContainsKey(assembly.GetName().Name!)).ToArray());
    }
}

internal sealed class SharedContractRegistration
{
    private readonly List<Assembly> _contracts = [];
    private WorkflowSharedAssemblyProfile? _frozen;
    internal void Add(IEnumerable<Assembly> assemblies)
    {
        lock (_contracts)
        {
            if (_frozen != null) throw new InvalidOperationException("The shared contract profile is already frozen");
            _contracts.AddRange(assemblies);
        }
    }
    internal WorkflowSharedAssemblyProfile Freeze()
    {
        lock (_contracts) return _frozen ??= WorkflowSharedAssemblies.CreateProfile(_contracts);
    }
}

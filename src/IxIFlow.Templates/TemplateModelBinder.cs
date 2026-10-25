using System.Reflection;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Templates;

/// <summary>Binds a code model's full identity and registered package closure without retaining its Type.</summary>
public static class TemplateModelBinder
{
    public static async Task<TemplateVersion> BindAsync(TemplateVersion version, Type modelType, IWorkflowCatalogStore catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(catalog);
        TemplateModelTypes.Validate(modelType);
        if (version.ModelType != TemplateVersion.ModelTypeName(modelType) || version.ModelTypeId != WorkflowTypeIdentity.SavedName(modelType))
            throw new ArgumentException("Template model identity differs from its declared runtime type", nameof(modelType));
        var packages = await catalog.GetPackagesAsync();
        var closure = new Dictionary<string, WorkflowPackageReference>(StringComparer.Ordinal);
        void Add(WorkflowPackageReference reference)
        {
            if (closure.TryGetValue(reference.Id, out var prior) && prior != reference)
                throw new ArgumentException("Template model package closure contains conflicting identities");
            closure[reference.Id] = reference;
        }
        var assemblies = new HashSet<Assembly>();
        void Visit(Type type)
        {
            if (type.HasElementType) Visit(type.GetElementType()!);
            assemblies.Add(type.Assembly);
            foreach (var argument in type.GenericTypeArguments) Visit(argument);
        }
        Visit(modelType);
        foreach (var assembly in assemblies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;
            var digest = ArtifactDigest.Of(await File.ReadAllBytesAsync(assembly.Location, cancellationToken));
            var owners = packages.Where(package => package.Entries.Any(entry => entry.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && entry.Value == digest)).ToArray();
            // A host model needs no nupkg. A model shipped by a catalog package pins that verified package and its closure.
            if (owners.Length == 0) continue;
            if (owners.Length > 1)
                owners = owners.Where(owner => version.ModelPackages.Contains(new(owner.Id, owner.Version, owner.Digest))).ToArray();
            if (owners.Length != 1)
                throw new ArgumentException("Model assembly has ambiguous package ownership; supply its exact ModelPackages pins");
            var owner = owners[0];
            if (owner.Closure == null) throw new ArgumentException("Model package closure has not been resolved");
            Add(new(owner.Id, owner.Version, owner.Digest));
            foreach (var reference in owner.Closure) Add(reference);
        }
        foreach (var supplied in version.ModelPackages)
            if (!closure.TryGetValue(supplied.Id, out var actual) || actual != supplied)
                throw new ArgumentException("Template model packages differ from its runtime model closure", nameof(version));
        return new(version.Key, version.Version, version.Engine, version.Format, version.ModelType, version.ModelTypeId,
            closure.Values.ToArray(), version.DisplayName, version.Description, version.Variants);
    }
}

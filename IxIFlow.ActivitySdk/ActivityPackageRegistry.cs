using System.Reflection;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.ActivitySdk;

/// <summary>
/// Resolves activity keys to types in already installed assemblies and exposes their manifests to Studio.
/// Package restore and deployment are handled by the application build or host deployment.
/// </summary>
public sealed class ActivityPackageRegistry : IActivityRegistry
{
    private readonly Dictionary<string, ActivityDescriptor> _activities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActivityPackageManifest> _packages = new(StringComparer.Ordinal);

    public IReadOnlyCollection<ActivityPackageManifest> Packages => _packages.Values;

    public void AddPackage(string manifestYaml, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        RegisterManifest(ActivityPackageManifest.Parse(manifestYaml), assembly);
    }

    public void AddAssembly(Assembly assembly, string packageName, string packageVersion)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageVersion);

        var activities = assembly.GetExportedTypes()
            .Select(type => (Type: type, Attribute: type.GetCustomAttribute<WorkflowActivityAttribute>()))
            .Where(entry => entry.Attribute != null)
            .Select(entry =>
            {
                var attribute = entry.Attribute!;
                var fields = entry.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(property => (Property: property, Attribute: property.GetCustomAttribute<WorkflowInputAttribute>()))
                    .Where(field => field.Attribute != null)
                    .Select(field => new ActivityManifestField
                    {
                        Key = field.Property.Name,
                        Label = field.Attribute!.Label ?? field.Property.Name,
                        Control = field.Attribute.Control,
                        Required = field.Attribute.Required,
                        Help = field.Attribute.Help
                    })
                    .ToList();
                var defaults = entry.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(property => (Property: property, Attribute: property.GetCustomAttribute<WorkflowInputAttribute>()))
                    .Where(field => field.Attribute?.Default != null)
                    .ToDictionary(field => field.Property.Name, field => field.Attribute!.Default!, StringComparer.Ordinal);
                return new ActivityManifestEntry
                {
                    Key = attribute.Key,
                    Version = attribute.Version,
                    Name = attribute.Name ?? entry.Type.Name,
                    Category = attribute.Category ?? "Activities",
                    Designer = attribute.Designer,
                    Fields = fields,
                    Defaults = defaults,
                    Outputs = entry.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(property => property.GetCustomAttribute<WorkflowOutputAttribute>() != null)
                        .Select(property => property.Name)
                        .ToList()
                };
            })
            .ToList();

        var manifest = new ActivityPackageManifest
        {
            Package = new ActivityPackageIdentity { Name = packageName, Version = packageVersion },
            Activities = activities
        };
        manifest.Validate();
        RegisterManifest(manifest, assembly);
    }

    private void RegisterManifest(ActivityPackageManifest manifest, Assembly assembly)
    {
        if (_packages.ContainsKey(manifest.PackageReference))
        {
            throw new InvalidOperationException($"Activity package {manifest.PackageReference} is already registered");
        }

        var attributedTypes = assembly.GetExportedTypes()
            .Select(type => (Type: type, Attribute: type.GetCustomAttribute<WorkflowActivityAttribute>()))
            .Where(entry => entry.Attribute != null)
            .ToArray();
        var typeByKey = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var entry in attributedTypes)
        {
            if (!typeof(IAsyncActivity).IsAssignableFrom(entry.Type) || entry.Type.IsAbstract ||
                string.IsNullOrWhiteSpace(entry.Attribute!.Key) ||
                !typeByKey.TryAdd(entry.Attribute.Key, entry.Type))
            {
                throw new InvalidOperationException($"Assembly {assembly.GetName().Name} has an invalid or duplicate workflow activity");
            }
        }

        var descriptors = new List<ActivityDescriptor>();
        foreach (var activity in manifest.Activities)
        {
            if (!typeByKey.TryGetValue(activity.Key, out var activityType))
            {
                throw new InvalidOperationException($"Activity {activity.Key} has no matching implementation in assembly {assembly.GetName().Name}");
            }
            if (_activities.ContainsKey(activity.Key))
            {
                throw new InvalidOperationException($"Activity key {activity.Key} is already registered");
            }
            descriptors.Add(new ActivityDescriptor
            {
                Key = activity.Key,
                Version = activity.Version,
                PackageName = manifest.Package.Name,
                PackageVersion = manifest.Package.Version,
                ActivityType = activityType
            });
        }

        foreach (var descriptor in descriptors) _activities.Add(descriptor.Key, descriptor);
        _packages.Add(manifest.PackageReference, manifest);
    }

    public void AddEmbeddedPackage(Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Activity manifest resource {resourceName} was not found in {assembly.GetName().Name}");
        using var reader = new StreamReader(stream);
        AddPackage(reader.ReadToEnd(), assembly);
    }

    public void RegisterActivities(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var descriptor in _activities.Values)
        {
            services.AddTransient(descriptor.ActivityType);
        }
    }

    public ValueTask<ActivityDescriptor?> FindAsync(string activityKey, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_activities.GetValueOrDefault(activityKey));
}

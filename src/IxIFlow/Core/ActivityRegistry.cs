using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using IxIFlow.ActivitySdk;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Core;

/// <summary>The code and declaration contract of a locally registered activity.</summary>
public sealed record ActivityDescriptor(string Key, string Version, string Name, string Category, string? Icon,
    string? Designer, IReadOnlyList<ContractMember> Inputs, IReadOnlyList<ContractMember> Outputs,
    string PackageId, string PackageVersion, [property: JsonIgnore] Type? ActivityType)
{
    public ActivityScopeDescriptor? Scope { get; init; }
    public IReadOnlyList<ScopeRequirement> RequiresScopes { get; init; } = [];
    public static ActivityDescriptor OfType(Type type, string packageId, string packageVersion)
    {
        try { return ActivityDescriptorRules.Describe(type, packageId, packageVersion); }
        catch (WorkflowEngineException error) { throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, $"Activity '{type.FullName}' {error.Message}", error); }
    }
    public static IReadOnlyList<ActivityDescriptor> Scan(Assembly assembly, string packageId, string packageVersion) =>
        assembly.GetTypes().Where(type => type.IsDefined(typeof(WorkflowActivityAttribute))).Select(type => OfType(type, packageId, packageVersion)).OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
    public string ToJson()
    {
        ActivityDescriptorRules.Validate(this);
        return WorkflowJson.Serialize(this with { Inputs = Inputs.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            Outputs = Outputs.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            RequiresScopes = RequiresScopes.OrderBy(item => item.Type, StringComparer.Ordinal).ToArray() });
    }
    public static ActivityDescriptor Parse(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var names = new[] { "Key", "Version", "Name", "Category", "Icon", "Designer", "Inputs", "Outputs", "PackageId", "PackageVersion", "Scope", "RequiresScopes" };
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !document.RootElement.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)))
            throw new System.Text.Json.JsonException("Invalid activity descriptor shape");
        var root = document.RootElement;
        foreach (var member in root.GetProperty("Inputs").EnumerateArray().Concat(root.GetProperty("Outputs").EnumerateArray()))
            Shape(member, ["Name", "Type", "Required", "HasDefault", "DefaultJson", "DisplayName", "Description", "UIHint"]);
        foreach (var requirement in root.GetProperty("RequiresScopes").EnumerateArray()) Shape(requirement, ["Type", "TypeName"]);
        if (root.GetProperty("Scope").ValueKind != System.Text.Json.JsonValueKind.Null)
            Shape(root.GetProperty("Scope"), ["ValueType", "ValueTypeName", "Concurrency", "Threading"]);
        var descriptor = WorkflowJson.Deserialize<ActivityDescriptor>(json) ?? throw new System.Text.Json.JsonException("Missing descriptor");
        ActivityDescriptorRules.Validate(descriptor);
        return descriptor;

        static void Shape(System.Text.Json.JsonElement element, string[] members)
        {
            if (element.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !element.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal).SequenceEqual(members.Order(StringComparer.Ordinal)))
                throw new System.Text.Json.JsonException("Invalid nested activity descriptor shape");
        }
    }
    [JsonIgnore] public string ContractHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        WorkflowJson.Serialize(new { Key, Version, Inputs = Inputs.OrderBy(item => item.Name, StringComparer.Ordinal).Select(Member), Outputs = Outputs.OrderBy(item => item.Name, StringComparer.Ordinal).Select(Member),
            Scope = Scope == null ? null : new { Scope.ValueType, Scope.Concurrency, Scope.Threading }, RequiresScopes = RequiresScopes.Select(item => item.Type).Order(StringComparer.Ordinal) }))));
    private static object Member(ContractMember member) => new { member.Name, member.Type, member.Required, member.HasDefault, member.DefaultJson };
}

/// <summary>Immutable installed activity metadata. The fingerprint identifies metadata, not executable code.</summary>
public sealed record InstalledActivityPackage(string Id, string Version, string Fingerprint, string ActivitiesJson)
{
    /// <summary>The live members that registered this exact metadata.</summary>
    public IReadOnlyList<Guid> Hosts { get; init; } = [];
}

/// <summary>The activities and installed packages registered in this process.</summary>
public interface IActivityRegistry
{
    /// <summary>The registered activity declarations.</summary>
    IReadOnlyList<ActivityDescriptor> Activities { get; }
    /// <summary>The packages and fingerprints of the selected declarations.</summary>
    IReadOnlyList<InstalledActivityPackage> Packages { get; }
    /// <summary>Finds an activity by its ordinal key.</summary>
    ActivityDescriptor? Find(string key);
    /// <summary>Whether this runtime type was explicitly registered.</summary>
    bool IsRegistered(Type type);
}

internal sealed class ActivityRegistrations(IServiceCollection services)
{
    private readonly Dictionary<Type, ActivityDescriptor> _activities = [];
    private readonly Dictionary<string, Assembly> _assemblies = new(StringComparer.Ordinal);

    internal void Add(IEnumerable<Type> types)
    {
        var pending = types.Where(type => !_activities.ContainsKey(type)).Select(Describe).ToArray();
        var keys = _activities.Values.ToDictionary(activity => activity.Key, activity => activity.ActivityType!, StringComparer.Ordinal);
        var assemblies = new Dictionary<string, Assembly>(_assemblies, StringComparer.Ordinal);
        foreach (var activity in pending)
        {
            if (!keys.TryAdd(activity.Key, activity.ActivityType!))
                throw Refused(activity.ActivityType!, $"activity key '{activity.Key}' is already registered");
            if (assemblies.TryGetValue(activity.PackageId, out var assembly) && assembly != activity.ActivityType!.Assembly)
                throw Refused(activity.ActivityType!, $"package id '{activity.PackageId}' belongs to another assembly");
            assemblies[activity.PackageId] = activity.ActivityType!.Assembly;
        }
        foreach (var activity in pending)
        {
            _activities.Add(activity.ActivityType!, activity);
            _assemblies[activity.PackageId] = activity.ActivityType!.Assembly;
            services.TryAddTransient(activity.ActivityType!);
        }
    }

    internal static void CheckAssembly(Assembly assembly)
    {
        if (AssemblyLoadContext.GetLoadContext(assembly) != AssemblyLoadContext.Default || assembly.IsCollectible)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Activity assembly '{assembly.GetName().Name}' must use the default, non-collectible load context");
    }

    private static ActivityDescriptor Describe(Type type)
    {
        CheckAssembly(type.Assembly);
        var name = type.Assembly.GetName();
        var id = WorkflowPackageRules.NormalizeId(name.Name!);
        var version = type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? name.Version!.ToString();
        try { version = WorkflowPackageRules.NormalizeVersion(version.Split('+', 2)[0]); }
        catch (WorkflowEngineException error) { throw Refused(type, error.Message); }
        return ActivityDescriptor.OfType(type, id, version);
    }

    internal IActivityRegistry Build()
    {
        foreach (var type in _activities.Keys)
        {
            var descriptors = services.Where(service => service.ServiceType == type && !service.IsKeyedService).ToArray();
            if (descriptors.Length != 1 || descriptors[0].Lifetime != ServiceLifetime.Transient)
                throw Refused(type, "requires exactly one transient service descriptor in the final service collection");
        }
        return new ActivityRegistry(_activities.Values.OrderBy(activity => activity.Key, StringComparer.Ordinal).ToArray());
    }

    private static WorkflowEngineException Refused(Type type, string reason) =>
        new(WorkflowErrorKind.InvalidDefinition, $"Activity '{type.FullName}' {reason}");
}

internal sealed class ActivityRegistry(IReadOnlyList<ActivityDescriptor> activities) : IActivityRegistry
{
    private readonly FrozenSet<Type> _types = activities.Select(activity => activity.ActivityType!).ToFrozenSet();
    private readonly FrozenDictionary<string, ActivityDescriptor> _byKey = activities
        .ToFrozenDictionary(activity => activity.Key, StringComparer.Ordinal);
    public IReadOnlyList<ActivityDescriptor> Activities { get; } = Array.AsReadOnly(activities.ToArray());
    public IReadOnlyList<InstalledActivityPackage> Packages { get; } = Array.AsReadOnly(activities
        .GroupBy(activity => (activity.PackageId, activity.PackageVersion))
        .OrderBy(group => group.Key.PackageId, StringComparer.Ordinal).Select(group =>
        {
            var json = WorkflowJson.Serialize(group.OrderBy(activity => activity.Key, StringComparer.Ordinal).ToArray());
            return new InstalledActivityPackage(group.Key.PackageId, group.Key.PackageVersion,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), json);
        }).ToArray());
    public ActivityDescriptor? Find(string key) => _byKey.GetValueOrDefault(key);
    public bool IsRegistered(Type type) => _types.Contains(type);
}

internal static class InstalledPackageRules
{
    internal static InstalledActivityPackage Snapshot(InstalledActivityPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Version == null || WorkflowPackageRules.NormalizeId(package.Id) != package.Id ||
            WorkflowPackageRules.NormalizeVersion(package.Version) != package.Version ||
            package.ActivitiesJson == null || package.Fingerprint !=
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(package.ActivitiesJson))))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid installed activity package metadata");
        using var json = System.Text.Json.JsonDocument.Parse(package.ActivitiesJson);
        if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Installed activities must be a JSON array");
        return package with { Hosts = [] };
    }
}

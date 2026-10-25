using System.Reflection;
using System.ComponentModel;
using IxIFlow.ActivitySdk;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public sealed record ScopeRequirement(string Type, string TypeName);
public sealed record ActivityScopeDescriptor(string ValueType, string ValueTypeName, ScopeConcurrency Concurrency, ScopeThreading Threading);

/// <summary>Portable activity declarations, derived identically for installed and extracted code.</summary>
public static class ActivityDescriptorRules
{
    internal static ActivityDescriptor Describe(Type type, string packageId, string packageVersion)
    {
        var attribute = type.GetCustomAttribute<WorkflowActivityAttribute>();
        if (attribute == null || !type.IsVisible || !type.IsClass || type.IsAbstract || type.IsGenericType ||
            type.ContainsGenericParameters || !ActivityTypes.IsActivity(type))
            throw Invalid("Requires a visible, concrete, non-generic activity with [WorkflowActivity]");
        var scope = typeof(IScopeActivity).IsAssignableFrom(type) ? ActivityTypes.ValidateScope(type) : null;
        if (scope == null && (type.IsDefined(typeof(ScopeConcurrencyAttribute)) || type.IsDefined(typeof(ScopeThreadingAttribute))))
            throw Invalid("Scope concurrency and threading require a scope implementation");
        var contract = ActivityContract.Describe(type);
        var result = new ActivityDescriptor(attribute.Key, attribute.Version, attribute.Name ?? type.Name,
            attribute.Category ?? "Activities", type.GetCustomAttribute<DescriptionAttribute>()?.Description, attribute.Icon, attribute.Designer, contract.Inputs, contract.Outputs,
            WorkflowPackageRules.NormalizeId(packageId), WorkflowPackageRules.NormalizeVersion(packageVersion), type.FullName!, WorkflowTypeIdentity.CSharpName(type))
        {
            ActivityType = type,
            Scope = scope == null ? null : new(WorkflowTypeIdentity.SavedName(scope), WorkflowTypeIdentity.CSharpName(scope),
                ActivityTypes.Concurrency(type), ActivityTypes.Threading(type)),
            RequiresScopes = Array.AsReadOnly(ActivityTypes.Requirements(type).OrderBy(WorkflowTypeIdentity.SavedName, StringComparer.Ordinal)
                .Select(item => new ScopeRequirement(WorkflowTypeIdentity.SavedName(item), WorkflowTypeIdentity.CSharpName(item))).ToArray())
        };
        Validate(result);
        return result;
    }

    public static void Validate(ActivityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        try
        {
            foreach (var value in new[] { descriptor.Key, descriptor.Version, descriptor.Name, descriptor.Category,
                         descriptor.Description, descriptor.Icon, descriptor.Designer, descriptor.PackageId, descriptor.PackageVersion,
                         descriptor.MetadataTypeName, descriptor.CSharpTypeName })
                WorkflowCatalogRules.ValidateText(value);
        }
        catch (ArgumentException error)
        { throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, "Invalid activity descriptor text", error); }
        if (string.IsNullOrWhiteSpace(descriptor.Key) || descriptor.Key != descriptor.Key.Trim() || descriptor.Key.Length > 100 ||
            string.IsNullOrWhiteSpace(descriptor.Version) || descriptor.Version.Length > 50 ||
            string.IsNullOrWhiteSpace(descriptor.Name) || descriptor.Name.Length > 200 ||
            string.IsNullOrWhiteSpace(descriptor.Category) || descriptor.Category.Length > 100 ||
            !WorkflowPackageRules.ValidId(descriptor.PackageId) || WorkflowPackageRules.NormalizeId(descriptor.PackageId) != descriptor.PackageId ||
            WorkflowPackageRules.NormalizeVersion(descriptor.PackageVersion) != descriptor.PackageVersion ||
            string.IsNullOrWhiteSpace(descriptor.MetadataTypeName) ||
            WorkflowTypeIdentity.CSharpName($"ixi-type:1:N1:X{descriptor.MetadataTypeName.Length}:{descriptor.MetadataTypeName}0:") != descriptor.CSharpTypeName ||
            descriptor.MetadataTypeName.Split('+', '.').Any(part => part.Length == 0 || !(char.IsLetter(part[0]) || part[0] == '_') || part.Any(c => !(char.IsLetterOrDigit(c) || c == '_'))) ||
            descriptor.Inputs == null || descriptor.Outputs == null || descriptor.RequiresScopes == null)
            throw Invalid("Invalid activity descriptor");
        _ = new WorkflowContract(descriptor.Inputs, descriptor.Outputs, false);
        if (descriptor.Inputs.Concat(descriptor.Outputs).Any(member => member.UIHint is not (null or "text" or "code" or "binding" or "connection")))
            throw Invalid("Invalid activity member UI hint");
        foreach (var token in new[] { descriptor.Icon, descriptor.Designer }.OfType<string>())
            if (token.Length > 100 || !System.Text.RegularExpressions.Regex.IsMatch(token, "^[a-z][a-z0-9-]*$"))
                throw Invalid("Invalid activity icon or designer token");
        foreach (var requirement in descriptor.RequiresScopes)
            if (requirement == null || !WorkflowTypeIdentity.IsSavedName(requirement.Type) || string.IsNullOrWhiteSpace(requirement.TypeName) || WorkflowTypeIdentity.CSharpName(requirement.Type) != requirement.TypeName)
                throw Invalid("Invalid scope requirement");
        if (descriptor.RequiresScopes.Select(item => item.Type).Distinct(StringComparer.Ordinal).Count() != descriptor.RequiresScopes.Count)
            throw Invalid("Duplicate scope requirements");
        if (descriptor.Scope is { } scope && (!WorkflowTypeIdentity.IsSavedName(scope.ValueType) || string.IsNullOrWhiteSpace(scope.ValueTypeName) ||
            WorkflowTypeIdentity.CSharpName(scope.ValueType) != scope.ValueTypeName || !Enum.IsDefined(scope.Concurrency) || !Enum.IsDefined(scope.Threading)))
            throw Invalid("Invalid scope descriptor");
        if (descriptor.ActivityType is { } type)
        {
            var scopes = ActivityTypes.ScopeTypes(type);
            if (typeof(IScopeActivity).IsAssignableFrom(type)) _ = ActivityTypes.ValidateScope(type);
            if ((scopes.Length == 1) != (descriptor.Scope != null) || descriptor.Scope is { } declared &&
                (declared.ValueType != WorkflowTypeIdentity.SavedName(scopes[0]) || declared.ValueTypeName != WorkflowTypeIdentity.CSharpName(scopes[0]) ||
                 declared.Concurrency != ActivityTypes.Concurrency(type) || declared.Threading != ActivityTypes.Threading(type)))
                throw Invalid("Scope descriptor disagrees with its implementation");
            var required = ActivityTypes.Requirements(type).Select(item => new ScopeRequirement(WorkflowTypeIdentity.SavedName(item), WorkflowTypeIdentity.CSharpName(item)))
                .OrderBy(item => item.Type, StringComparer.Ordinal);
            if (!required.SequenceEqual(descriptor.RequiresScopes.OrderBy(item => item.Type, StringComparer.Ordinal)))
                throw Invalid("Scope requirements disagree with the implementation");
        }
    }

    private static WorkflowEngineException Invalid(string message) => new(WorkflowErrorKind.InvalidDefinition, message);
}

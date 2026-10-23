using System.Reflection;
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
            attribute.Category ?? "Activities", attribute.Icon, attribute.Designer, contract.Inputs, contract.Outputs,
            WorkflowPackageRules.NormalizeId(packageId), WorkflowPackageRules.NormalizeVersion(packageVersion), type)
        {
            Scope = scope == null ? null : new(WorkflowTypeIdentity.SavedName(scope), WorkflowTypeIdentity.CSharpName(scope),
                ActivityTypes.Concurrency(type), ActivityTypes.Threading(type)),
            RequiresScopes = ActivityTypes.Requirements(type).OrderBy(WorkflowTypeIdentity.SavedName, StringComparer.Ordinal)
                .Select(item => new ScopeRequirement(WorkflowTypeIdentity.SavedName(item), WorkflowTypeIdentity.CSharpName(item))).ToArray()
        };
        Validate(result);
        return result;
    }

    public static void Validate(ActivityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.Key) || descriptor.Key.Length > 100 ||
            string.IsNullOrWhiteSpace(descriptor.Version) || descriptor.Version.Length > 50 ||
            descriptor.Inputs == null || descriptor.Outputs == null || descriptor.RequiresScopes == null)
            throw Invalid("Invalid activity descriptor");
        _ = new WorkflowContract(descriptor.Inputs, descriptor.Outputs, false);
        foreach (var token in new[] { descriptor.Icon, descriptor.Designer }.OfType<string>())
            if (!System.Text.RegularExpressions.Regex.IsMatch(token, "^[a-z][a-z0-9-]*$"))
                throw Invalid("Invalid activity icon or designer token");
        foreach (var requirement in descriptor.RequiresScopes)
            if (requirement == null || !WorkflowTypeIdentity.IsSavedName(requirement.Type) || WorkflowTypeIdentity.CSharpName(requirement.Type) != requirement.TypeName)
                throw Invalid("Invalid scope requirement");
        if (descriptor.RequiresScopes.Select(item => item.Type).Distinct(StringComparer.Ordinal).Count() != descriptor.RequiresScopes.Count)
            throw Invalid("Duplicate scope requirements");
        if (descriptor.Scope is { } scope && (!WorkflowTypeIdentity.IsSavedName(scope.ValueType) ||
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

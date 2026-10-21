using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using IxIFlow.ActivitySdk;

namespace IxIFlow.Core.Runtime;

/// <summary>The directions and typed defaults of an activity's properties.</summary>
internal static class ActivityContract
{
    public static bool IsMarked(PropertyInfo property) =>
        property.IsDefined(typeof(ActivityInputAttribute)) || property.IsDefined(typeof(ActivityOutputAttribute)) ||
        property.IsDefined(typeof(ActivityInputOutputAttribute));

    public static bool IsInput(PropertyInfo property, bool marked) => marked
        ? property.IsDefined(typeof(ActivityInputAttribute)) || property.IsDefined(typeof(ActivityInputOutputAttribute))
        : property.SetMethod is { IsPublic: true };

    public static bool IsOutput(PropertyInfo property, bool marked) => marked
        ? property.IsDefined(typeof(ActivityOutputAttribute)) || property.IsDefined(typeof(ActivityInputOutputAttribute))
        : property.GetMethod is { IsPublic: true };

    public static void Check(Type activity)
    {
        var properties = activity.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var marked = properties.Any(IsMarked);
        foreach (var property in properties)
        {
            if (IsMarked(property))
            {
                if (IsInput(property, true) && property.SetMethod is not { IsPublic: true })
                    throw Refused(activity, property, "is marked as an input but has no public setter");
                if (IsOutput(property, true) && property.GetMethod is not { IsPublic: true })
                    throw Refused(activity, property, "is marked as an output but has no public getter");
            }
            if (property.IsDefined(typeof(RequiredAttribute)) && IsOutput(property, marked) && !IsInput(property, marked))
                throw Refused(activity, property, "has [Required] but is output-only");
            if (property.IsDefined(typeof(DefaultValueAttribute)))
            {
                if (property.SetMethod is not { IsPublic: true })
                    throw Refused(activity, property, "has [DefaultValue] but has no public setter");
                _ = Default(property, activity);
            }
        }
    }

    public static (IReadOnlyList<ContractMember> Inputs, IReadOnlyList<ContractMember> Outputs) Describe(Type activity)
    {
        Check(activity);
        var properties = activity.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var marked = properties.Any(IsMarked);
        foreach (var property in properties)
            if (property.GetCustomAttribute<UIHintAttribute>() is { UIHint: var hint } &&
                hint is not ("text" or "code" or "binding" or "connection"))
                throw Refused(activity, property, $"has unsupported [UIHint] '{hint}'");
        ContractMember Member(PropertyInfo property)
        {
            var display = property.GetCustomAttribute<DisplayAttribute>();
            var hasDefault = property.IsDefined(typeof(DefaultValueAttribute));
            return new(property.Name, WorkflowTypeIdentity.SavedName(property.PropertyType),
                property.IsDefined(typeof(RequiredAttribute)), hasDefault,
                hasDefault ? ActivityInputValue.Serialize(property, Default(property, activity)) : null,
                display?.GetName() ?? property.Name, display?.GetDescription(),
                property.GetCustomAttribute<UIHintAttribute>()?.UIHint ?? "text");
        }
        var visible = activity.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        return (Array.AsReadOnly(visible.Where(property => IsInput(property, marked)).Select(Member).ToArray()),
            Array.AsReadOnly(visible.Where(property => IsOutput(property, marked)).Select(Member).ToArray()));
    }

    private static WorkflowEngineException Refused(Type activity, PropertyInfo property, string reason) =>
        new(WorkflowErrorKind.InvalidDefinition, $"Activity property '{activity.Name}.{property.Name}' {reason}");

    public static object? Default(PropertyInfo property, Type? activityType = null)
    {
        var value = property.GetCustomAttribute<DefaultValueAttribute>()!.Value;
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        try
        {
            if (value == null)
            {
                // DefaultValue(Type, string) swallows failed conversions and leaves Value null.
                var attribute = DefaultDeclaration(property);
                if (attribute?.ConstructorArguments is { Count: 2 } arguments && arguments[1].Value != null)
                    throw new InvalidCastException("The typed default literal cannot be converted");
                if (property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) == null)
                    throw new InvalidCastException("Null is not assignable to a non-nullable value type");
                return null;
            }
            if (type.IsInstanceOfType(value))
                return value;
            if (type.IsEnum)
                return value is string name ? Enum.Parse(type, name) : Enum.ToObject(type, value);
            var converter = TypeDescriptor.GetConverter(type);
            var converted = converter.CanConvertFrom(value.GetType())
                ? converter.ConvertFrom(null, CultureInfo.InvariantCulture, value)
                : Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            if (converted == null ? type.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) == null :
                !type.IsInstanceOfType(converted))
                throw new InvalidCastException("The converter returned an incompatible value");
            return converted;
        }
        catch (Exception error) when (error is not WorkflowEngineException)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Activity property '{(activityType ?? property.DeclaringType)!.Name}.{property.Name}' has a [DefaultValue] that cannot be converted to {property.PropertyType.Name}", error);
        }
    }

    private static CustomAttributeData? DefaultDeclaration(PropertyInfo property)
    {
        for (var type = property.DeclaringType; type != null; type = type.BaseType)
            if (type.GetProperty(property.Name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                    BindingFlags.DeclaredOnly)?.GetCustomAttributesData()
                .FirstOrDefault(item => item.AttributeType == typeof(DefaultValueAttribute)) is { } attribute)
                return attribute;
        return null;
    }

    public static void ValidateRequired(IAsyncActivity activity, ActivityInputSnapshot inputs, IServiceProvider services)
    {
        var properties = activity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var marked = properties.Any(IsMarked);
        foreach (var property in properties.Where(property => IsInput(property, marked) &&
                     property.IsDefined(typeof(RequiredAttribute))))
        {
            // A setter-only input can still be validated from its saved value.
            var value = property.GetMethod != null ? property.GetValue(activity) :
                inputs.Properties.TryGetValue(property.Name, out var saved) ? saved.Read(property, services) : null;
            if (value == null || value is string { Length: 0 })
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"InvalidInput: activity input '{activity.GetType().Name}.{property.Name}' is required and cannot be null or an empty string");
        }
    }
}

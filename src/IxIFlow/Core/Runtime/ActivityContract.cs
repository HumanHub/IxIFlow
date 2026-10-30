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
            if (property.GetIndexParameters().Length != 0 && (IsInput(property, marked) || IsOutput(property, marked)))
                throw Refused(activity, property, "is an indexer and cannot be mapped");

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
                WorkflowTypeIdentity.CSharpName(property.PropertyType),
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

    public static object? Default(PropertyInfo property, Type? activityType = null, string owner = "Activity")
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        try
        {
            var declaration = DefaultDeclaration(property);

            // DefaultValueAttribute(Type,string) calls the process-wide TypeDescriptor cache.
            // Read that constructor's data directly for collectible types, using the same
            // converter contract without putting the package's Type in a global cache.
            var value = declaration?.ConstructorArguments is { Count: 2 } declared && declared[0].Value is Type target &&
                target.IsCollectible
                    ? declared[1].Value == null
                        ? null
                        : Converter(target).ConvertFrom(null, CultureInfo.InvariantCulture, declared[1].Value!)
                    : property.GetCustomAttribute<DefaultValueAttribute>()!.Value;

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

            var converter = Converter(type);

            var converted = converter.CanConvertFrom(value.GetType())
                ? converter.ConvertFrom(null, CultureInfo.InvariantCulture, value)
                : Convert.ChangeType(value, type, CultureInfo.InvariantCulture);

            if (converted == null
                    ? type.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) == null
                    : !type.IsInstanceOfType(converted))
                throw new InvalidCastException("The converter returned an incompatible value");

            return converted;
        }
        catch (Exception error) when (error is not WorkflowEngineException)
        {
            var cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;

            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"{owner} property '{(activityType ?? property.DeclaringType)!.Name}.{property.Name}' has a [DefaultValue] that cannot be converted to {property.PropertyType.Name}: {cause.Message}",
                cause);
        }
    }

    private static TypeConverter Converter(Type type)
    {
        if (!type.IsCollectible)
            return TypeDescriptor.GetConverter(type);

        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return new CollectibleNullableConverter(underlying, Converter(underlying));

        if (type.GetCustomAttribute<TypeConverterAttribute>(inherit: true) is { ConverterTypeName.Length: > 0 } attribute)
        {
            var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly)!;

            var converterType = Type.GetType(attribute.ConverterTypeName, context.LoadFromAssemblyName,
                (assembly, name, ignoreCase) => (assembly ?? type.Assembly).GetType(name, throwOnError: false, ignoreCase),
                throwOnError: true)!;

            return (TypeConverter)(converterType.GetConstructor([typeof(Type)]) is { } constructor
                ? constructor.Invoke([type])
                : Activator.CreateInstance(converterType)!);
        }

        if (type.IsEnum)
            return new EnumConverter(type);

        return new TypeConverter();
    }

    private sealed class CollectibleNullableConverter(Type underlying, TypeConverter converter) : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
            sourceType == underlying || sourceType == typeof(string) || converter.CanConvertFrom(context, sourceType);

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
            value == null || value is string { Length: 0 } ? null :
            underlying.IsInstanceOfType(value) ? value : converter.ConvertFrom(context, culture, value);
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

    internal static bool IsEmpty(object? value) => value == null || value is string { Length: 0 };

    public static void ValidateRequired(object activity, ActivityInputSnapshot inputs, IServiceProvider services)
    {
        var properties = activity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var marked = properties.Any(IsMarked);

        foreach (var property in properties.Where(property => IsInput(property, marked) &&
                     property.IsDefined(typeof(RequiredAttribute))))
        {
            // A setter-only input can still be validated from its saved value.
            var value = property.GetMethod != null ? ActivityPropertyAccess.GetValue(property, activity) :
                inputs.Properties.TryGetValue(property.Name, out var saved) ? saved.Read(property, services) : null;

            if (IsEmpty(value))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"InvalidInput: activity input '{activity.GetType().Name}.{property.Name}' is required and cannot be null or an empty string");
        }
    }
}
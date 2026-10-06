using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IxIFlow.Core.Runtime;

/// <summary>Copies selected exception properties into a checkpointable fault DTO.</summary>
internal sealed class FaultProjection
{
    private static readonly ConcurrentDictionary<(Type Exception, Type Fault), FaultProjection> Cache = new();

    private readonly Type _exceptionType;
    private readonly Type _faultType;
    private readonly ConstructorInfo? _constructor;
    private readonly (PropertyInfo Target, PropertyInfo Source)[] _properties;

    private FaultProjection(Type exceptionType, Type faultType)
    {
        if (!typeof(Exception).IsAssignableFrom(exceptionType))
            throw new ArgumentException("The catch type must be an exception", nameof(exceptionType));
        _exceptionType = exceptionType;
        if (faultType == typeof(EmptyFault))
        {
            _faultType = faultType;
            _properties = [];
            _constructor = faultType.GetConstructor(Type.EmptyTypes);
            return;
        }

        ValidateValueType(faultType, new HashSet<Type>());
        _faultType = faultType;
        var targetProperties = faultType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetMethod?.IsPublic == true).ToArray();
        _properties = targetProperties.Select(target =>
        {
            var source = FindSourceProperty(exceptionType, target.Name);
            if (source?.GetMethod?.IsPublic != true || source.GetIndexParameters().Length != 0)
                throw new NotSupportedException(
                    $"Fault '{faultType.Name}.{target.Name}' has no matching public property on '{exceptionType.Name}'");
            if (source.PropertyType != target.PropertyType)
                throw new NotSupportedException(
                    $"Fault '{faultType.Name}.{target.Name}' has type '{target.PropertyType.Name}', " +
                    $"but '{exceptionType.Name}.{source.Name}' has type '{source.PropertyType.Name}'");
            return (target, source);
        }).ToArray();
        _constructor = SelectConstructor(faultType, targetProperties);
    }

    public static FaultProjection For(Type exceptionType, Type faultType) =>
        Cache.GetOrAdd((exceptionType, faultType), key => new FaultProjection(key.Exception, key.Fault));

    public IReadOnlyCollection<PropertyInfo> SourceProperties =>
        _properties.Select(pair => pair.Source).ToArray();

    public static string PropertyKey(PropertyInfo property) =>
        $"{property.DeclaringType!.AssemblyQualifiedName}|{property.Name}";

    private static PropertyInfo? FindSourceProperty(Type exceptionType, string name)
    {
        for (var type = exceptionType; type != null; type = type.BaseType)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.DeclaredOnly);
            if (property != null)
                return property;
        }
        return null;
    }

    public object Capture(Exception error)
    {
        if (_faultType == typeof(EmptyFault))
            return new EmptyFault();
        var values = _properties.ToDictionary(pair => pair.Target.Name,
            pair => pair.Source.GetValue(error), StringComparer.OrdinalIgnoreCase);
        return CreateFault(values);
    }

    public object Capture(SerializedException error)
    {
        if (_faultType == typeof(EmptyFault))
            return new EmptyFault();
        var savedFault = error.Fault;
        if (savedFault?.Type == _faultType.AssemblyQualifiedName &&
            error.FaultCatchType == _exceptionType.AssemblyQualifiedName)
            return JsonSerializer.Deserialize(savedFault.Json, _faultType)
                ?? throw new InvalidOperationException($"Saved fault '{_faultType.Name}' could not be restored");

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (target, source) in _properties)
        {
            if (!error.Properties.TryGetValue(PropertyKey(source), out var saved))
                throw new InvalidOperationException(
                    $"Saved exception property '{source.Name}' is unavailable for fault '{_faultType.Name}'");
            if (saved.Error != null)
                throw new InvalidOperationException(
                    $"Saved exception property '{source.Name}' cannot be projected: {saved.Error}");
            values[target.Name] = JsonSerializer.Deserialize(saved.Json ?? "null", target.PropertyType);
        }
        return CreateFault(values);
    }

    private object CreateFault(Dictionary<string, object?> values)
    {
        var arguments = _constructor?.GetParameters().Select(parameter => values[parameter.Name!]).ToArray() ?? [];
        var fault = _constructor?.Invoke(arguments) ?? Activator.CreateInstance(_faultType)!;
        foreach (var (target, _) in _properties)
        {
            if (_constructor?.GetParameters().Any(parameter =>
                    string.Equals(parameter.Name, target.Name, StringComparison.OrdinalIgnoreCase)) == true)
                continue;
            target.SetValue(fault, values[target.Name]);
        }

        var json = JsonSerializer.Serialize(fault, _faultType);
        var restored = JsonSerializer.Deserialize(json, _faultType)
            ?? throw new InvalidOperationException($"Fault '{_faultType.Name}' could not be restored");
        if (JsonSerializer.Serialize(restored, _faultType) != json)
            throw new InvalidOperationException($"Fault '{_faultType.Name}' does not survive a JSON round trip");
        return restored;
    }

    private static ConstructorInfo? SelectConstructor(Type type, PropertyInfo[] properties)
    {
        if (type.IsValueType)
            return null;
        var constructors = type.GetConstructors();
        var marked = constructors.Where(candidate =>
            candidate.GetCustomAttribute<JsonConstructorAttribute>() != null).ToArray();
        if (marked.Length > 1)
            throw new NotSupportedException($"Fault '{type.Name}' has more than one JSON constructor");
        var constructor = marked.SingleOrDefault() ??
            constructors.FirstOrDefault(candidate => candidate.GetParameters().Length == 0) ??
            (constructors.Length == 1 ? constructors[0] : null);
        if (constructor == null)
            throw new NotSupportedException($"Fault '{type.Name}' needs one usable public constructor");
        foreach (var parameter in constructor.GetParameters())
            if (!properties.Any(property =>
                    string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase) &&
                    property.PropertyType == parameter.ParameterType))
                throw new NotSupportedException(
                    $"Fault constructor parameter '{type.Name}.{parameter.Name}' needs a matching property");
        foreach (var property in properties)
            if (property.SetMethod?.IsPublic != true && !constructor.GetParameters().Any(parameter =>
                    string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException(
                    $"Fault property '{type.Name}.{property.Name}' must be writable or constructor-bound");
        return constructor;
    }

    private static void ValidateValueType(Type type, HashSet<Type> seen)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
            type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
            type == typeof(TimeSpan) || type == typeof(DateOnly) || type == typeof(TimeOnly))
            return;
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            ValidateValueType(type.GetElementType()!, seen);
            return;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            ValidateValueType(type.GetGenericArguments()[0], seen);
            return;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) &&
            type.GetGenericArguments()[0] == typeof(string))
        {
            ValidateValueType(type.GetGenericArguments()[1], seen);
            return;
        }
        if (type == typeof(object) || type.IsInterface || type.IsAbstract ||
            typeof(Exception).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type) ||
            typeof(MemberInfo).IsAssignableFrom(type) || type.IsPointer || type.IsByRef ||
            type.ContainsGenericParameters || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true ||
            !seen.Add(type))
            throw new NotSupportedException($"Fault value type '{type.FullName}' cannot be checkpointed");
        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetMethod?.IsPublic == true).ToArray();
        if (properties.Length == 0 || type.GetFields(BindingFlags.Instance | BindingFlags.Public).Length != 0)
            throw new NotSupportedException($"Fault '{type.Name}' needs public properties and no public fields");
        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length != 0 ||
                property.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                throw new NotSupportedException($"Fault property '{type.Name}.{property.Name}' cannot be checkpointed");
            ValidateValueType(property.PropertyType, seen);
        }
        SelectConstructor(type, properties);
        seen.Remove(type);
    }
}

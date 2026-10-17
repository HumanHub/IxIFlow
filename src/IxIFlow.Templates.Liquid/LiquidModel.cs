using System.Reflection;

namespace IxIFlow.Templates.Liquid;

/// <summary>
/// What a Liquid template can read from a model type: the public instance properties and fields of the model and of
/// every type reachable through them, through collections and string-keyed dictionaries included. Nothing else is
/// readable, so a template reaches the model's members and no other object in the process (H-03).
/// </summary>
internal static class LiquidModel
{
    /// <summary>The types whose members a template may read, starting at <paramref name="model"/>.</summary>
    public static IReadOnlyList<Type> Reachable(Type model)
    {
        var found = new List<Type>();
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>([model]);
        while (pending.Count > 0)
        {
            var type = Unwrap(pending.Dequeue());
            if (IsScalar(type) || type == typeof(object))
                continue;
            if (DictionaryValue(type) is { } value)
            {
                pending.Enqueue(value);
                continue;
            }
            if (Element(type) is { } element)
            {
                pending.Enqueue(element);
                continue;
            }
            if (!seen.Add(type))
                continue;
            found.Add(type);
            foreach (var (_, memberType) in Members(type))
                pending.Enqueue(memberType);
            // An interface's own properties do not include those of the interfaces it extends.
            if (type.IsInterface)
            {
                foreach (var inherited in type.GetInterfaces())
                    pending.Enqueue(inherited);
            }
        }
        return found;
    }

    /// <summary>The readable members of <paramref name="type"/>, as Fluid reads them.</summary>
    public static IEnumerable<(string Name, Type Type)> Members(Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0 && property.GetMethod is { IsPublic: true })
                yield return (property.Name, property.PropertyType);
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            yield return (field.Name, field.FieldType);
        if (type.IsInterface)
        {
            foreach (var inherited in type.GetInterfaces())
            {
                foreach (var property in inherited.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (property.GetIndexParameters().Length == 0 && property.GetMethod is { IsPublic: true })
                        yield return (property.Name, property.PropertyType);
                }
            }
        }
    }

    public static Type? Member(Type type, string name)
    {
        foreach (var (member, memberType) in Members(type))
        {
            if (member == name)
                return Unwrap(memberType);
        }
        return null;
    }

    /// <summary>
    /// Values Fluid turns into its own strings, numbers, booleans and dates, which have no members: what
    /// <c>FluidValue.Create</c> converts before it looks for a dictionary, a collection or an object.
    /// </summary>
    public static bool IsScalar(Type type)
    {
        type = Unwrap(type);
        return type.IsEnum || Type.GetTypeCode(type) != TypeCode.Object || type == typeof(DateTimeOffset) ||
               type == typeof(TimeSpan) || typeof(IConvertible).IsAssignableFrom(type) ||
               typeof(IFormattable).IsAssignableFrom(type);
    }

    /// <summary>The value type of a dictionary Fluid reads by key, or null for any other type.</summary>
    public static Type? DictionaryValue(Type type)
    {
        if (IsScalar(type))
            return null;
        var generic = Implementations(type)
            .Where(candidate => candidate.IsGenericType &&
                                (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                                 candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)) &&
                                candidate.GetGenericArguments()[0] == typeof(string))
            .Select(candidate => candidate.GetGenericArguments()[1])
            .FirstOrDefault();
        return generic ?? (typeof(System.Collections.IDictionary).IsAssignableFrom(type) ? typeof(object) : null);
    }

    /// <summary>The element type of a collection other than a dictionary, or null for any other type.</summary>
    public static Type? Element(Type type)
    {
        if (IsScalar(type) || DictionaryValue(type) != null)
            return null;
        if (type.IsArray)
            return type.GetElementType();
        var generic = Implementations(type)
            .Where(candidate => candidate.IsGenericType &&
                                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(candidate => candidate.GetGenericArguments()[0])
            .FirstOrDefault();
        return generic ?? (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) ? typeof(object) : null);
    }

    /// <summary>Whether Fluid reads <paramref name="type"/> as an object with members.</summary>
    public static bool IsObject(Type type) =>
        !IsScalar(type) && DictionaryValue(type) == null && Element(type) == null;

    public static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static IEnumerable<Type> Implementations(Type type) =>
        type.IsInterface ? type.GetInterfaces().Prepend(type) : type.GetInterfaces();
}

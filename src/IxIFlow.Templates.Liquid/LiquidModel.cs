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

    /// <summary>Values Fluid turns into its own strings, numbers, booleans and dates, which have no members.</summary>
    public static bool IsScalar(Type type)
    {
        type = Unwrap(type);
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
               type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
               type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Guid);
    }

    /// <summary>The value type of a dictionary with string keys, or null for any other type.</summary>
    public static Type? DictionaryValue(Type type) =>
        Implementations(type)
            .Where(candidate => candidate.IsGenericType &&
                                (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                                 candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)) &&
                                candidate.GetGenericArguments()[0] == typeof(string))
            .Select(candidate => candidate.GetGenericArguments()[1])
            .FirstOrDefault();

    /// <summary>The element type of a collection other than a string or a dictionary, or null for any other type.</summary>
    public static Type? Element(Type type)
    {
        if (type == typeof(string) || DictionaryValue(type) != null)
            return null;
        if (type.IsArray)
            return type.GetElementType();
        return Implementations(type)
            .Where(candidate => candidate.IsGenericType &&
                                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(candidate => candidate.GetGenericArguments()[0])
            .FirstOrDefault();
    }

    public static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static IEnumerable<Type> Implementations(Type type) =>
        type.IsInterface ? type.GetInterfaces().Prepend(type) : type.GetInterfaces();
}

using System.Collections;
using System.Reflection;
using Fluid;

namespace IxIFlow.Templates.Liquid;

/// <summary>
/// What a Liquid template can read from a model type: the public instance properties and fields of the model and of
/// every type reachable through them, through collections and string-keyed dictionaries included. A member is
/// readable only when its declared type says what it holds: a member typed <c>object</c>, or a collection or
/// dictionary of <c>object</c>, could hold anything, so it is not readable at all. Nothing else is readable, so a
/// template reaches the model's members and no other object in the process (H-03).
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
            foreach (var member in Members(type).Where(member => member.Readable))
                pending.Enqueue(member.Type);
            // An interface's own properties do not include those of the interfaces it extends.
            if (type.IsInterface)
            {
                foreach (var inherited in type.GetInterfaces())
                    pending.Enqueue(inherited);
            }
        }
        return found;
    }

    /// <summary>The public instance properties and fields of <paramref name="type"/>, readable or not.</summary>
    public static IEnumerable<ModelMember> Members(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).AsEnumerable();
        if (type.IsInterface)
        {
            properties = properties.Concat(type.GetInterfaces()
                .SelectMany(inherited => inherited.GetProperties(BindingFlags.Public | BindingFlags.Instance)));
        }
        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length == 0 && property.GetMethod is { IsPublic: true })
                yield return new ModelMember(property.Name, Unwrap(property.PropertyType), property.GetValue);
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            yield return new ModelMember(field.Name, Unwrap(field.FieldType), field.GetValue);
    }

    /// <summary>The member of <paramref name="type"/> named <paramref name="name"/>, readable or not.</summary>
    public static ModelMember? Member(Type type, string name) =>
        Members(type).FirstOrDefault(member => member.Name == name);

    /// <summary>The accessors a render reads <paramref name="type"/>'s readable members through.</summary>
    public static IEnumerable<KeyValuePair<string, IMemberAccessor>> Accessors(Type type) =>
        Members(type)
            .Where(member => member.Readable)
            .GroupBy(member => member.Name, StringComparer.Ordinal)
            .Select(group => KeyValuePair.Create(group.Key, (IMemberAccessor)new Accessor(group.First().Get)));

    /// <summary>
    /// Whether a value declared as <paramref name="type"/> is readable: a scalar, an object of a known type, or a
    /// collection or dictionary of readable values.
    /// </summary>
    public static bool Readable(Type type)
    {
        // A collection of itself, such as a tree node enumerating its children, is as readable as its members.
        var seen = new HashSet<Type>();
        for (type = Unwrap(type); seen.Add(type); type = Unwrap(type))
        {
            if (type == typeof(object))
                return false;
            if (IsScalar(type))
                return true;
            if (DictionaryValue(type) is { } value)
                type = value;
            else if (Element(type) is { } element)
                type = element;
            else
                return true;
        }
        return true;
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

    /// <summary>
    /// The value type of a dictionary Fluid reads by key, or null for any other type. Fluid reads a dictionary by
    /// key only when it implements <see cref="IDictionary"/> or <see cref="IDictionary{TKey,TValue}"/>; a dictionary
    /// keyed by anything but strings, or a non-generic one, holds values of unknown type.
    /// </summary>
    public static Type? DictionaryValue(Type type)
    {
        if (IsScalar(type))
            return null;
        var generic = Implementations(type)
            .FirstOrDefault(candidate => candidate.IsGenericType &&
                                         candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        if (generic != null)
            return generic.GetGenericArguments()[0] == typeof(string) ? generic.GetGenericArguments()[1] : typeof(object);
        return typeof(IDictionary).IsAssignableFrom(type) ? typeof(object) : null;
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
        return generic ?? (typeof(IEnumerable).IsAssignableFrom(type) ? typeof(object) : null);
    }

    /// <summary>Whether Fluid reads <paramref name="type"/> as an object with members.</summary>
    public static bool IsObject(Type type) =>
        !IsScalar(type) && DictionaryValue(type) == null && Element(type) == null;

    public static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static IEnumerable<Type> Implementations(Type type) =>
        type.IsInterface ? type.GetInterfaces().Prepend(type) : type.GetInterfaces();

    private sealed class Accessor(Func<object?, object?> get) : IMemberAccessor
    {
        public object? Get(object obj, string name, TemplateContext ctx) => get(obj);
    }
}

/// <summary>A public property or field of a model type, and whether a template may read it.</summary>
internal sealed record ModelMember(string Name, Type Type, Func<object?, object?> Get)
{
    public bool Readable => LiquidModel.Readable(Type);
}

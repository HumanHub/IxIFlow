using System.Collections;
using System.Reflection;

namespace IxIFlow.Templates;

/// <summary>The declared, closed read surfaces supported by templates, independent of an engine.</summary>
public static class TemplateModelTypes
{
    public static void Validate(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.IsVisible || type.ContainsGenericParameters || type.IsPointer || type.IsByRef ||
            type.IsFunctionPointer || type.IsByRefLike || type == typeof(object) || type == typeof(void) ||
            !IsObject(type) ||
            typeof(Delegate).IsAssignableFrom(type) || !Members(type).Any(Readable))
            throw new ArgumentException($"'{type}' is not a visible, closed object model with readable typed public members", nameof(type));
    }

    private static IEnumerable<Type> Members(Type type) => (type.IsInterface ? type.GetInterfaces().Prepend(type) : [type])
        .SelectMany(surface => surface.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0)
            .Select(property => property.PropertyType)
            .Concat(surface.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(field => field.FieldType)));

    private static bool Readable(Type type)
    {
        var seen = new HashSet<Type>();
        while (seen.Add(type))
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(object) || type.ContainsGenericParameters || type.IsPointer || type.IsByRef || type.IsFunctionPointer || type.IsByRefLike) return false;
            if (Scalar(type)) return true;
            var implementations = type.GetInterfaces().Prepend(type).ToArray();
            var dictionary = implementations.FirstOrDefault(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            if (dictionary != null)
            {
                type = dictionary.GetGenericArguments()[0] == typeof(string) ? dictionary.GetGenericArguments()[1] : typeof(object);
                continue;
            }
            if (typeof(IDictionary).IsAssignableFrom(type)) return false;
            if (type.IsArray) { type = type.GetElementType()!; continue; }
            var collection = implementations.FirstOrDefault(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (collection != null) { type = collection.GetGenericArguments()[0]; continue; }
            return !typeof(IEnumerable).IsAssignableFrom(type) && type.IsVisible;
        }
        return true;
    }

    private static bool IsObject(Type type) => !Scalar(Nullable.GetUnderlyingType(type) ?? type) && !typeof(IEnumerable).IsAssignableFrom(type);
    private static bool Scalar(Type type) => type.IsEnum || Type.GetTypeCode(type) != TypeCode.Object ||
        type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || typeof(IConvertible).IsAssignableFrom(type) || typeof(IFormattable).IsAssignableFrom(type);
}

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace IxIFlow.Core.Runtime;

/// <summary>The versioned, complete label-state contract shared by body and edge switches.</summary>
internal static class SwitchLabelSignature
{
    internal static object? Snapshot(object? value)
    {
        _ = Of(value); // Validate every stored member before accepting the snapshot.
        return value == null ? null : RuntimeHelpers.GetObjectValue(value);
    }

    internal static string Of(object? value)
    {
        if (value != null) ValidateType(value.GetType(), new HashSet<Type>());
        var result = new StringBuilder("switch-label/1;");
        Write(result, value, new HashSet<Type>());
        return result.ToString();
    }

    internal static void ValidateType(Type type) => ValidateType(type, new HashSet<Type>());

    private static void ValidateType(Type type, HashSet<Type> path)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type.IsEnum || type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr) ||
            type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
            type == typeof(TimeSpan) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Guid)) return;
        if (!type.IsValueType || type.IsPointer || type.IsByRefLike ||
            type == typeof(IntPtr) || type == typeof(UIntPtr) || type.IsDefined(typeof(CompilerGeneratedAttribute), false) || !path.Add(type))
            throw new ArgumentException($"Unsupported or opaque switch label state '{type}'");
        try
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (type.GetCustomAttribute<InlineArrayAttribute>() is { } inline &&
                (inline.Length <= 0 || fields.Length != 1))
                throw new ArgumentException($"Uninspectable inline-array switch label layout '{type}'");
            if (fields.Length == 0 && type.Assembly == typeof(int).Assembly)
                throw new ArgumentException($"Uninspectable switch label layout '{type}'");
            foreach (var field in fields)
            {
                _ = Identity(field);
                ValidateType(field.FieldType, path);
            }
        }
        finally { path.Remove(type); }
    }

    private static void Token(StringBuilder text, string value) =>
        text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');

    private static void Write(StringBuilder text, object? value, HashSet<Type> path)
    {
        if (value == null) { Token(text, "null"); return; }
        var type = value.GetType();
        Token(text, WorkflowTypeIdentity.StableName(type));
        if (type.IsEnum)
        {
            Write(text, Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture), path);
            return;
        }
        string? scalar = value switch
        {
            // Encode UTF-16 code units directly: JSON/UTF-8 replacement must not merge lone surrogates.
            string s => string.Concat(s.Select(character => ((int)character).ToString("X4", CultureInfo.InvariantCulture))),
            bool b => b ? "true" : "false",
            char c => ((int)c).ToString(CultureInfo.InvariantCulture),
            byte or sbyte or short or ushort or int or uint or long or ulong =>
                ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
            float f => BitConverter.SingleToInt32Bits(f).ToString("X8", CultureInfo.InvariantCulture),
            double d => BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture),
            decimal d => string.Join(",", decimal.GetBits(d).Select(i => i.ToString("X8", CultureInfo.InvariantCulture))),
            DateTime d => d.Ticks.ToString(CultureInfo.InvariantCulture) + "/" + (int)d.Kind,
            DateTimeOffset d => d.Ticks.ToString(CultureInfo.InvariantCulture) + "/" + d.Offset.Ticks.ToString(CultureInfo.InvariantCulture),
            TimeSpan t => t.Ticks.ToString(CultureInfo.InvariantCulture),
            DateOnly d => d.DayNumber.ToString(CultureInfo.InvariantCulture),
            TimeOnly t => t.Ticks.ToString(CultureInfo.InvariantCulture),
            Guid g => g.ToString("N"),
            _ => null
        };
        if (scalar != null) { Token(text, scalar); return; }
        if (!type.IsValueType || type.IsPointer || type.IsByRefLike ||
            type == typeof(IntPtr) || type == typeof(UIntPtr) ||
            type.IsDefined(typeof(CompilerGeneratedAttribute), false) || !path.Add(type))
            throw new ArgumentException($"Unsupported or opaque switch label state '{type}'");
        try
        {
            if (type.GetCustomAttribute<InlineArrayAttribute>() is { } inline)
            {
                var element = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single().FieldType;
                Token(text, "inline-array");
                Token(text, inline.Length.ToString(CultureInfo.InvariantCulture));
                Token(text, WorkflowTypeIdentity.StableName(element));
                var values = (object?[])typeof(SwitchLabelSignature).GetMethod(nameof(InlineElements), BindingFlags.Static | BindingFlags.NonPublic)!
                    .MakeGenericMethod(type, element).Invoke(null, [value, inline.Length])!;
                foreach (var item in values) Write(text, item, path);
                return;
            }
            var members = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(field => (Field: field, Identity: Identity(field))).OrderBy(pair => pair.Identity, StringComparer.Ordinal).ToArray();
            if (members.Length == 0 && type.Assembly == typeof(int).Assembly)
                throw new ArgumentException($"Uninspectable switch label layout '{type}'");
            Token(text, members.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var (field, identity) in members)
            {
                if (field.FieldType.IsPointer || field.FieldType.IsByRef ||
                    typeof(Delegate).IsAssignableFrom(field.FieldType) ||
                    !field.FieldType.IsValueType && field.FieldType != typeof(string))
                    throw new ArgumentException($"Unsupported switch label member '{identity}' of type '{field.FieldType}'");
                Token(text, identity);
                Token(text, WorkflowTypeIdentity.StableName(field.FieldType));
                Write(text, field.GetValue(value), path);
            }
        }
        finally { path.Remove(type); }
    }

    private static object?[] InlineElements<TArray, TElement>(object value, int length) where TArray : struct
    {
        var copy = (TArray)value;
        ref var first = ref Unsafe.As<TArray, TElement>(ref copy);
        var elements = new object?[length];
        for (var index = 0; index < length; index++) elements[index] = Unsafe.Add(ref first, index);
        return elements;
    }

    private static string Identity(FieldInfo field)
    {
        var owner = WorkflowTypeIdentity.StableName(field.DeclaringType);
        if (!field.IsDefined(typeof(CompilerGeneratedAttribute), false) && !field.Name.Contains('<') && !field.Name.Contains('>')) return owner + "/field/" + field.Name;
        const string suffix = ">k__BackingField";
        if (field.Name.StartsWith('<') && field.Name.EndsWith(suffix, StringComparison.Ordinal))
        {
            var name = field.Name[1..^suffix.Length];
            var property = field.DeclaringType!.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.PropertyType == field.FieldType && property.GetIndexParameters().Length == 0)
                return owner + "/property/" + name;
        }
        throw new ArgumentException($"Switch label storage cannot be normalized for '{owner}'");
    }
}

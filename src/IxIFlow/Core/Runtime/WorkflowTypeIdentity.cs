using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace IxIFlow.Core.Runtime;

/// <summary>Structure names and versioned saved identities, resolved only in the current definition's context.</summary>
public static class WorkflowTypeIdentity
{
    private const string Prefix = "ixi-type:1:";
    private static readonly AsyncLocal<AssemblyLoadContext?> Context = new();
    internal static AssemblyLoadContext? CurrentContext => Context.Value;

    /// <summary>A recursive, length-prefixed identifier. N names a type; V/A name vector/ranked arrays.</summary>
    public static string SavedName(Type type)
    {
        var text = new StringBuilder(Prefix);
        Write(type, text);
        return text.ToString();
    }

    private static void Write(Type type, StringBuilder text)
    {
        if (type.IsArray)
        {
            text.Append(type.IsSZArray ? 'V' : 'A');
            if (!type.IsSZArray) text.Append(type.GetArrayRank()).Append(':');
            Write(type.GetElementType()!, text);
            return;
        }
        var assembly = type.Assembly.GetName().Name!;
        var name = (type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type).FullName
            ?? throw new ArgumentException("An open type parameter cannot be saved", nameof(type));
        var arguments = type.IsConstructedGenericType ? type.GetGenericArguments() : [];
        text.Append('N').Append(assembly.Length).Append(':').Append(assembly)
            .Append(name.Length).Append(':').Append(name).Append(arguments.Length).Append(':');
        foreach (var argument in arguments) Write(argument, text);
    }

    internal static IDisposable Enter(AssemblyLoadContext? context)
    {
        var previous = Context.Value;
        Context.Value = context;
        return new TypeScope(previous, (context ?? AssemblyLoadContext.Default).EnterContextualReflection());
    }

    private sealed class TypeScope(AssemblyLoadContext? previous, AssemblyLoadContext.ContextualReflectionScope reflection) : IDisposable
    {
        public void Dispose()
        {
            reflection.Dispose();
            Context.Value = previous;
        }
    }

    public static string StableName(Type? type)
    {
        if (type == null)
            return "";
        if (type.IsArray)
            return $"{StableName(type.GetElementType())}[{new string(',', type.GetArrayRank() - 1)}]";
        if (type.IsConstructedGenericType)
            return $"{StableName(type.GetGenericTypeDefinition())}<" +
                string.Join(",", type.GetGenericArguments().Select(StableName)) + ">";
        return $"{type.FullName}, {type.Assembly.GetName().Name}";
    }

    public static Type? Resolve(string? savedName)
    {
        if (savedName?.StartsWith(Prefix, StringComparison.Ordinal) != true) return null;
        try
        {
            var position = Prefix.Length;
            var type = Read(savedName, ref position, 0);
            return position == savedName.Length ? type : null;
        }
        catch (Exception error) when (error is ArgumentException or FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException or FormatException or OverflowException)
        { return null; }
    }

    private static int Count(string text, ref int position)
    {
        var end = text.IndexOf(':', position);
        if (end <= position || end - position > 6 || !int.TryParse(text.AsSpan(position, end - position),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count))
            throw new FormatException("Invalid type identifier length");
        position = end + 1;
        return count;
    }

    private static string Part(string text, ref int position)
    {
        var length = Count(text, ref position);
        if (length > text.Length - position) throw new FormatException("Truncated type identifier");
        var value = text.Substring(position, length);
        position += length;
        return value;
    }

    private static Type? Read(string text, ref int position, int depth)
    {
        if (depth > 64 || position >= text.Length) throw new FormatException("Invalid type identifier nesting");
        var kind = text[position++];
        if (kind is 'V' or 'A')
        {
            var rank = kind == 'A' ? Count(text, ref position) : 0;
            var element = Read(text, ref position, depth + 1);
            return element == null ? null : kind == 'V' ? element.MakeArrayType() : element.MakeArrayType(rank);
        }
        if (kind != 'N') throw new FormatException("Unknown type identifier node");
        var assembly = Part(text, ref position);
        var name = Part(text, ref position);
        var count = Count(text, ref position);
        if (count > 128) throw new FormatException("Too many generic arguments");
        var arguments = new Type?[count];
        for (var index = 0; index < count; index++) arguments[index] = Read(text, ref position, depth + 1);
        var type = (Context.Value ?? AssemblyLoadContext.Default).LoadFromAssemblyName(new AssemblyName(assembly))
            .GetType(name, throwOnError: false);
        return type == null || count == 0 ? type : arguments.Any(argument => argument == null) ? null : type.MakeGenericType(arguments!);
    }
}

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

    internal static string CSharpName(Type type)
    {
        if (type.IsArray) return CSharpName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        var arguments = type.IsGenericType ? type.GetGenericArguments() : [];
        var chain = new Stack<Type>();
        for (var current = type; current != null; current = current.DeclaringType) chain.Push(current);
        var parts = new List<string>();
        var offset = 0;
        foreach (var part in chain)
        {
            var name = Escape(part.Name.Split('`')[0]);
            var count = part.IsGenericType ? part.GetGenericArguments().Length - (part.DeclaringType?.GetGenericArguments().Length ?? 0) : 0;
            if (count > 0) { name += "<" + string.Join(", ", arguments.Skip(offset).Take(count).Select(CSharpName)) + ">"; offset += count; }
            parts.Add(name);
        }
        return "global::" + (string.IsNullOrEmpty(type.Namespace) ? "" : string.Join(".", type.Namespace.Split('.').Select(Escape)) + ".") + string.Join(".", parts);
    }

    internal static string? CSharpName(string? saved)
    {
        if (!IsSavedName(saved)) return null;
        try
        {
            var position = Prefix.Length;
            var result = Display(saved!, ref position, 0);
            return position == saved!.Length ? result : null;
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException) { return null; }
    }

    private static string Display(string saved, ref int position, int depth)
    {
        if (depth > 64) throw new FormatException("Invalid type nesting");
        var kind = saved[position++];
        if (kind is 'A' or 'V')
        {
            var rank = kind == 'A' ? Count(saved, ref position) : 1;
            return Display(saved, ref position, depth + 1) + "[" + new string(',', rank - 1) + "]";
        }
        _ = Part(saved, ref position);
        var name = Part(saved, ref position);
        var count = Count(saved, ref position);
        var arguments = new string[count];
        for (var index = 0; index < count; index++) arguments[index] = Display(saved, ref position, depth + 1);
        var offset = 0;
        var parts = name.Split('+').Select(part =>
        {
            var tick = part.IndexOf('`');
            if (tick < 0) return string.Join(".", part.Split('.').Select(Escape));
            var arity = int.Parse(part.AsSpan(tick + 1), System.Globalization.CultureInfo.InvariantCulture);
            var result = string.Join(".", part[..tick].Split('.').Select(Escape)) + "<" + string.Join(", ", arguments.Skip(offset).Take(arity)) + ">";
            offset += arity;
            return result;
        }).ToArray();
        if (offset != count) throw new FormatException("Invalid generic type arity");
        return "global::" + string.Join(".", parts);
    }

    private static string Escape(string name) => Keywords.Contains(name) ? "@" + name : name;
    private static readonly HashSet<string> Keywords = new(("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while").Split(' '), StringComparer.Ordinal);

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

    internal static bool IsSavedName(string? name)
    {
        if (name?.StartsWith(Prefix, StringComparison.Ordinal) != true) return false;
        try
        {
            var position = Prefix.Length;
            _ = Read(name, ref position, 0, resolve: false);
            return position == name.Length;
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException) { return false; }
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

    private static Type? Read(string text, ref int position, int depth, bool resolve = true)
    {
        if (depth > 64 || position >= text.Length) throw new FormatException("Invalid type identifier nesting");
        var kind = text[position++];
        if (kind is 'V' or 'A')
        {
            var rank = kind == 'A' ? Count(text, ref position) : 0;
            if (kind == 'A' && rank is < 1 or > 32) throw new FormatException("Invalid array rank");
            var element = Read(text, ref position, depth + 1, resolve);
            return element == null ? null : kind == 'V' ? element.MakeArrayType() : element.MakeArrayType(rank);
        }
        if (kind != 'N') throw new FormatException("Unknown type identifier node");
        var assembly = Part(text, ref position);
        var name = Part(text, ref position);
        if (assembly.Length == 0 || name.Length == 0) throw new FormatException("Empty type identifier part");
        var count = Count(text, ref position);
        if (count > 128) throw new FormatException("Too many generic arguments");
        var arguments = new Type?[count];
        for (var index = 0; index < count; index++) arguments[index] = Read(text, ref position, depth + 1, resolve);
        if (!resolve) return null;
        var type = (Context.Value ?? AssemblyLoadContext.Default).LoadFromAssemblyName(new AssemblyName(assembly))
            .GetType(name, throwOnError: false);
        return type == null || count == 0 ? type : arguments.Any(argument => argument == null) ? null : type.MakeGenericType(arguments!);
    }
}

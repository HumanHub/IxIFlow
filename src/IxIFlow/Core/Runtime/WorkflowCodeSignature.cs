using System.Runtime.CompilerServices;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;

namespace IxIFlow.Core.Runtime;

/// <summary>Identifies code-bound behavior without persisting delegates in a checkpoint.</summary>
internal static class WorkflowCodeSignature
{
    /// <summary>
    /// The signature of <paramref name="behavior"/>. A <c>ForEach</c> handle it captures is named by its
    /// step's place in <paramref name="handles"/>, never by its label (D-34).
    /// </summary>
    public static string Of(Delegate? behavior, IReadOnlyDictionary<ItemScope, string>? handles = null)
    {
        if (behavior == null)
            return "";
        return Describe(behavior, NewPath(), 0, handles);
    }

    public static string OfCapturedValue(object? value, IReadOnlyDictionary<ItemScope, string>? handles = null) =>
        DescribeValue(value, NewPath(), 0, handles);

    // The objects being described, outermost first, with their depth, so a cycle can name where it returns.
    private static Dictionary<object, int> NewPath() => new(ReferenceEqualityComparer.Instance);

    private static string Describe(Delegate behavior, Dictionary<object, int> visited, int depth,
        IReadOnlyDictionary<ItemScope, string>? handles)
    {
        var method = behavior.Method;
        if (method.Module.Assembly == typeof(WorkflowCodeSignature).Assembly)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                "An engine runtime adapter cannot be fingerprinted; retain the author-supplied behavior or expression");
        var code = LambdaCode.Of(method);
        var body = code != LambdaCode.Dynamic;
        var owner = LambdaCode.TypeName(method.DeclaringType);
        var target = behavior.Target == null ? "static" : DescribeValue(behavior.Target, visited, depth, handles);
        var methodName = !body && method.Name.StartsWith("lambda_method", StringComparison.Ordinal)
            ? "compiled-expression" : LambdaCode.MethodName(method);
        return $"{owner}.{methodName}:{code}:{target}";
    }

    private static string DescribeValue(object? value, Dictionary<object, int> visited, int depth,
        IReadOnlyDictionary<ItemScope, string>? handles)
    {
        if (value == null)
            return "null";
        if (value is Delegate nested)
            return depth < 8 ? Describe(nested, visited, depth + 1, handles) : "nested-delegate";
        if (value is Type type)
            return WorkflowTypeIdentity.StableName(type);
        // A ForEach handle is known by its step's place and its item type. Its label is for diagnostics, and
        // where the place is not known yet, as in an expression signature made at build, the type alone stands.
        if (value is ItemScope item)
            return $"item:{(handles != null && handles.TryGetValue(item, out var place) ? place : "")}:{
                WorkflowTypeIdentity.StableName(item.ItemType)}";
        if (value is string text)
            return $"string:{text.Length}:{text}";
        if (value is bool or char or Guid or TimeSpan or DateTime or DateTimeOffset ||
            value.GetType().IsPrimitive || value.GetType().IsEnum || value is decimal)
            return $"{WorkflowTypeIdentity.StableName(value.GetType())}:{
                Convert.ToString(value, CultureInfo.InvariantCulture)}";

        var runtimeType = value.GetType();
        if (!runtimeType.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)))
        {
            try
            {
                return WorkflowTypeIdentity.StableName(runtimeType) + ":" +
                    System.Text.Json.JsonSerializer.Serialize(value, runtimeType);
            }
            catch (Exception error) when (error is NotSupportedException or
                System.Text.Json.JsonException)
            {
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Captured value of type '{runtimeType.FullName}' cannot be fingerprinted; " +
                    "capture serializable values instead", error);
            }
        }
        // A cycle through captured values is named by the depth it returns to, not followed.
        if (!visited.TryAdd(value, depth))
            return "cycle:" + visited[value];
        if (depth >= 8)
        {
            visited.Remove(value);
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                "Captured closure is too deep to fingerprint");
        }
        try
        {
            var fields = runtimeType.GetFields(BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic);
            // C# caches a nested lambda in a field of its closure, filled when the lambda is first made,
            // which may be after the definition is registered. The lambda's code is part of the code of
            // the behavior that makes it (LambdaCode).
            return LambdaCode.TypeName(runtimeType) + "{" + string.Join(",",
                fields.Where(field => !field.Name.StartsWith("<>9__", StringComparison.Ordinal))
                    .OrderBy(LambdaCode.FieldName, StringComparer.Ordinal)
                    .Select(field => LambdaCode.FieldName(field) + "=" +
                        DescribeValue(field.GetValue(value), visited, depth + 1, handles))) + "}";
        }
        finally
        {
            visited.Remove(value);
        }
    }
}

/// <summary>
/// Hashes IL opcodes, stable resolved operands and local types, followed in order by the code of
/// generated lambdas and local functions it reaches. Other called method bodies are not read.
/// A compiled expression has no IL and is <see cref="Dynamic"/>; its shape is signed separately.
/// Codes are kept per method for as long as the method is loaded; dynamic methods are not kept.
/// </summary>
internal static class LambdaCode
{
    public const string Dynamic = "dynamic";

    private static readonly ConditionalWeakTable<MethodBase, string> Codes = new();
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    public static string Of(MethodBase method)
    {
        if (Codes.TryGetValue(method, out var known))
            return known;
        var code = Compute(method, []);
        if (code != Dynamic)
            Codes.AddOrUpdate(method, code);
        return code;
    }

    private static string Compute(MethodBase method, HashSet<MethodBase> seen)
    {
        MethodBody? body;
        try { body = method.GetMethodBody(); }
        catch (InvalidOperationException) { body = null; }
        var il = body?.GetILAsByteArray();
        if (il == null)
            return Dynamic;
        if (!seen.Add(method))
            return "recursive";
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(body!.LocalVariables.Count);
        foreach (var local in body.LocalVariables)
        {
            writer.Write(TypeName(local.LocalType));
            writer.Write(local.IsPinned);
        }
        var generated = new List<MethodBase>();
        var positions = new Dictionary<MethodBase, int>();
        var position = 0;
        while (position < il.Length)
        {
            var value = (short)il[position++];
            if (value == 0xFE && position < il.Length)
                value = unchecked((short)(0xFE00 | il[position++]));
            writer.Write(value);
            if (!OpCodesByValue.TryGetValue(value, out var code))
                break;
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, position),
                _ => 4
            };
            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField or
                OperandType.InlineType or OperandType.InlineTok or OperandType.InlineString or OperandType.InlineSig)
            {
                var token = BitConverter.ToInt32(il, position);
                try
                {
                    if (code.OperandType == OperandType.InlineString)
                        writer.Write("string:" + method.Module.ResolveString(token));
                    else if (code.OperandType == OperandType.InlineSig)
                        writer.Write(new Signature(method, method.Module.ResolveSignature(token)).Read());
                    else
                    {
                        var member = method.Module.ResolveMember(token, TypeArguments(method), MethodArguments(method));
                        writer.Write(member switch
                        {
                            Type type => "type:" + TypeName(type),
                            FieldInfo field => "field:" + TypeName(field.DeclaringType) + "." + FieldName(field) +
                                ":" + TypeName(field.FieldType),
                            MethodBase target => "method:" + MethodIdentity(target),
                            _ => "unresolved:" + code.Name
                        });
                        if (member is MethodBase nested && IsGenerated(nested) &&
                            (code == OpCodes.Ldftn || code == OpCodes.Ldvirtftn ||
                             code == OpCodes.Call || code == OpCodes.Callvirt))
                            generated.Add(nested);
                    }
                }
                catch (Exception error) when (error is ArgumentException or BadImageFormatException or
                    InvalidOperationException or NotSupportedException or TypeLoadException or MissingMemberException or
                    FileNotFoundException or FileLoadException)
                {
                    writer.Write("unresolved:" + code.Name);
                }
            }
            else
                writer.Write(il, position, size);
            position += size;
        }
        writer.Flush();
        var hash = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        return string.Join("+", generated.Select(lambda => Compute(lambda, seen)).Prepend(hash));

        string MethodIdentity(MethodBase target)
        {
            var name = MethodName(target);
            if (IsLambda(target))
            {
                if (!positions.TryGetValue(target, out var index))
                    positions.Add(target, index = positions.Count);
                name += ":" + index.ToString(CultureInfo.InvariantCulture);
            }
            return TypeName(target.DeclaringType) + "." + name + "<" +
                string.Join(",", MethodArguments(target)?.Select(TypeName) ?? []) + ">(" +
                string.Join(",", target.GetParameters().Select(parameter => TypeName(parameter.ParameterType))) +
                "):" + TypeName(target is MethodInfo info ? info.ReturnType : typeof(void));
        }
    }

    private static Type[]? TypeArguments(MethodBase method) =>
        method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;

    private static Type[]? MethodArguments(MethodBase method) =>
        method.IsGenericMethod ? method.GetGenericArguments() : null;

    private static bool IsLambda(MethodBase method) => method.Name.Contains(">b__", StringComparison.Ordinal);

    private static bool IsGenerated(MethodBase method) => IsLambda(method) ||
        method.Name.Contains(">g__", StringComparison.Ordinal);

    internal static string MethodName(MethodBase method)
    {
        if (IsLambda(method))
            return "lambda";
        var start = method.Name.IndexOf(">g__", StringComparison.Ordinal);
        return start < 0 ? method.Name : "local:" + method.Name[(start + 4)..method.Name.IndexOf('|', start)];
    }

    internal static string FieldName(FieldInfo field) =>
        field.Name.StartsWith("<>9__", StringComparison.Ordinal) ? "lambda-cache" :
        field.Name.StartsWith("CS$<>8__locals", StringComparison.Ordinal) ? "closure-link" : field.Name;

    internal static string TypeName(Type? type) => TypeName(type, []);

    private static string TypeName(Type? type, HashSet<Type> path)
    {
        if (type == null)
            return "";
        if (type.IsGenericParameter)
            return (type.DeclaringMethod == null ? "!" : "!!") + type.GenericParameterPosition;
        if (type.HasElementType)
            return TypeName(type.GetElementType(), path) + (type.IsByRef ? "&" : type.IsPointer ? "*" :
                type.IsSZArray ? "[]" : "[" + new string(',', type.GetArrayRank() - 1) + "*]");
        if (type.IsFunctionPointer)
            return "fnptr(" + string.Join(",", type.GetFunctionPointerCallingConventions().Select(TypeName)) + ";" +
                string.Join(",", type.GetFunctionPointerParameterTypes().Select(TypeName)) + "):" +
                TypeName(type.GetFunctionPointerReturnType(), path);
        if (type.IsConstructedGenericType)
            return TypeName(type.GetGenericTypeDefinition(), path) + "<" +
                string.Join(",", type.GetGenericArguments().Select(argument => TypeName(argument, path))) + ">";
        if (type.IsDefined(typeof(CompilerGeneratedAttribute)) &&
            (type.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal) ||
             type.Name == "<>c" || type.Name.StartsWith("<>c`", StringComparison.Ordinal)))
        {
            if (!path.Add(type))
                return "closure-cycle";
            try
            {
                return TypeName(type.DeclaringType, path) + "{closure:" + string.Join(",",
                    type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(field => !field.Name.StartsWith("<>9__", StringComparison.Ordinal))
                        .Select(field => FieldName(field) + ":" + TypeName(field.FieldType, path))
                        .OrderBy(field => field, StringComparer.Ordinal)) + "}";
            }
            finally { path.Remove(type); }
        }
        return type.FullName ?? type.Name;
    }

    // Standalone signatures contain compressed type tokens too.
    private sealed class Signature(MethodBase method, byte[] bytes)
    {
        private int position;

        public string Read()
        {
            var convention = Number();
            var generics = (convention & 0x10) == 0 ? 0 : Number();
            var count = Number();
            var result = ReadType();
            var parameters = new List<string>();
            for (var index = 0; index < count; index++)
            {
                var sentinel = position < bytes.Length && bytes[position] == 0x41;
                if (sentinel) position++;
                parameters.Add((sentinel ? "sentinel:" : "") + ReadType());
            }
            return $"signature:{convention}:{generics}({string.Join(",", parameters)}):{result}";
        }

        private string ReadType()
        {
            var kind = Number();
            return kind switch
            {
                0x0F or 0x10 or 0x1D or 0x45 => kind + ":" + ReadType(),
                0x11 or 0x12 => kind + ":" + TokenType(),
                0x13 => GenericParameter(TypeArguments(method), "!"),
                0x1E => GenericParameter(MethodArguments(method), "!!"),
                0x14 => ArrayType(),
                0x15 => GenericType(),
                0x1B => "fnptr:" + Read(),
                0x1F or 0x20 => kind + ":" + TokenType() + ":" + ReadType(),
                >= 0x01 and <= 0x0E or 0x16 or 0x18 or 0x19 or 0x1C => "primitive:" + kind,
                _ => throw new BadImageFormatException("Unsupported signature type")
            };
        }

        private string GenericParameter(Type[]? arguments, string prefix)
        {
            var index = Number();
            return arguments != null && index < arguments.Length ? TypeName(arguments[index]) : prefix + index;
        }

        private string TokenType()
        {
            var coded = Number();
            var table = (coded & 3) switch
            {
                0 => 0x02000000,
                1 => 0x01000000,
                2 => 0x1B000000,
                _ => throw new BadImageFormatException("Invalid signature type token")
            };
            return TypeName(method.Module.ResolveType(table | (coded >> 2), TypeArguments(method), MethodArguments(method)));
        }

        private string GenericType()
        {
            var kind = Number();
            var type = TokenType();
            var count = Number();
            var arguments = new List<string>();
            for (var index = 0; index < count; index++) arguments.Add(ReadType());
            return kind + ":" + type + "<" + string.Join(",", arguments) + ">";
        }

        private string ArrayType()
        {
            var type = ReadType();
            var rank = Number();
            var sizes = Numbers();
            var bounds = Numbers();
            return $"{type}[{rank};{sizes};{bounds}]";
        }

        private string Numbers()
        {
            var count = Number();
            var values = new List<int>();
            for (var index = 0; index < count; index++) values.Add(Number());
            return string.Join(",", values);
        }

        private int Number()
        {
            if (position >= bytes.Length) throw new BadImageFormatException("Incomplete signature");
            var first = bytes[position++];
            if ((first & 0x80) == 0) return first;
            var count = (first & 0xC0) == 0x80 ? 1 : (first & 0xE0) == 0xC0 ? 3 :
                throw new BadImageFormatException("Invalid compressed integer");
            if (position + count > bytes.Length) throw new BadImageFormatException("Incomplete signature");
            var value = first & (count == 1 ? 0x3F : 0x1F);
            for (var index = 0; index < count; index++) value = (value << 8) | bytes[position++];
            return value;
        }
    }
}

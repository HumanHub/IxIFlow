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
        var code = LambdaCode.Of(method);
        var body = code != LambdaCode.Dynamic;
        var owner = WorkflowTypeIdentity.StableName(method.DeclaringType);
        var target = behavior.Target == null ? "static" : DescribeValue(behavior.Target, visited, depth, handles);
        var methodName = !body && method.Name.StartsWith("lambda_method", StringComparison.Ordinal)
            ? "compiled-expression" : method.Name;
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
            return WorkflowTypeIdentity.StableName(runtimeType) + "{" + string.Join(",",
                fields.Where(field => !field.Name.StartsWith("<>9__", StringComparison.Ordinal))
                    .OrderBy(field => field.Name, StringComparer.Ordinal)
                    .Select(field => field.Name + "=" +
                        DescribeValue(field.GetValue(value), visited, depth + 1, handles))) + "}";
        }
        finally
        {
            visited.Remove(value);
        }
    }
}

/// <summary>
/// The code of a method as a hash of its IL and, in order, of the code of every lambda it creates and
/// every local function it uses, so a change inside them changes the code of the behavior that makes
/// them. Other methods it calls are not read. A compiled expression has no IL to read and is
/// <see cref="Dynamic"/>; its shape is signed by <see cref="WorkflowExpressionSignature"/>. Codes are kept
/// per method for as long as the method is loaded; dynamic methods are not kept.
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
        byte[]? body;
        try { body = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (InvalidOperationException) { body = null; }
        if (body == null)
            return Dynamic;
        if (!seen.Add(method))
            return "recursive";
        var nested = Generated(method, body)
            .Select(lambda => Compute(lambda, seen));
        return string.Join("+", nested.Prepend(Convert.ToHexString(SHA256.HashData(body))));
    }

    // The compiler-generated methods the IL loads with ldftn or calls: its lambdas and local functions.
    private static IEnumerable<MethodBase> Generated(MethodBase method, byte[] il)
    {
        var position = 0;
        while (position < il.Length)
        {
            var value = (short)il[position++];
            if (value == 0xFE && position < il.Length)
                value = unchecked((short)(0xFE00 | il[position++]));
            if (!OpCodesByValue.TryGetValue(value, out var code))
                yield break;
            if ((code == OpCodes.Ldftn || code == OpCodes.Call || code == OpCodes.Callvirt) &&
                position + 4 <= il.Length)
            {
                var token = BitConverter.ToInt32(il, position);
                MethodBase? target;
                try
                {
                    target = method.Module.ResolveMethod(token,
                        method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (Exception error) when (error is ArgumentException or BadImageFormatException)
                {
                    target = null;
                }
                if (target != null && (target.Name.Contains(">b__", StringComparison.Ordinal) ||
                                       target.Name.Contains(">g__", StringComparison.Ordinal)))
                    yield return target;
            }
            position += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch when position + 4 <= il.Length => 4 + 4 * BitConverter.ToInt32(il, position),
                _ => 4
            };
        }
    }
}

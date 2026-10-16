using System.Globalization;
using System.Reflection;
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
        return Describe(behavior, new HashSet<object>(ReferenceEqualityComparer.Instance), 0, handles);
    }

    public static string OfCapturedValue(object? value, IReadOnlyDictionary<ItemScope, string>? handles = null) =>
        DescribeValue(value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0, handles);

    private static string Describe(Delegate behavior, HashSet<object> visited, int depth,
        IReadOnlyDictionary<ItemScope, string>? handles)
    {
        var method = behavior.Method;
        byte[]? body;
        try { body = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (InvalidOperationException) { body = null; }
        var code = body == null ? "dynamic" : Convert.ToHexString(SHA256.HashData(body));
        var owner = WorkflowTypeIdentity.StableName(method.DeclaringType);
        var target = behavior.Target == null ? "static" : DescribeValue(behavior.Target, visited, depth, handles);
        var methodName = body == null && method.Name.StartsWith("lambda_method", StringComparison.Ordinal)
            ? "compiled-expression" : method.Name;
        return $"{owner}.{methodName}:{code}:{target}";
    }

    private static string DescribeValue(object? value, HashSet<object> visited, int depth,
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
        // A cycle through captured values is named, not followed.
        if (!visited.Add(value))
            return "cycle:" + WorkflowTypeIdentity.StableName(runtimeType);
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
            // which may be after the definition is registered. The lambda is signed where it is used.
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

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;

namespace IxIFlow.Core.Runtime;

/// <summary>Identifies code-bound behavior without persisting delegates in a checkpoint.</summary>
internal static class WorkflowCodeSignature
{
    public static string Of(Delegate? behavior)
    {
        if (behavior == null)
            return "";
        return Describe(behavior, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
    }

    public static string OfCapturedValue(object? value) =>
        DescribeValue(value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);

    private static string Describe(Delegate behavior, HashSet<object> visited, int depth)
    {
        var method = behavior.Method;
        byte[]? body;
        try { body = method.GetMethodBody()?.GetILAsByteArray(); }
        catch (InvalidOperationException) { body = null; }
        var code = body == null ? "dynamic" : Convert.ToHexString(SHA256.HashData(body));
        var owner = WorkflowTypeIdentity.StableName(method.DeclaringType);
        var target = behavior.Target == null ? "static" : DescribeValue(behavior.Target, visited, depth);
        var methodName = body == null && method.Name.StartsWith("lambda_method", StringComparison.Ordinal)
            ? "compiled-expression" : method.Name;
        return $"{owner}.{methodName}:{code}:{target}";
    }

    private static string DescribeValue(object? value, HashSet<object> visited, int depth)
    {
        if (value == null)
            return "null";
        if (value is Delegate nested)
            return depth < 8 ? Describe(nested, visited, depth + 1) : "nested-delegate";
        if (value is Type type)
            return WorkflowTypeIdentity.StableName(type);
        if (value is string text)
            return $"string:{text.Length}:{text}";
        if (value is bool or char or Guid or TimeSpan or DateTime or DateTimeOffset ||
            value.GetType().IsPrimitive || value.GetType().IsEnum || value is decimal)
            return $"{WorkflowTypeIdentity.StableName(value.GetType())}:{
                Convert.ToString(value, CultureInfo.InvariantCulture)}";

        var runtimeType = value.GetType();
        if (!runtimeType.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute)) ||
            depth >= 8 || !visited.Add(value))
            return WorkflowTypeIdentity.StableName(runtimeType);
        try
        {
            var fields = runtimeType.GetFields(BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic);
            return WorkflowTypeIdentity.StableName(runtimeType) + "{" + string.Join(",",
                fields.OrderBy(field => field.Name, StringComparer.Ordinal)
                    .Select(field => field.Name + "=" +
                        DescribeValue(field.GetValue(value), visited, depth + 1))) + "}";
        }
        finally
        {
            visited.Remove(value);
        }
    }
}

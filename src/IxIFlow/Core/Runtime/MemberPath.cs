using System.Linq.Expressions;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The member chain an output writes, from its context parameter, such as <c>WorkflowData.Total</c>.
/// An index, or an element picked with <c>First</c>, <c>Single</c>, <c>Last</c> or <c>ElementAt</c>, is
/// written <c>[]</c>, whatever it is, so two writes into the same list overlap; a built-in cast is passed
/// through. A chain that starts at a <c>ForEach</c> item, <c>ctx.Item(...)</c>, begins with
/// <see cref="ItemRoot"/>.
/// </summary>
internal static class MemberPath
{
    public const string ItemRoot = "@item";

    /// <summary>The path, or null when the target is not a member and index chain on the parameter.</summary>
    public static string? Of(LambdaExpression expression)
    {
        var parts = new Stack<string>();
        var current = expression.Body;
        while (true)
        {
            switch (current)
            {
                case MemberExpression member:
                    parts.Push(member.Member.Name);
                    current = member.Expression;
                    continue;
                case IndexExpression index:
                    parts.Push("[]");
                    current = index.Object;
                    continue;
                case BinaryExpression { NodeType: ExpressionType.ArrayIndex } array:
                    parts.Push("[]");
                    current = array.Left;
                    continue;
                // A built-in cast keeps the object; a user-defined conversion may return another one.
                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or
                    ExpressionType.TypeAs, Method: null } cast:
                    current = cast.Operand;
                    continue;
                case MethodCallExpression call when IsItem(call):
                    return Join(ItemRoot, parts);
                case MethodCallExpression { Method.Name: "get_Item", Object: not null } indexer:
                    parts.Push("[]");
                    current = indexer.Object;
                    continue;
                case MethodCallExpression call when IsElementSelector(call):
                    parts.Push("[]");
                    current = call.Arguments[0];
                    continue;
                case ParameterExpression when parts.Count > 0:
                    return Join(null, parts);
                default:
                    return null;
            }
        }
    }

    /// <summary>True when writing <paramref name="path"/> may change <paramref name="target"/>: one contains the other.</summary>
    public static bool Overlaps(string? path, string target) =>
        path != null && (path == target || Inside(path, target) || Inside(target, path));

    private static bool Inside(string path, string owner) =>
        path.StartsWith(owner + ".", StringComparison.Ordinal) || path.StartsWith(owner + "[", StringComparison.Ordinal);

    // The element selectors of Enumerable return an element of their source, so they write like an index.
    private static bool IsElementSelector(MethodCallExpression call) =>
        call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count > 0 &&
        call.Method.Name is nameof(Enumerable.First) or nameof(Enumerable.FirstOrDefault) or nameof(Enumerable.Single)
            or nameof(Enumerable.SingleOrDefault) or nameof(Enumerable.Last) or nameof(Enumerable.LastOrDefault)
            or nameof(Enumerable.ElementAt) or nameof(Enumerable.ElementAtOrDefault);

    private static bool IsItem(MethodCallExpression call) =>
        call.Method.Name == nameof(WorkflowContext<object>.Item) && call.Method.DeclaringType is { IsGenericType: true } owner &&
        owner.GetGenericTypeDefinition() == typeof(WorkflowContext<>);

    private static string Join(string? root, Stack<string> parts)
    {
        var path = root ?? "";
        foreach (var part in parts)
            path = part == "[]" ? path + part : path.Length == 0 ? part : path + "." + part;
        return path;
    }
}

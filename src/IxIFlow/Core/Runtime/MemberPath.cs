using System.Linq.Expressions;

namespace IxIFlow.Core.Runtime;

/// <summary>The member chain an expression reads from its parameter, such as <c>WorkflowData.Total</c>.</summary>
internal static class MemberPath
{
    /// <summary>The dotted member path, or null when the body is not a plain member chain on the parameter.</summary>
    public static string? Of(LambdaExpression expression)
    {
        var members = new Stack<string>();
        var current = expression.Body;
        while (current is MemberExpression member)
        {
            members.Push(member.Member.Name);
            current = member.Expression;
        }
        return current is ParameterExpression && members.Count > 0 ? string.Join(".", members) : null;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="target"/> or a member inside it.</summary>
    public static bool Writes(string? path, string target) =>
        path != null && (path == target || path.StartsWith(target + ".", StringComparison.Ordinal));
}

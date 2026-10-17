using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace IxIFlow.Core.Runtime;

/// <summary>Records the expression shape and the values captured by a closure.</summary>
internal static class WorkflowExpressionSignature
{
    /// <summary>
    /// The signature of <paramref name="expression"/>. A <c>ForEach</c> handle it captures is named by its
    /// step's place in <paramref name="handles"/> (D-34).
    /// </summary>
    public static string Of(LambdaExpression expression, IReadOnlyDictionary<ItemScope, string>? handles = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var captures = new CapturedFields(handles);
        captures.Visit(expression.Body);
        return expression + "|" + string.Join(";", captures.Values
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal));
    }

    /// <summary>
    /// Returns <paramref name="expression"/> once its signature can be made, so a capture that cannot be
    /// fingerprinted fails where the expression is given. The signature itself is made when the workflow
    /// is registered, when the places of its handles are known.
    /// </summary>
    public static TExpression Checked<TExpression>(TExpression expression) where TExpression : LambdaExpression
    {
        Of(expression);
        return expression;
    }

    private sealed class CapturedFields(IReadOnlyDictionary<ItemScope, string>? handles) : ExpressionVisitor
    {
        public List<string> Values { get; } = [];

        protected override Expression VisitMember(MemberExpression node)
        {
            if (TryReadCapturedField(node, out var value))
                Values.Add(node + "=" + WorkflowCodeSignature.OfCapturedValue(value, handles));
            return base.VisitMember(node);
        }

        private static bool TryReadCapturedField(MemberExpression node, out object? value)
        {
            value = null;
            if (node.Member is not FieldInfo field || node.Expression == null ||
                !TryReadClosure(node.Expression, out var owner))
                return false;
            value = field.GetValue(owner);
            return true;
        }

        private static bool TryReadClosure(Expression expression, out object? value)
        {
            if (expression is ConstantExpression constant && constant.Value?.GetType()
                    .IsDefined(typeof(CompilerGeneratedAttribute)) == true)
            {
                value = constant.Value;
                return true;
            }
            if (expression is MemberExpression member && member.Member is FieldInfo field &&
                member.Expression != null && TryReadClosure(member.Expression, out var owner))
            {
                value = field.GetValue(owner);
                return true;
            }
            value = null;
            return false;
        }
    }
}

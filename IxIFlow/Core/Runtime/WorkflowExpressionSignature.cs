using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace IxIFlow.Core.Runtime;

/// <summary>Records the expression shape and scalar values captured by a closure.</summary>
internal static class WorkflowExpressionSignature
{
    public static string Of(LambdaExpression expression)
    {
        var captures = new CapturedFields();
        captures.Visit(expression.Body);
        return expression + "|" + string.Join(";", captures.Values
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal));
    }

    private sealed class CapturedFields : ExpressionVisitor
    {
        public List<string> Values { get; } = [];

        protected override Expression VisitMember(MemberExpression node)
        {
            if (TryReadCapturedField(node, out var value))
                Values.Add(node + "=" + WorkflowCodeSignature.OfCapturedValue(value));
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

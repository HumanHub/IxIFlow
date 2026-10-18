using System.Linq.Expressions;

namespace IxIFlow.Core;

internal class ReplaceParameterVisitor : ExpressionVisitor
{
    private readonly ParameterExpression _newParameter;
    private readonly ParameterExpression _oldParameter;

    private ReplaceParameterVisitor(ParameterExpression oldParameter, ParameterExpression newParameter)
    {
        _oldParameter = oldParameter;
        _newParameter = newParameter;
    }

    public static Expression Replace(Expression expression, ParameterExpression oldParameter,
        ParameterExpression newParameter)
    {
        var visitor = new ReplaceParameterVisitor(oldParameter, newParameter);
        return visitor.Visit(expression);
    }

    protected override Expression VisitParameter(ParameterExpression node)
    {
        return node == _oldParameter ? _newParameter : node;
    }
}

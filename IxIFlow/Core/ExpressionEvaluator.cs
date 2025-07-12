using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
///     Implementation of IExpressionEvaluator with comprehensive caching and error handling
/// </summary>
public class ExpressionEvaluator : IExpressionEvaluator
{
    private readonly ConcurrentDictionary<string, object> _assignmentCache;
    private readonly ConcurrentDictionary<string, object> _expressionCache;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<ExpressionEvaluator> _logger;

    public ExpressionEvaluator(ILogger<ExpressionEvaluator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _expressionCache = new ConcurrentDictionary<string, object>();
        _assignmentCache = new ConcurrentDictionary<string, object>();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public string SerializeCondition(object condition)
    {
        if (condition == null)
            return string.Empty;

        try
        {
            _logger.LogDebug("Serializing condition of type {ConditionType}", condition.GetType().Name);
            return JsonSerializer.Serialize(condition, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serialize condition of type {ConditionType}", condition.GetType().Name);
            throw new InvalidOperationException($"Failed to serialize condition: {ex.Message}", ex);
        }
    }

    public object? DeserializeCondition(string conditionJson)
    {
        if (string.IsNullOrEmpty(conditionJson))
            return null;

        try
        {
            _logger.LogDebug("Deserializing condition from JSON");
            return JsonSerializer.Deserialize<object>(conditionJson, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize condition from JSON");
            throw new InvalidOperationException($"Failed to deserialize condition: {ex.Message}", ex);
        }
    }

    public Func<TContext, TValue> CompileExpression<TContext, TValue>(Expression<Func<TContext, TValue>> expression)
    {
        if (expression == null)
            throw new ArgumentNullException(nameof(expression));

        var cacheKey = GenerateCacheKey<TContext, TValue>(expression);

        return (Func<TContext, TValue>)_expressionCache.GetOrAdd(cacheKey, _ =>
        {
            try
            {
                _logger.LogDebug("Compiling expression: {Expression} for context {Context}",
                    expression.ToString(), typeof(TContext).Name);

                // Validate the expression before compiling
                if (!ValidateExpressionInternal(expression))
                    throw new InvalidOperationException($"Invalid expression: {expression}");

                var compiled = expression.Compile();

                _logger.LogDebug("Successfully compiled expression for context {Context}", typeof(TContext).Name);
                return compiled;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to compile expression: {Expression}", expression.ToString());
                throw new ExpressionCompilationException($"Failed to compile expression: {expression}", ex);
            }
        });
    }

    public TValue EvaluateExpression<TContext, TValue>(Func<TContext, TValue> compiledExpression, TContext context)
    {
        if (compiledExpression == null)
            throw new ArgumentNullException(nameof(compiledExpression));

        if (context == null)
            throw new ArgumentNullException(nameof(context));

        try
        {
            _logger.LogTrace("Evaluating expression for context {Context}", typeof(TContext).Name);

            var result = compiledExpression(context);

            _logger.LogTrace("Expression evaluation successful, result type: {ResultType}",
                result?.GetType()?.Name ?? "null");

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate expression for context {Context}", typeof(TContext).Name);
            throw new ExpressionEvaluationException(
                $"Failed to evaluate expression for context {typeof(TContext).Name}", ex);
        }
    }

    public Action<TContext, TValue> CompileAssignmentExpression<TContext, TValue>(
        Expression<Func<TContext, TValue>> expression)
    {
        if (expression == null)
            throw new ArgumentNullException(nameof(expression));

        var cacheKey = GenerateAssignmentCacheKey<TContext, TValue>(expression);

        return (Action<TContext, TValue>)_assignmentCache.GetOrAdd(cacheKey, _ =>
        {
            try
            {
                _logger.LogDebug("Compiling assignment expression: {Expression} for context {Context}",
                    expression.ToString(), typeof(TContext).Name);

                // Convert the getter expression to a setter expression
                var setterExpression = CreateSetterExpression<TContext, TValue>(expression);
                var compiled = setterExpression.Compile();

                _logger.LogDebug("Successfully compiled assignment expression for context {Context}",
                    typeof(TContext).Name);
                return compiled;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to compile assignment expression: {Expression}", expression.ToString());
                throw new ExpressionCompilationException($"Failed to compile assignment expression: {expression}", ex);
            }
        });
    }

    public void ExecuteAssignment<TContext, TValue>(Action<TContext, TValue> compiledAssignment, TContext context,
        TValue value)
    {
        if (compiledAssignment == null)
            throw new ArgumentNullException(nameof(compiledAssignment));

        if (context == null)
            throw new ArgumentNullException(nameof(context));

        try
        {
            _logger.LogTrace("Executing assignment for context {Context}, value type: {ValueType}",
                typeof(TContext).Name, typeof(TValue).Name);

            compiledAssignment(context, value);

            _logger.LogTrace("Assignment execution successful");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute assignment for context {Context}", typeof(TContext).Name);
            throw new ExpressionEvaluationException($"Failed to execute assignment for context {typeof(TContext).Name}",
                ex);
        }
    }

    public bool ValidateExpression<TContext, TValue>(Expression<Func<TContext, TValue>> expression)
    {
        if (expression == null)
            return false;

        return ValidateExpressionInternal(expression);
    }

    public object? EvaluateLambdaExpression(LambdaExpression lambdaExpression, object context)
    {
        if (lambdaExpression == null)
            throw new ArgumentNullException(nameof(lambdaExpression));

        if (context == null)
            throw new ArgumentNullException(nameof(context));

        try
        {
            _logger.LogTrace("Evaluating lambda expression directly for context type: {ContextType}",
                context.GetType().Name);

            // Compile the lambda expression dynamically
            var compiled = lambdaExpression.Compile();

            // Invoke the compiled expression with the context
            var result = compiled.DynamicInvoke(context);

            _logger.LogTrace("Lambda expression evaluation successful, result type: {ResultType}",
                result?.GetType()?.Name ?? "null");

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate lambda expression for context type: {ContextType}",
                context.GetType().Name);
            throw new ExpressionEvaluationException(
                $"Failed to evaluate lambda expression for context type {context.GetType().Name}", ex);
        }
    }

    public void ClearCache()
    {
        _logger.LogDebug("Clearing expression caches");
        _expressionCache.Clear();
        _assignmentCache.Clear();
    }

    #region Private Methods

    private bool ValidateExpressionInternal<TContext, TValue>(Expression<Func<TContext, TValue>> expression)
    {
        try
        {
            // Basic validation - ensure the expression can be traversed
            var visitor = new ExpressionValidationVisitor();
            visitor.Visit(expression);
            return visitor.IsValid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Expression validation failed: {Expression}", expression.ToString());
            return false;
        }
    }

    private Expression<Action<TContext, TValue>> CreateSetterExpression<TContext, TValue>(
        Expression<Func<TContext, TValue>> getterExpression)
    {
        // Extract the member access from the getter expression
        if (getterExpression.Body is not MemberExpression memberExpression)
            throw new InvalidOperationException($"Expression must be a member access expression: {getterExpression}");

        // Ensure it's a property
        if (memberExpression.Member is not PropertyInfo propertyInfo)
            throw new InvalidOperationException($"Expression must access a property: {getterExpression}");

        // Ensure the property has a setter
        if (propertyInfo.SetMethod == null)
            throw new InvalidOperationException($"Property {propertyInfo.Name} does not have a setter");

        // Create parameters for the setter expression
        var contextParameter = Expression.Parameter(typeof(TContext), "context");
        var valueParameter = Expression.Parameter(typeof(TValue), "value");

        // Create the property access expression
        var propertyAccess = Expression.Property(
            ReplaceParameterVisitor.Replace(memberExpression.Expression, getterExpression.Parameters[0],
                contextParameter),
            propertyInfo
        );

        // Create the assignment expression
        var assignment = Expression.Assign(propertyAccess, valueParameter);

        // Create the lambda expression
        return Expression.Lambda<Action<TContext, TValue>>(assignment, contextParameter, valueParameter);
    }

    private string GenerateCacheKey<TContext, TValue>(Expression<Func<TContext, TValue>> expression)
    {
        return $"Getter_{typeof(TContext).FullName}_{typeof(TValue).FullName}_{expression.ToString().GetHashCode()}";
    }

    private string GenerateAssignmentCacheKey<TContext, TValue>(Expression<Func<TContext, TValue>> expression)
    {
        return $"Setter_{typeof(TContext).FullName}_{typeof(TValue).FullName}_{expression.ToString().GetHashCode()}";
    }

    #endregion
}

/// <summary>
///     Expression visitor for validating expressions
/// </summary>
internal class ExpressionValidationVisitor : ExpressionVisitor
{
    public bool IsValid { get; private set; } = true;

    protected override Expression VisitMember(MemberExpression node)
    {
        // Validate that we can access the member
        try
        {
            var member = node.Member;
            if (member is PropertyInfo property)
            {
                // Ensure property is readable
                if (property.GetMethod == null)
                {
                    IsValid = false;
                    return node;
                }
            }
            else if (member is FieldInfo field)
            {
                // Fields are generally accessible if they're public
                if (!field.IsPublic)
                {
                    IsValid = false;
                    return node;
                }
            }
        }
        catch
        {
            IsValid = false;
            return node;
        }

        return base.VisitMember(node);
    }
}

/// <summary>
///     Expression visitor for replacing parameters in expressions
/// </summary>
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

/// <summary>
///     Exception thrown when expression compilation fails
/// </summary>
public class ExpressionCompilationException : Exception
{
    public ExpressionCompilationException(string message) : base(message)
    {
    }

    public ExpressionCompilationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
///     Exception thrown when expression evaluation fails
/// </summary>
public class ExpressionEvaluationException : Exception
{
    public ExpressionEvaluationException(string message) : base(message)
    {
    }

    public ExpressionEvaluationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
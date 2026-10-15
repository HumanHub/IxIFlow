using System.Linq.Expressions;

namespace IxIFlow.Core;

/// <summary>
///     Compiles and evaluates lambda expressions for property mapping in workflows.
///     Supports all context types: WorkflowData, PreviousStep, ResumeEvent, Exception contexts.
///     Provides caching for performance optimization.
/// </summary>
public interface IExpressionEvaluator
{
    /// <summary>
    ///     Serializes a condition object to JSON for persistence
    /// </summary>
    /// <param name="condition">The condition object to serialize</param>
    /// <returns>JSON string representation of the condition</returns>
    string SerializeCondition(object condition);

    /// <summary>
    ///     Deserializes a condition from JSON
    /// </summary>
    /// <param name="conditionJson">The JSON string representation of the condition</param>
    /// <returns>The deserialized condition object</returns>
    object? DeserializeCondition(string conditionJson);

    /// <summary>
    ///     Compiles an expression for property mapping
    /// </summary>
    /// <typeparam name="TContext">The context type (WorkflowContext, CatchContext, etc.)</typeparam>
    /// <typeparam name="TValue">The property value type</typeparam>
    /// <param name="expression">The lambda expression to compile</param>
    /// <returns>Compiled expression delegate</returns>
    Func<TContext, TValue> CompileExpression<TContext, TValue>(Expression<Func<TContext, TValue>> expression);

    /// <summary>
    ///     Evaluates a compiled expression with the given context
    /// </summary>
    /// <typeparam name="TContext">The context type</typeparam>
    /// <typeparam name="TValue">The return value type</typeparam>
    /// <param name="compiledExpression">The compiled expression</param>
    /// <param name="context">The context instance</param>
    /// <returns>The evaluated value</returns>
    TValue EvaluateExpression<TContext, TValue>(Func<TContext, TValue> compiledExpression, TContext context);

    /// <summary>
    ///     Evaluates a lambda expression directly without requiring type casting
    /// </summary>
    /// <param name="lambdaExpression">The lambda expression to evaluate</param>
    /// <param name="context">The context instance</param>
    /// <returns>The evaluated value</returns>
    object? EvaluateLambdaExpression(LambdaExpression lambdaExpression, object context);

    /// <summary>
    ///     Compiles an assignment expression for property output mapping
    /// </summary>
    /// <typeparam name="TContext">The context type</typeparam>
    /// <typeparam name="TValue">The property value type</typeparam>
    /// <param name="expression">The lambda expression representing the assignment target</param>
    /// <returns>Compiled assignment action</returns>
    Action<TContext, TValue> CompileAssignmentExpression<TContext, TValue>(
        Expression<Func<TContext, TValue>> expression);

    /// <summary>
    ///     Executes a compiled assignment expression
    /// </summary>
    /// <typeparam name="TContext">The context type</typeparam>
    /// <typeparam name="TValue">The value type</typeparam>
    /// <param name="compiledAssignment">The compiled assignment action</param>
    /// <param name="context">The context instance</param>
    /// <param name="value">The value to assign</param>
    void ExecuteAssignment<TContext, TValue>(Action<TContext, TValue> compiledAssignment, TContext context,
        TValue value);

    /// <summary>
    ///     Validates that an expression is valid for the given context type
    /// </summary>
    /// <typeparam name="TContext">The context type</typeparam>
    /// <typeparam name="TValue">The property value type</typeparam>
    /// <param name="expression">The expression to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    bool ValidateExpression<TContext, TValue>(Expression<Func<TContext, TValue>> expression);

    /// <summary>
    ///     Clears the expression cache (useful for testing or memory management)
    /// </summary>
    void ClearCache();
}
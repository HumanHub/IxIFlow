using System.Linq.Expressions;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
/// Implementation for configuring activities within saga error handling blocks with access to previous step data
/// </summary>
public class SagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> : ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    private readonly WorkflowStep _step;

    public SagaErrorActivitySetupBuilder(WorkflowStep step)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
    }

    /// <summary>
    /// Configures an input property for the error handling activity with access to previous step data
    /// </summary>
    public ISagaErrorInputSetupBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> Input<TProperty>(
        Expression<Func<TActivity, TProperty>> property)
    {
        return new SagaErrorInputSetupBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData>(_step, property);
    }

    /// <summary>
    /// Configures an output property for the error handling activity
    /// </summary>
    public ISagaErrorOutputSetupBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> Output<TProperty>(
        Expression<Func<TActivity, TProperty>> property)
    {
        return new SagaErrorOutputSetupBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData>(_step, property);
    }

    /// <summary>
    /// Configures error handling for a specific exception type in the error handling activity
    /// </summary>
    public ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError<TException>(
        Action<ISagaStepErrorHandler<TException>> handler) where TException : Exception
    {
        // Implementation for error handling
        return this;
    }

    /// <summary>
    /// Configures general error handling for the error handling activity
    /// </summary>
    public ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError(Action<ISagaStepErrorHandler> handler)
    {
        // Implementation for error handling
        return this;
    }
}

/// <summary>
/// Builder for configuring input properties of error handling activities with access to previous step data
/// </summary>
public class SagaErrorInputSetupBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> : ISagaErrorInputSetupBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData>
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    private readonly WorkflowStep _step;
    private readonly Expression<Func<TActivity, TProperty>> _propertyExpression;

    public SagaErrorInputSetupBuilder(WorkflowStep step, Expression<Func<TActivity, TProperty>> propertyExpression)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _propertyExpression = propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));
    }

    /// <summary>
    /// Specifies the source of the input value from exception context with access to previous step data
    /// </summary>
    public ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> From(
        Expression<Func<CatchContext<TWorkflowData, Exception, TPreviousStepData>, TProperty>> source)
    {
        // Extract property name from the property expression
        var propertyName = GetPropertyName(_propertyExpression);

        // Compile the expression to a fast Func delegate
        var compiledFunction = source.Compile();

        // Create wrapped function for object context
        Func<object, object?> wrappedFunction = context =>
        {
            if (context is CatchContext<TWorkflowData, Exception, TPreviousStepData> typedContext)
                return compiledFunction(typedContext);
            throw new InvalidOperationException(
                $"Expected CatchContext<{typeof(TWorkflowData).Name}, Exception, {typeof(TPreviousStepData).Name}>, got {context.GetType().Name}");
        };

        // Create property mapping with compiled function
        var mapping = new PropertyMapping
        {
            TargetProperty = propertyName,
            SourceFunction = wrappedFunction,
            SourceType = typeof(TProperty),
            TargetType = typeof(TProperty),
            Direction = PropertyMappingDirection.Input
        };

        _step.InputMappings.Add(mapping);

        return new SagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>(_step);
    }

    /// <summary>
    /// Configures an input property for the error handling activity with access to previous step data
    /// </summary>
    public ISagaErrorInputSetupBuilder<TWorkflowData, TActivity, TProperty2, TPreviousStepData> Input<TProperty2>(
        Expression<Func<TActivity, TProperty2>> property)
    {
        return new SagaErrorInputSetupBuilder<TWorkflowData, TActivity, TProperty2, TPreviousStepData>(_step, property);
    }

    /// <summary>
    /// Configures an output property for the error handling activity
    /// </summary>
    public ISagaErrorOutputSetupBuilder<TWorkflowData, TActivity, TProperty2, TPreviousStepData> Output<TProperty2>(
        Expression<Func<TActivity, TProperty2>> property)
    {
        return new SagaErrorOutputSetupBuilder<TWorkflowData, TActivity, TProperty2, TPreviousStepData>(_step, property);
    }

    /// <summary>
    /// Configures error handling for a specific exception type in the error handling activity
    /// </summary>
    public ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError<TException>(
        Action<ISagaStepErrorHandler<TException>> handler) where TException : Exception
    {
        return new SagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>(_step);
    }

    /// <summary>
    /// Configures general error handling for the error handling activity
    /// </summary>
    public ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError(Action<ISagaStepErrorHandler> handler)
    {
        return new SagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>(_step);
    }

    private string GetPropertyName<T>(Expression<Func<TActivity, T>> propertyExpression)
    {
        if (propertyExpression.Body is MemberExpression memberExpression) 
            return memberExpression.Member.Name;
        throw new ArgumentException("Property expression must be a member access expression", nameof(propertyExpression));
    }
}

/// <summary>
/// Builder for configuring output properties of error handling activities with access to previous step data
/// </summary>
public class SagaErrorOutputSetupBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> : ISagaErrorOutputSetupBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData>
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    private readonly WorkflowStep _step;
    private readonly Expression<Func<TActivity, TProperty>> _propertyExpression;

    public SagaErrorOutputSetupBuilder(WorkflowStep step, Expression<Func<TActivity, TProperty>> propertyExpression)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _propertyExpression = propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));
    }

    /// <summary>
    /// Specifies where to store the output value from the error handling activity
    /// </summary>
    public ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination)
    {
        // Extract property name from the property expression
        var propertyName = GetPropertyName(_propertyExpression);

        // Compile the destination expression to an assignment function
        var compiledAssignment = CompileOutputAssignment(destination);

        // Create wrapped assignment function for object context
        Action<object, object?> assignmentFunction = (context, value) =>
        {
            if (context is WorkflowContext<TWorkflowData> typedContext && value is TProperty typedValue)
                compiledAssignment(typedContext, typedValue);
            else if (context is WorkflowContext<TWorkflowData> typedContext2 && value == null)
                compiledAssignment(typedContext2, default!);
            else
                throw new InvalidOperationException(
                    $"Expected WorkflowContext<{typeof(TWorkflowData).Name}> and {typeof(TProperty).Name}, got {context.GetType().Name} and {value?.GetType().Name}");
        };

        // Create dummy source function for outputs (not used)
        Func<object, object?> dummySourceFunction = _ => null;

        // Create property mapping with compiled assignment function
        var mapping = new PropertyMapping
        {
            TargetProperty = propertyName,
            SourceFunction = dummySourceFunction,
            TargetAssignmentFunction = assignmentFunction,
            SourceType = typeof(TProperty),
            TargetType = typeof(TProperty),
            Direction = PropertyMappingDirection.Output
        };

        _step.OutputMappings.Add(mapping);

        return new SagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>(_step);
    }

    /// <summary>
    /// Configures an input property for the error handling activity with access to previous step data
    /// </summary>
    public ISagaErrorInputSetupBuilder<TWorkflowData, TActivity, TProperty2, TPreviousStepData> Input<TProperty2>(
        Expression<Func<TActivity, TProperty2>> property)
    {
        return new SagaErrorInputSetupBuilder<TWorkflowData, TActivity, TProperty2, TPreviousStepData>(_step, property);
    }

    /// <summary>
    /// Configures an output property for the error handling activity
    /// </summary>
    public ISagaErrorOutputSetupBuilder<TWorkflowData, TActivity, TProperty2, TPreviousStepData> Output<TProperty2>(
        Expression<Func<TActivity, TProperty2>> property)
    {
        return new SagaErrorOutputSetupBuilder<TWorkflowData, TActivity, TProperty2, TPreviousStepData>(_step, property);
    }

    /// <summary>
    /// Configures error handling for a specific exception type in the error handling activity
    /// </summary>
    public ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError<TException>(
        Action<ISagaStepErrorHandler<TException>> handler) where TException : Exception
    {
        return new SagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>(_step);
    }

    /// <summary>
    /// Configures general error handling for the error handling activity
    /// </summary>
    public ISagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError(Action<ISagaStepErrorHandler> handler)
    {
        return new SagaErrorActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>(_step);
    }

    /// <summary>
    /// Compiles the output assignment expression using reflection and expression compilation
    /// </summary>
    private Action<WorkflowContext<TWorkflowData>, TProperty> CompileOutputAssignment(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination)
    {
        // Use the same logic as ActivitySetupBuilder
        if (destination.Body is not MemberExpression memberExpression)
            throw new InvalidOperationException($"Output destination must be a member access expression: {destination}");

        if (memberExpression.Member is not System.Reflection.PropertyInfo propertyInfo)
            throw new InvalidOperationException($"Output destination must access a property: {destination}");

        if (propertyInfo.SetMethod == null)
            throw new InvalidOperationException($"Property {propertyInfo.Name} does not have a setter");

        var contextParameter = Expression.Parameter(typeof(WorkflowContext<TWorkflowData>), "context");
        var valueParameter = Expression.Parameter(typeof(TProperty), "value");

        var propertyAccess = Expression.Property(
            ReplaceParameter(memberExpression.Expression, destination.Parameters[0], contextParameter),
            propertyInfo
        );

        var assignment = Expression.Assign(propertyAccess, valueParameter);
        var lambda = Expression.Lambda<Action<WorkflowContext<TWorkflowData>, TProperty>>(assignment, contextParameter, valueParameter);
        return lambda.Compile();
    }

    /// <summary>
    /// Replaces a parameter in an expression with a new parameter
    /// </summary>
    private Expression ReplaceParameter(Expression? expression, ParameterExpression oldParameter, ParameterExpression newParameter)
    {
        if (expression == null) return newParameter;
        if (expression == oldParameter) return newParameter;
        if (expression is MemberExpression memberExpr)
            return Expression.MakeMemberAccess(
                ReplaceParameter(memberExpr.Expression, oldParameter, newParameter),
                memberExpr.Member
            );
        return expression;
    }

    private string GetPropertyName<T>(Expression<Func<TActivity, T>> propertyExpression)
    {
        if (propertyExpression.Body is MemberExpression memberExpression) 
            return memberExpression.Member.Name;
        throw new ArgumentException("Property expression must be a member access expression", nameof(propertyExpression));
    }
}

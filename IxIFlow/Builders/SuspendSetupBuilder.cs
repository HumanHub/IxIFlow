using System.Linq.Expressions;
using System.Reflection;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Builder for configuring suspend/resume operations
/// </summary>
public class SuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData> 
    : ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>
    where TResumeEvent : class
    where TPreviousStepData : class
{
    private readonly WorkflowStep _step;

    public SuspendSetupBuilder(WorkflowStep step)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
    }

    /// <summary>
    ///     Configures an input mapping for the resume event
    /// </summary>
    public ISuspendInputBuilder<TWorkflowData, TResumeEvent, TProperty, TPreviousStepData> Input<TProperty>(
        Expression<Func<TResumeEvent, TProperty>> property)
    {
        return new SuspendInputBuilder<TWorkflowData, TResumeEvent, TProperty, TPreviousStepData>(_step, property);
    }

    /// <summary>
    ///     Configures an output mapping for the resume event
    /// </summary>
    public ISuspendOutputBuilder<TWorkflowData, TResumeEvent, TProperty, TPreviousStepData> Output<TProperty>(
        Expression<Func<TResumeEvent, TProperty>> property)
    {
        return new SuspendOutputBuilder<TWorkflowData, TResumeEvent, TProperty, TPreviousStepData>(_step, property);
    }
}

/// <summary>
///     Builder for configuring input mappings for suspend/resume operations
/// </summary>
public class SuspendInputBuilder<TWorkflowData, TResumeEvent, TProperty, TPreviousStepData> 
    : ISuspendInputBuilder<TWorkflowData, TResumeEvent, TProperty, TPreviousStepData>
    where TResumeEvent : class
    where TPreviousStepData : class
{
    private readonly Expression<Func<TResumeEvent, TProperty>> _propertyExpression;
    private readonly WorkflowStep _step;

    public SuspendInputBuilder(WorkflowStep step, Expression<Func<TResumeEvent, TProperty>> propertyExpression)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _propertyExpression = propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));
    }

    /// <summary>
    ///     Specifies the source expression for this input mapping
    /// </summary>
    public ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData> From(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TProperty>> source)
    {
        // Extract property name from the property expression
        var propertyName = GetPropertyName(_propertyExpression);

        // Compile the expression to a fast Func delegate
        var compiledFunction = source.Compile();

        // Create wrapped function for object context
        Func<object, object?> wrappedFunction = context =>
        {
            if (context is WorkflowContext<TWorkflowData, TPreviousStepData> typedContext)
                return compiledFunction(typedContext);
            throw new InvalidOperationException(
                $"Expected WorkflowContext<{typeof(TWorkflowData).Name}, {typeof(TPreviousStepData).Name}>, got {context.GetType().Name}");
        };

        // Create property mapping with compiled function
        var mapping = new PropertyMapping
        {
            TargetProperty = propertyName,
            SourceFunction = wrappedFunction,
            SourceType = typeof(WorkflowContext<TWorkflowData, TPreviousStepData>),
            TargetType = typeof(TResumeEvent),
            Direction = PropertyMappingDirection.Input
        };

        _step.InputMappings.Add(mapping);

        return new SuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>(_step);
    }

    /// <summary>
    ///     Configures an input property mapping
    /// </summary>
    public ISuspendInputBuilder<TWorkflowData, TResumeEvent, TNextProperty, TPreviousStepData> Input<TNextProperty>(
        Expression<Func<TResumeEvent, TNextProperty>> property)
    {
        return new SuspendInputBuilder<TWorkflowData, TResumeEvent, TNextProperty, TPreviousStepData>(_step, property);
    }

    /// <summary>
    ///     Configures an output property mapping
    /// </summary>
    public ISuspendOutputBuilder<TWorkflowData, TResumeEvent, TNextProperty, TPreviousStepData> Output<TNextProperty>(
        Expression<Func<TResumeEvent, TNextProperty>> property)
    {
        return new SuspendOutputBuilder<TWorkflowData, TResumeEvent, TNextProperty, TPreviousStepData>(_step, property);
    }

    private string GetPropertyName<T>(Expression<Func<TResumeEvent, T>> propertyExpression)
    {
        if (propertyExpression.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        throw new ArgumentException("Property expression must be a member access expression",
            nameof(propertyExpression));
    }
}

/// <summary>
///     Builder for configuring output mappings for suspend/resume operations
/// </summary>
public class SuspendOutputBuilder<TWorkflowData, TResumeEvent, TProperty, TPreviousStepData> 
    : ISuspendOutputBuilder<TWorkflowData, TResumeEvent, TProperty, TPreviousStepData>
    where TResumeEvent : class
    where TPreviousStepData : class
{
    private readonly Expression<Func<TResumeEvent, TProperty>> _propertyExpression;
    private readonly WorkflowStep _step;

    public SuspendOutputBuilder(WorkflowStep step, Expression<Func<TResumeEvent, TProperty>> propertyExpression)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _propertyExpression = propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));
    }

    /// <summary>
    ///     Specifies the destination expression for this output mapping
    /// </summary>
    public ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData> To(
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
                // Handle null values gracefully
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
            SourceFunction = dummySourceFunction, // Not used for outputs
            TargetAssignmentFunction = assignmentFunction,
            SourceType = typeof(TResumeEvent),
            TargetType = typeof(WorkflowContext<TWorkflowData>),
            Direction = PropertyMappingDirection.Output
        };

        _step.OutputMappings.Add(mapping);

        return new SuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>(_step);
    }

    /// <summary>
    ///     Configures an input property mapping
    /// </summary>
    public ISuspendInputBuilder<TWorkflowData, TResumeEvent, TNextProperty, TPreviousStepData> Input<TNextProperty>(
        Expression<Func<TResumeEvent, TNextProperty>> property)
    {
        return new SuspendInputBuilder<TWorkflowData, TResumeEvent, TNextProperty, TPreviousStepData>(_step, property);
    }

    /// <summary>
    ///     Configures an output property mapping
    /// </summary>
    public ISuspendOutputBuilder<TWorkflowData, TResumeEvent, TNextProperty, TPreviousStepData> Output<TNextProperty>(
        Expression<Func<TResumeEvent, TNextProperty>> property)
    {
        return new SuspendOutputBuilder<TWorkflowData, TResumeEvent, TNextProperty, TPreviousStepData>(_step, property);
    }

    /// <summary>
    ///     Compiles the output assignment expression using reflection and expression compilation
    /// </summary>
    private Action<WorkflowContext<TWorkflowData>, TProperty> CompileOutputAssignment(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination)
    {
        // Use the same logic as ExpressionEvaluator.CreateSetterExpression but inline
        if (destination.Body is not MemberExpression memberExpression)
            throw new InvalidOperationException(
                $"Output destination must be a member access expression: {destination}");

        // Ensure it's a property
        if (memberExpression.Member is not PropertyInfo propertyInfo)
            throw new InvalidOperationException($"Output destination must access a property: {destination}");

        // Ensure the property has a setter
        if (propertyInfo.SetMethod == null)
            throw new InvalidOperationException($"Property {propertyInfo.Name} does not have a setter");

        // Create parameters for the setter expression
        var contextParameter = Expression.Parameter(typeof(WorkflowContext<TWorkflowData>), "context");
        var valueParameter = Expression.Parameter(typeof(TProperty), "value");

        // Create the property access expression by replacing the parameter
        var propertyAccess = Expression.Property(
            ReplaceParameter(memberExpression.Expression, destination.Parameters[0], contextParameter),
            propertyInfo
        );

        // Create the assignment expression
        var assignment = Expression.Assign(propertyAccess, valueParameter);

        // Create and compile the lambda expression
        var lambda =
            Expression.Lambda<Action<WorkflowContext<TWorkflowData>, TProperty>>(assignment, contextParameter,
                valueParameter);
        return lambda.Compile();
    }

    /// <summary>
    ///     Replaces a parameter in an expression with a new parameter
    /// </summary>
    private Expression ReplaceParameter(Expression? expression, ParameterExpression oldParameter,
        ParameterExpression newParameter)
    {
        if (expression == null) return newParameter;

        if (expression == oldParameter)
            return newParameter;

        if (expression is MemberExpression memberExpr)
            return Expression.MakeMemberAccess(
                ReplaceParameter(memberExpr.Expression, oldParameter, newParameter),
                memberExpr.Member
            );

        return expression;
    }

    private string GetPropertyName<T>(Expression<Func<TResumeEvent, T>> propertyExpression)
    {
        if (propertyExpression.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        throw new ArgumentException("Property expression must be a member access expression",
            nameof(propertyExpression));
    }
}
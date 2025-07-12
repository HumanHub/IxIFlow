using System.Linq.Expressions;
using System.Reflection;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Workflow invocation builder for first step (no previous step access)
/// </summary>
public class WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData>(WorkflowStep step)
    : IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData>
    where TWorkflowData : class
    where TInvokedData : class
{
    private readonly WorkflowStep _step = step ?? throw new ArgumentNullException(nameof(step));

    /// <summary>
    ///     Configures an input property mapping for the invoked workflow
    /// </summary>
    public IWorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty> Input<TProperty>(
        Expression<Func<TInvokedData, TProperty>> property)
    {
        return new WorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty>(_step, property);
    }

    /// <summary>
    ///     Configures an output property mapping from the invoked workflow
    /// </summary>
    public IWorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty> Output<TProperty>(
        Expression<Func<TInvokedData, TProperty>> property)
    {
        return new WorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty>(_step, property);
    }
}

/// <summary>
///     Workflow invocation builder for subsequent steps (with previous step access)
/// </summary>
public class WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>(WorkflowStep step)
    : IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>
    where TWorkflowData : class
    where TInvokedData : class
    where TPreviousStepData : class
{
    private readonly WorkflowStep _step = step ?? throw new ArgumentNullException(nameof(step));

    /// <summary>
    ///     Configures an input property mapping with previous step access
    /// </summary>
    public IWorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty, TPreviousStepData>
        Input<TProperty>(
            Expression<Func<TInvokedData, TProperty>> property)
    {
        return new WorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty, TPreviousStepData>(
            _step, property);
    }

    /// <summary>
    ///     Configures an output property mapping from the invoked workflow
    /// </summary>
    public IWorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty> Output<TProperty>(
        Expression<Func<TInvokedData, TProperty>> property)
    {
        return new WorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty>(_step, property);
    }

    // Explicit interface implementations for base interface
    IWorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty>
        IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData>.Input<TProperty>(
            Expression<Func<TInvokedData, TProperty>> property)
    {
        return new WorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty>(_step, property);
    }
}

/// <summary>
///     Input builder for first step workflow invocation (no previous step)
/// </summary>
public class WorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty>(
    WorkflowStep step,
    Expression<Func<TInvokedData, TProperty>> propertyExpression)
    : IWorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty>
    where TWorkflowData : class
    where TInvokedData : class
{
    private readonly Expression<Func<TInvokedData, TProperty>> _propertyExpression =
        propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));

    private readonly WorkflowStep _step = step ?? throw new ArgumentNullException(nameof(step));

    /// <summary>
    ///     Specifies the source expression for this input mapping
    /// </summary>
    public IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData> From(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> source)
    {
        // Extract property name from the property expression
        var propertyName = GetPropertyName(_propertyExpression);

        // Compile the expression to a fast Func delegate
        var compiledFunction = source.Compile();

        // Create wrapped function for object context
        Func<object, object?> wrappedFunction = context =>
        {
            if (context is WorkflowContext<TWorkflowData> typedContext) return compiledFunction(typedContext);
            throw new InvalidOperationException(
                $"Expected WorkflowContext<{typeof(TWorkflowData).Name}>, got {context.GetType().Name}");
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

        return new WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData>(_step);
    }

    // Explicit interface implementation for base interface
    IWorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty2>
        IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData>.Input<TProperty2>(
            Expression<Func<TInvokedData, TProperty2>> property)
    {
        return new WorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty2>(_step, property);
    }

    IWorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty2>
        IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData>.Output<TProperty2>(
            Expression<Func<TInvokedData, TProperty2>> property)
    {
        return new WorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty2>(_step, property);
    }

    private string GetPropertyName<T>(Expression<Func<TInvokedData, T>> propertyExpression)
    {
        if (propertyExpression.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        throw new ArgumentException(
            "Property expression must be a member access expression",
            nameof(propertyExpression));
    }
}

/// <summary>
///     Input builder for subsequent step workflow invocation (with previous step access)
/// </summary>
public class WorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty, TPreviousStepData>(
    WorkflowStep step,
    Expression<Func<TInvokedData, TProperty>> propertyExpression)
    : IWorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty, TPreviousStepData>
    where TWorkflowData : class
    where TInvokedData : class
    where TPreviousStepData : class
{
    private readonly Expression<Func<TInvokedData, TProperty>> _propertyExpression =
        propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));

    private readonly WorkflowStep _step = step ?? throw new ArgumentNullException(nameof(step));

    /// <summary>
    ///     Specifies the source expression for this input mapping with previous step access
    /// </summary>
    public IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData> From(
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
            SourceType = typeof(TProperty),
            TargetType = typeof(TProperty),
            Direction = PropertyMappingDirection.Input
        };

        _step.InputMappings.Add(mapping);

        return new WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>(_step);
    }

    /// <summary>
    ///     Configures another input property mapping with previous step access
    /// </summary>
    public IWorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty2, TPreviousStepData>
        Input<TProperty2>(
            Expression<Func<TInvokedData, TProperty2>> property)
    {
        return new WorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty2, TPreviousStepData>(
            _step, property);
    }

    /// <summary>
    ///     Configures an output property mapping
    /// </summary>
    public IWorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty2> Output<TProperty2>(
        Expression<Func<TInvokedData, TProperty2>> property)
    {
        return new WorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty2>(_step, property);
    }

    // Explicit interface implementations for base interface
    IWorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty2>
        IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData>.Input<TProperty2>(
            Expression<Func<TInvokedData, TProperty2>> property)
    {
        return new WorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty2>(_step, property);
    }

    IWorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty2>
        IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData>.Output<TProperty2>(
            Expression<Func<TInvokedData, TProperty2>> property)
    {
        return new WorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty2>(_step, property);
    }

    private string GetPropertyName<T>(Expression<Func<TInvokedData, T>> propertyExpression)
    {
        if (propertyExpression.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        throw new ArgumentException(
            "Property expression must be a member access expression",
            nameof(propertyExpression));
    }
}

/// <summary>
///     Output builder for workflow invocation property mappings
/// </summary>
public class WorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty>(
    WorkflowStep step,
    Expression<Func<TInvokedData, TProperty>> propertyExpression)
    : IWorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty>
    where TWorkflowData : class
    where TInvokedData : class
{
    private readonly Expression<Func<TInvokedData, TProperty>> _propertyExpression =
        propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));

    private readonly WorkflowStep _step = step ?? throw new ArgumentNullException(nameof(step));

    /// <summary>
    ///     Specifies the destination expression for this output mapping
    /// </summary>
    public IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData> To(
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
            SourceType = typeof(TProperty),
            TargetType = typeof(TProperty),
            Direction = PropertyMappingDirection.Output
        };

        _step.OutputMappings.Add(mapping);

        return new WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData>(_step);
    }

    /// <summary>
    ///     Configures another input property mapping
    /// </summary>
    public IWorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty2> Input<TProperty2>(
        Expression<Func<TInvokedData, TProperty2>> property)
    {
        return new WorkflowInvocationSetupInputBuilder<TWorkflowData, TInvokedData, TProperty2>(_step, property);
    }

    /// <summary>
    ///     Configures another output property mapping
    /// </summary>
    public IWorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty2> Output<TProperty2>(
        Expression<Func<TInvokedData, TProperty2>> property)
    {
        return new WorkflowInvocationSetupOutputBuilder<TWorkflowData, TInvokedData, TProperty2>(_step, property);
    }

    /// <summary>
    ///     Compiles the output assignment expression using reflection and expression compilation
    /// </summary>
    private Action<WorkflowContext<TWorkflowData>, TProperty> CompileOutputAssignment(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination)
    {
        // Use the same logic as ActivitySetupBuilder
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
            propertyInfo);

        // Create the assignment expression
        var assignment = Expression.Assign(propertyAccess, valueParameter);

        // Create and compile the lambda expression
        var lambda =
            Expression.Lambda<Action<WorkflowContext<TWorkflowData>, TProperty>>(
                assignment, contextParameter,
                valueParameter);
        return lambda.Compile();
    }

    /// <summary>
    ///     Replaces a parameter in an expression with a new parameter
    /// </summary>
    private Expression ReplaceParameter(
        Expression? expression,
        ParameterExpression oldParameter,
        ParameterExpression newParameter)
    {
        if (expression == null) return newParameter;

        if (expression == oldParameter)
            return newParameter;

        if (expression is MemberExpression memberExpr)
            return Expression.MakeMemberAccess(
                ReplaceParameter(memberExpr.Expression, oldParameter, newParameter),
                memberExpr.Member);

        return expression;
    }

    private string GetPropertyName<T>(Expression<Func<TInvokedData, T>> propertyExpression)
    {
        if (propertyExpression.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        throw new ArgumentException(
            "Property expression must be a member access expression",
            nameof(propertyExpression));
    }
}
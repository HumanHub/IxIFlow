using System.Linq.Expressions;
using System.Reflection;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Compensation activity builder for saga pattern
/// </summary>
public class CompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData> : ICompensationActivityBuilder<
    TWorkflowData, TCompensationActivity, TCurrentStepData>
    where TWorkflowData : class
    where TCompensationActivity : IAsyncActivity
    where TCurrentStepData : class
{
    private readonly CompensationActivityInfo _compensationInfo;

    public CompensationActivityBuilder(CompensationActivityInfo compensationInfo)
    {
        _compensationInfo = compensationInfo ?? throw new ArgumentNullException(nameof(compensationInfo));
    }

    public ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty, TCurrentStepData>
        Input<TProperty>(Expression<Func<TCompensationActivity, TProperty>> property)
    {
        return new CompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty, TCurrentStepData>(
            this, _compensationInfo, property);
    }

    public ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty> Output<TProperty>(
        Expression<Func<TCompensationActivity, TProperty>> property)
    {
        return new CompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty>(
            this, _compensationInfo, property);
    }
}

/// <summary>
///     Compensation activity builder with access to previous chain activities
/// </summary>
public class CompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
    TPreviousChainActivity> : ICompensationActivityBuilder<TWorkflowData, TCompensationActivity,
    TPreviousStepData, TPreviousChainActivity>
    where TWorkflowData : class
    where TCompensationActivity : IAsyncActivity
    where TPreviousStepData : class
    where TPreviousChainActivity : class, IAsyncActivity
{
    private readonly CompensationActivityInfo _compensationInfo;

    public CompensationActivityBuilder(CompensationActivityInfo compensationInfo)
    {
        _compensationInfo = compensationInfo ?? throw new ArgumentNullException(nameof(compensationInfo));
    }

    public ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty,
        TPreviousStepData, TPreviousChainActivity> Input<TProperty>(
        Expression<Func<TCompensationActivity, TProperty>> property)
    {
        return new CompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty,
            TPreviousStepData, TPreviousChainActivity>(
            this, _compensationInfo, property);
    }

    public ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty> Output<TProperty>(
        Expression<Func<TCompensationActivity, TProperty>> property)
    {
        return new CompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty>(
            this, _compensationInfo, property);
    }
}

/// <summary>
///     Compensation activity input builder
/// </summary>
public class CompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty, TCurrentStepData> :
    ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty, TCurrentStepData>
    where TWorkflowData : class
    where TCompensationActivity : IAsyncActivity
    where TCurrentStepData : class
{
    private readonly CompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData> _builder;
    private readonly CompensationActivityInfo _compensationInfo;
    private readonly Expression<Func<TCompensationActivity, TProperty>> _propertyExpression;

    public CompensationActivityInputBuilder(
        ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData> builder,
        CompensationActivityInfo compensationInfo,
        Expression<Func<TCompensationActivity, TProperty>> propertyExpression)
    {
        _builder = (CompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData>)builder ??
                   throw new ArgumentNullException(nameof(builder));
        _compensationInfo = compensationInfo ?? throw new ArgumentNullException(nameof(compensationInfo));
        _propertyExpression = propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));
    }

    public ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TCurrentStepData> From(
        Expression<Func<CompensationContext<TWorkflowData, TCurrentStepData>, TProperty>> source)
    {
        var propertyName = GetPropertyName(_propertyExpression);
        var sourceFunction = CompileToFunction(source);
        var propertyType = typeof(TProperty);

        var mapping = new PropertyMapping
        {
            TargetProperty = propertyName,
            SourceFunction = sourceFunction,
            SourceType = typeof(CompensationContext<TWorkflowData, TCurrentStepData>),
            TargetType = propertyType,
            Direction = PropertyMappingDirection.Input
        };

        _compensationInfo.InputMappings.Add(mapping);
        return _builder;
    }

    // Implement ICompensationActivityBuilder interface
    public ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TNewProperty, TCurrentStepData>
        Input<TNewProperty>(Expression<Func<TCompensationActivity, TNewProperty>> property)
    {
        return _builder.Input(property);
    }

    public ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TNewProperty> Output<TNewProperty>(
        Expression<Func<TCompensationActivity, TNewProperty>> property)
    {
        return _builder.Output(property);
    }

    private string GetPropertyName<T, TProp>(Expression<Func<T, TProp>> expression)
    {
        if (expression.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        throw new ArgumentException("Expression must be a member access", nameof(expression));
    }

    private Func<object, object?> CompileToFunction<TContext, TValue>(Expression<Func<TContext, TValue>> expression)
    {
        // Compile expression directly (same pattern as SagaBuilder)
        var compiled = expression.Compile();
        return context =>
        {
            if (context is TContext typedContext) return compiled(typedContext);

            // Add detailed logging for debugging type mismatches
            var expectedType = typeof(TContext);
            var actualType = context.GetType();

            Console.WriteLine("\ud83d\udd0d COMPENSATION TYPE MISMATCH DEBUG:");
            Console.WriteLine($"  Expected Type: {expectedType.FullName}");
            Console.WriteLine($"  Actual Type: {actualType.FullName}");

            if (expectedType.IsGenericType && actualType.IsGenericType)
            {
                Console.WriteLine($"  Expected Generic Definition: {expectedType.GetGenericTypeDefinition()}");
                Console.WriteLine($"  Actual Generic Definition: {actualType.GetGenericTypeDefinition()}");
                Console.WriteLine(
                    $"  Expected Generic Args: [{string.Join(", ", expectedType.GetGenericArguments().Select(t => t.Name))}]");
                Console.WriteLine(
                    $"  Actual Generic Args: [{string.Join(", ", actualType.GetGenericArguments().Select(t => t.Name))}]");
            }

            throw new InvalidOperationException($"Expected {expectedType.FullName}, got {actualType.FullName}");
        };
    }
}

/// <summary>
///     Compensation activity input builder with access to previous chain activities
/// </summary>
public class CompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty,
    TPreviousStepData,
    TPreviousChainActivity> : ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity,
    TProperty, TPreviousStepData, TPreviousChainActivity>
    where TWorkflowData : class
    where TCompensationActivity : IAsyncActivity
    where TPreviousStepData : class
    where TPreviousChainActivity : class, IAsyncActivity
{
    private readonly ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
        TPreviousChainActivity> _builder;

    private readonly CompensationActivityInfo _compensationInfo;
    private readonly Expression<Func<TCompensationActivity, TProperty>> _propertyExpression;

    public CompensationActivityInputBuilder(
        ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
            TPreviousChainActivity> builder,
        CompensationActivityInfo compensationInfo,
        Expression<Func<TCompensationActivity, TProperty>> propertyExpression)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _compensationInfo = compensationInfo ?? throw new ArgumentNullException(nameof(compensationInfo));
        _propertyExpression = propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));
    }

    public ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
        TPreviousChainActivity> From(Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> source)
    {
        var propertyName = GetPropertyName(_propertyExpression);
        var sourceFunction = CompileToFunction(source);
        var propertyType = typeof(TProperty);

        var mapping = new PropertyMapping
        {
            TargetProperty = propertyName,
            SourceFunction = sourceFunction,
            SourceType = typeof(WorkflowContext<TWorkflowData>),
            TargetType = propertyType,
            Direction = PropertyMappingDirection.Input
        };

        _compensationInfo.InputMappings.Add(mapping);
        return _builder;
    }

    public ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
        TPreviousChainActivity> From(Expression<Func<TPreviousStepData, TProperty>> source)
    {
        var propertyName = GetPropertyName(_propertyExpression);
        var sourceFunction = CompileToFunction(source);
        var propertyType = typeof(TProperty);

        var mapping = new PropertyMapping
        {
            TargetProperty = propertyName,
            SourceFunction = sourceFunction,
            SourceType = typeof(TPreviousStepData),
            TargetType = propertyType,
            Direction = PropertyMappingDirection.Input
        };

        _compensationInfo.InputMappings.Add(mapping);
        return _builder;
    }

    public ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
        TPreviousChainActivity> From(Expression<Func<TPreviousChainActivity, TProperty>> source)
    {
        var propertyName = GetPropertyName(_propertyExpression);
        var sourceFunction = CompileToFunction(source);
        var propertyType = typeof(TProperty);

        var mapping = new PropertyMapping
        {
            TargetProperty = propertyName,
            SourceFunction = sourceFunction,
            SourceType = typeof(TPreviousChainActivity),
            TargetType = propertyType,
            Direction = PropertyMappingDirection.Input
        };

        _compensationInfo.InputMappings.Add(mapping);
        return _builder;
    }

    // Implement ICompensationActivityBuilder interface
    ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TNewProperty, TPreviousStepData,
            TPreviousChainActivity>
        ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
            TPreviousChainActivity>.Input<TNewProperty>(Expression<Func<TCompensationActivity, TNewProperty>> property)
    {
        return _builder.Input(property);
    }

    ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TNewProperty>
        ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
            TPreviousChainActivity>.Output<TNewProperty>(Expression<Func<TCompensationActivity, TNewProperty>> property)
    {
        return _builder.Output(property);
    }

    private string GetPropertyName<T, TProp>(Expression<Func<T, TProp>> expression)
    {
        if (expression.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        throw new ArgumentException("Expression must be a member access", nameof(expression));
    }

    private Func<object, object?> CompileToFunction<TContext, TValue>(Expression<Func<TContext, TValue>> expression)
    {
        // Compile expression directly (same pattern as SagaBuilder)
        var compiled = expression.Compile();
        return context =>
        {
            if (context is TContext typedContext) return compiled(typedContext);

            // Add detailed logging for debugging type mismatches
            var expectedType = typeof(TContext);
            var actualType = context.GetType();

            Console.WriteLine("\ud83d\udd0d COMPENSATION TYPE MISMATCH DEBUG (WithAccess):");
            Console.WriteLine($"  Expected Type: {expectedType.FullName}");
            Console.WriteLine($"  Actual Type: {actualType.FullName}");

            if (expectedType.IsGenericType && actualType.IsGenericType)
            {
                Console.WriteLine($"  Expected Generic Definition: {expectedType.GetGenericTypeDefinition()}");
                Console.WriteLine($"  Actual Generic Definition: {actualType.GetGenericTypeDefinition()}");
                Console.WriteLine(
                    $"  Expected Generic Args: [{string.Join(", ", expectedType.GetGenericArguments().Select(t => t.Name))}]");
                Console.WriteLine(
                    $"  Actual Generic Args: [{string.Join(", ", actualType.GetGenericArguments().Select(t => t.Name))}]");
            }

            throw new InvalidOperationException($"Expected {expectedType.FullName}, got {actualType.FullName}");
        };
    }
}

/// <summary>
///     Compensation activity output builder
/// </summary>
public class CompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty> :
    ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty>,
    ICompensationActivityBuilder<TWorkflowData, TCompensationActivity>
    where TWorkflowData : class
    where TCompensationActivity : IAsyncActivity
{
    private readonly object _builder;
    private readonly CompensationActivityInfo _compensationInfo;
    private readonly Expression<Func<TCompensationActivity, TProperty>> _propertyExpression;

    public CompensationActivityOutputBuilder(
        object builder,
        CompensationActivityInfo compensationInfo,
        Expression<Func<TCompensationActivity, TProperty>> propertyExpression)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _compensationInfo = compensationInfo ?? throw new ArgumentNullException(nameof(compensationInfo));
        _propertyExpression = propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));
    }

    public ICompensationActivityBuilder<TWorkflowData, TCompensationActivity> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination)
    {
        var propertyName = GetPropertyName(_propertyExpression);
        var targetAssignmentFunction = CompileToAssignmentAction(destination);
        var propertyType = typeof(TProperty);

        var mapping = new PropertyMapping
        {
            TargetProperty = propertyName,
            TargetAssignmentFunction = targetAssignmentFunction,
            SourceType = propertyType,
            TargetType = typeof(WorkflowContext<TWorkflowData>),
            Direction = PropertyMappingDirection.Output
        };

        _compensationInfo.OutputMappings.Add(mapping);

        // Return a new 2-generic builder
        return new CompensationActivityBuilder<TWorkflowData, TCompensationActivity>(_compensationInfo);
    }

    // Implement ICompensationActivityBuilder interface
    public ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TNewProperty> Input<TNewProperty>(
        Expression<Func<TCompensationActivity, TNewProperty>> property)
    {
        return new CompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TNewProperty>(
            new CompensationActivityBuilder<TWorkflowData, TCompensationActivity>(_compensationInfo),
            _compensationInfo, property);
    }

    public ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TNewProperty> Output<TNewProperty>(
        Expression<Func<TCompensationActivity, TNewProperty>> property)
    {
        return new CompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TNewProperty>(
            new CompensationActivityBuilder<TWorkflowData, TCompensationActivity>(_compensationInfo),
            _compensationInfo, property);
    }

    private string GetPropertyName<T, TProp>(Expression<Func<T, TProp>> expression)
    {
        if (expression.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        throw new ArgumentException("Expression must be a member access", nameof(expression));
    }

    private Action<object, object?> CompileToAssignmentAction<TContext, TValue>(
        Expression<Func<TContext, TValue>> expression)
    {
        // Compile assignment expression inline (same pattern as SagaBuilder)
        if (expression.Body is not MemberExpression memberExpression)
            throw new InvalidOperationException($"Output destination must be a member access expression: {expression}");

        // Ensure it's a property
        if (memberExpression.Member is not PropertyInfo propertyInfo)
            throw new InvalidOperationException($"Output destination must access a property: {expression}");

        // Ensure the property has a setter
        if (propertyInfo.SetMethod == null)
            throw new InvalidOperationException($"Property {propertyInfo.Name} does not have a setter");

        // Create parameters for the setter expression
        var contextParameter = Expression.Parameter(typeof(TContext), "context");
        var valueParameter = Expression.Parameter(typeof(TValue), "value");

        // Create the property access expression by replacing the parameter
        var propertyAccess = Expression.Property(
            ReplaceParameter(memberExpression.Expression, expression.Parameters[0], contextParameter),
            propertyInfo
        );

        // Create the assignment expression
        var assignment = Expression.Assign(propertyAccess, valueParameter);

        // Create and compile the lambda expression
        var lambda = Expression.Lambda<Action<TContext, TValue>>(assignment, contextParameter, valueParameter);
        var compiled = lambda.Compile();

        return (context, value) =>
        {
            if (context is TContext typedContext && value is TValue typedValue)
                compiled(typedContext, typedValue);
            else if (context is TContext typedContext2 && value == null)
                // Handle null values gracefully
                compiled(typedContext2, default!);
            else
                throw new InvalidOperationException(
                    $"Expected {typeof(TContext).Name} and {typeof(TValue).Name}, got {context.GetType().Name} and {value?.GetType().Name}");
        };
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
}

/// <summary>
///     Compensation activity builder with 2 generics (for interface compatibility)
/// </summary>
public class CompensationActivityBuilder<TWorkflowData, TCompensationActivity> 
    : ICompensationActivityBuilder<TWorkflowData, TCompensationActivity>
    where TWorkflowData : class
    where TCompensationActivity : IAsyncActivity
{
    private readonly CompensationActivityInfo _compensationInfo;

    public CompensationActivityBuilder(CompensationActivityInfo compensationInfo)
    {
        _compensationInfo = compensationInfo ?? throw new ArgumentNullException(nameof(compensationInfo));
    }

    public ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty> Input<TProperty>(
        Expression<Func<TCompensationActivity, TProperty>> property)
    {
        return new CompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty>(
            this, _compensationInfo, property);
    }

    public ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty> Output<TProperty>(
        Expression<Func<TCompensationActivity, TProperty>> property)
    {
        return new CompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TProperty>(
            this, _compensationInfo, property);
    }
}

/// <summary>
///     Compensation activity input builder with 2 generics (for interface compatibility)
/// </summary>
public class CompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty> :
    ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TProperty>
    where TWorkflowData : class
    where TCompensationActivity : IAsyncActivity
{
    private readonly CompensationActivityBuilder<TWorkflowData, TCompensationActivity> _builder;
    private readonly CompensationActivityInfo _compensationInfo;
    private readonly Expression<Func<TCompensationActivity, TProperty>> _propertyExpression;

    public CompensationActivityInputBuilder(
        CompensationActivityBuilder<TWorkflowData, TCompensationActivity> builder,
        CompensationActivityInfo compensationInfo,
        Expression<Func<TCompensationActivity, TProperty>> propertyExpression)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _compensationInfo = compensationInfo ?? throw new ArgumentNullException(nameof(compensationInfo));
        _propertyExpression = propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));
    }

    public ICompensationActivityBuilder<TWorkflowData, TCompensationActivity> From(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> source)
    {
        var propertyName = GetPropertyName(_propertyExpression);
        var sourceFunction = CompileToFunction(source);
        var propertyType = typeof(TProperty);

        var mapping = new PropertyMapping
        {
            TargetProperty = propertyName,
            SourceFunction = sourceFunction,
            SourceType = typeof(WorkflowContext<TWorkflowData>),
            TargetType = propertyType,
            Direction = PropertyMappingDirection.Input
        };

        _compensationInfo.InputMappings.Add(mapping);
        return _builder;
    }

    public ICompensationActivityInputBuilder<TWorkflowData, TCompensationActivity, TNewProperty> Input<TNewProperty>(
        Expression<Func<TCompensationActivity, TNewProperty>> property)
    {
        return _builder.Input(property);
    }

    public ICompensationActivityOutputBuilder<TWorkflowData, TCompensationActivity, TNewProperty> Output<TNewProperty>(
        Expression<Func<TCompensationActivity, TNewProperty>> property)
    {
        return _builder.Output(property);
    }

    private string GetPropertyName<T, TProp>(Expression<Func<T, TProp>> expression)
    {
        if (expression.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        throw new ArgumentException("Expression must be a member access", nameof(expression));
    }

    private Func<object, object?> CompileToFunction<TContext, TValue>(Expression<Func<TContext, TValue>> expression)
    {
        var compiled = expression.Compile();
        return context =>
        {
            if (context is TContext typedContext) return compiled(typedContext);
            throw new InvalidOperationException($"Expected {typeof(TContext).Name}, got {context.GetType().Name}");
        };
    }
}

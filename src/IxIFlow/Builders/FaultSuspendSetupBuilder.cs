using System.Linq.Expressions;
using System.Reflection;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Builder for configuring suspend/resume operations
/// </summary>
internal class FaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData>
    : IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData>
    where TWorkflowData : class
    where TResumeEvent : class
    where TPreviousStepData : class
{
    private readonly WorkflowStep _step;

    internal FaultSuspendSetupBuilder(WorkflowStep step)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
    }

    /// <summary>
    ///     Maps a value into the suggested event payload once when the wait opens.
    /// </summary>
    public IFaultSuspendInputBuilder<TWorkflowData, TFault, TResumeEvent, TProperty, TPreviousStepData> Input<TProperty>(
        Expression<Func<TResumeEvent, TProperty>> property)
    {
        return new FaultSuspendInputBuilder<TWorkflowData, TFault, TResumeEvent, TProperty, TPreviousStepData>(_step, property);
    }

    /// <summary>
    ///     Configures an output mapping for the resume event
    /// </summary>
    public IFaultSuspendOutputBuilder<TWorkflowData, TFault, TResumeEvent, TProperty, TPreviousStepData> Output<TProperty>(
        Expression<Func<TResumeEvent, TProperty>> property)
    {
        return new FaultSuspendOutputBuilder<TWorkflowData, TFault, TResumeEvent, TProperty, TPreviousStepData>(_step, property);
    }

    public IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> DefaultPayload(TResumeEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _step.DefaultPayload = payload;

        return this;
    }

    public IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> TimeoutAfter(
        TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Wait timeout must be positive");

        _step.WaitTimeout = duration;

        return this;
    }

    public IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> OnTimeout(
        Action<IFaultTimeoutWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        if (_step.TimeoutSteps.Count != 0)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                "The wait already has a timeout branch");

        configure(new FaultTimeoutWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(
            _step.TimeoutSteps, typeof(Exception)));

        return this;
    }
}

/// <summary>
///     Builder for configuring input mappings for suspend/resume operations
/// </summary>
internal class FaultSuspendInputBuilder<TWorkflowData, TFault, TResumeEvent, TProperty, TPreviousStepData>
    : IFaultSuspendInputBuilder<TWorkflowData, TFault, TResumeEvent, TProperty, TPreviousStepData>
    where TWorkflowData : class
    where TResumeEvent : class
    where TPreviousStepData : class
{
    private readonly Expression<Func<TResumeEvent, TProperty>> _propertyExpression;
    private readonly WorkflowStep _step;

    public IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> DefaultPayload(TResumeEvent payload) =>
        new FaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData>(_step)
            .DefaultPayload(payload);

    public IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> TimeoutAfter(
        TimeSpan duration) =>
        new FaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData>(_step)
            .TimeoutAfter(duration);

    public IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> OnTimeout(
        Action<IFaultTimeoutWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure) =>
        new FaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData>(_step)
            .OnTimeout(configure);

    internal FaultSuspendInputBuilder(WorkflowStep step, Expression<Func<TResumeEvent, TProperty>> propertyExpression)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _propertyExpression = propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));
    }

    /// <summary>
    ///     Specifies the source expression for this input mapping
    /// </summary>
    public IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> From(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TProperty>> source)
    {
        // Extract property name from the property expression
        var propertyName = GetPropertyName(_propertyExpression);

        // Compile the expression to a fast Func delegate
        var compiledFunction = source.Compile();

        // Create wrapped function for object context
        Func<object, object?> wrappedFunction = context =>
        {
            if (context is FaultContext<TWorkflowData, TFault, TPreviousStepData> typedContext)
                return compiledFunction(typedContext);

            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Expected WorkflowContext<{typeof(TWorkflowData).Name}, {typeof(TPreviousStepData).Name}>, got {context.GetType().Name}");
        };

        // Create property mapping with compiled function
        var mapping = new PropertyMapping
        {
            TargetProperty = propertyName,
            SourceFunction = wrappedFunction,
            Expression = source,
            ContextFactory = Core.Runtime.WorkflowStepDelegates.Fault<TWorkflowData, TFault, TPreviousStepData>,
            SourceType = typeof(TProperty),
            TargetType = typeof(TProperty),
            Direction = PropertyMappingDirection.Input
        };

        _step.InputMappings.Add(mapping);

        return new FaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData>(_step);
    }

    /// <summary>
    ///     Configures an input property mapping
    /// </summary>
    public IFaultSuspendInputBuilder<TWorkflowData, TFault, TResumeEvent, TNextProperty, TPreviousStepData> Input<TNextProperty>(
        Expression<Func<TResumeEvent, TNextProperty>> property)
    {
        return new FaultSuspendInputBuilder<TWorkflowData, TFault, TResumeEvent, TNextProperty, TPreviousStepData>(_step, property);
    }

    /// <summary>
    ///     Configures an output property mapping
    /// </summary>
    public IFaultSuspendOutputBuilder<TWorkflowData, TFault, TResumeEvent, TNextProperty, TPreviousStepData> Output<TNextProperty>(
        Expression<Func<TResumeEvent, TNextProperty>> property)
    {
        return new FaultSuspendOutputBuilder<TWorkflowData, TFault, TResumeEvent, TNextProperty, TPreviousStepData>(_step, property);
    }

    private string GetPropertyName<T>(Expression<Func<TResumeEvent, T>> propertyExpression)
    {
        if (propertyExpression.Body is MemberExpression memberExpression)
            return memberExpression.Member.Name;

        throw new ArgumentException("Property expression must be a member access expression",
            nameof(propertyExpression));
    }
}

/// <summary>
///     Builder for configuring output mappings for suspend/resume operations
/// </summary>
internal class FaultSuspendOutputBuilder<TWorkflowData, TFault, TResumeEvent, TProperty, TPreviousStepData>
    : IFaultSuspendOutputBuilder<TWorkflowData, TFault, TResumeEvent, TProperty, TPreviousStepData>
    where TWorkflowData : class
    where TResumeEvent : class
    where TPreviousStepData : class
{
    private readonly Expression<Func<TResumeEvent, TProperty>> _propertyExpression;
    private readonly WorkflowStep _step;

    public IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> DefaultPayload(TResumeEvent payload) =>
        new FaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData>(_step)
            .DefaultPayload(payload);

    public IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> TimeoutAfter(
        TimeSpan duration) =>
        new FaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData>(_step)
            .TimeoutAfter(duration);

    public IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> OnTimeout(
        Action<IFaultTimeoutWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure) =>
        new FaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData>(_step)
            .OnTimeout(configure);

    internal FaultSuspendOutputBuilder(WorkflowStep step, Expression<Func<TResumeEvent, TProperty>> propertyExpression)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _propertyExpression = propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));
    }

    /// <summary>
    ///     Specifies the destination expression for this output mapping
    /// </summary>
    public IFaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData> To(
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
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
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
            Expression = destination,
            ContextFactory = Core.Runtime.WorkflowStepDelegates.Data<TWorkflowData>,
            TargetPath = Core.Runtime.MemberPath.Of(destination),
            SourceType = typeof(TResumeEvent),
            TargetType = typeof(WorkflowContext<TWorkflowData>),
            Direction = PropertyMappingDirection.Output
        };

        _step.OutputMappings.Add(mapping);

        return new FaultSuspendSetupBuilder<TWorkflowData, TFault, TResumeEvent, TPreviousStepData>(_step);
    }

    /// <summary>
    ///     Configures an input property mapping
    /// </summary>
    public IFaultSuspendInputBuilder<TWorkflowData, TFault, TResumeEvent, TNextProperty, TPreviousStepData> Input<TNextProperty>(
        Expression<Func<TResumeEvent, TNextProperty>> property)
    {
        return new FaultSuspendInputBuilder<TWorkflowData, TFault, TResumeEvent, TNextProperty, TPreviousStepData>(_step, property);
    }

    /// <summary>
    ///     Configures an output property mapping
    /// </summary>
    public IFaultSuspendOutputBuilder<TWorkflowData, TFault, TResumeEvent, TNextProperty, TPreviousStepData> Output<TNextProperty>(
        Expression<Func<TResumeEvent, TNextProperty>> property)
    {
        return new FaultSuspendOutputBuilder<TWorkflowData, TFault, TResumeEvent, TNextProperty, TPreviousStepData>(_step, property);
    }

    /// <summary>
    ///     Compiles the output assignment expression using reflection and expression compilation
    /// </summary>
    private Action<WorkflowContext<TWorkflowData>, TProperty> CompileOutputAssignment(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination)
    {
        // Build a setter for the destination member
        if (destination.Body is not MemberExpression memberExpression)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Output destination must be a member access expression: {destination}");

        // Ensure it's a property
        if (memberExpression.Member is not PropertyInfo propertyInfo)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Output destination must access a property: {destination}");

        // Ensure the property has a setter
        if (propertyInfo.SetMethod == null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Property {propertyInfo.Name} does not have a setter");

        // Create parameters for the setter expression
        var contextParameter = Expression.Parameter(typeof(WorkflowContext<TWorkflowData>), "context");
        var valueParameter = Expression.Parameter(typeof(TProperty), "value");

        // Create the property access expression by replacing the parameter
        var propertyAccess = Expression.Property(
            ReplaceParameterVisitor.Replace(memberExpression.Expression!, destination.Parameters[0], contextParameter),
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

    private string GetPropertyName<T>(Expression<Func<TResumeEvent, T>> propertyExpression)
    {
        if (propertyExpression.Body is MemberExpression memberExpression)
            return memberExpression.Member.Name;

        throw new ArgumentException("Property expression must be a member access expression",
            nameof(propertyExpression));
    }
}
using System.Linq.Expressions;
using System.Reflection;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Try builder implementation for exception handling (with previous step data)
/// </summary>
public class TryBuilder<TWorkflowData, TPreviousStepData>(List<WorkflowStep> steps, WorkflowStep tryStep)
    : ITryBuilder<TWorkflowData, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    private readonly List<WorkflowStep> _steps = steps ?? throw new ArgumentNullException(nameof(steps));
    private readonly WorkflowStep _tryStep = tryStep ?? throw new ArgumentNullException(nameof(tryStep));

    public ICatchBuilder<TWorkflowData, TPreviousStepData> Catch<TException>(
        Action<ICatchWorkflowBuilder<TWorkflowData, TException, TPreviousStepData>> configure)
        where TException : Exception
    {
        // Create catch block for this exception type
        var catchBlock = new WorkflowStep
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"Catch<{typeof(TException).Name}>",
            StepType = WorkflowStepType.CatchBlock,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            ExceptionType = typeof(TException),
            Order = _tryStep.CatchBlocks.Count
        };

        // Configure catch block with specialized builder
        var catchWorkflowBuilder =
            new CatchWorkflowBuilder<TWorkflowData, TException, TPreviousStepData>(catchBlock.SequenceSteps);
        configure(catchWorkflowBuilder);

        // Add to try step's catch blocks
        _tryStep.CatchBlocks.Add(catchBlock);

        // Return catch builder for chaining
        return new CatchBuilder<TWorkflowData, TPreviousStepData>(_steps, _tryStep);
    }

    public ICatchBuilder<TWorkflowData, TPreviousStepData> Catch(
        Action<ICatchWorkflowBuilder<TWorkflowData, Exception, TPreviousStepData>> configure)
    {
        // Create catch-all block for any Exception type
        var catchBlock = new WorkflowStep
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Catch<Exception>", // Catch-all
            StepType = WorkflowStepType.CatchBlock,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            ExceptionType = typeof(Exception),
            Order = _tryStep.CatchBlocks.Count
        };

        // Configure catch block with specialized builder
        var catchWorkflowBuilder =
            new CatchWorkflowBuilder<TWorkflowData, Exception, TPreviousStepData>(catchBlock.SequenceSteps);
        configure(catchWorkflowBuilder);

        // Add to try step's catch blocks
        _tryStep.CatchBlocks.Add(catchBlock);

        // Return catch builder for chaining
        return new CatchBuilder<TWorkflowData, TPreviousStepData>(_steps, _tryStep);
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Finally(
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        // Create finally block using SequenceBuilderContinuation pattern
        var finallyBuilder = new WorkflowBuilder<TWorkflowData, TPreviousStepData>(_tryStep.FinallySteps);
        configure(finallyBuilder);

        // Return original workflow builder to continue workflow
        return new WorkflowBuilder<TWorkflowData, TPreviousStepData>(_steps);
    }
}

/// <summary>
///     Catch builder implementation (with previous step data)
/// </summary>
public class CatchBuilder<TWorkflowData, TPreviousStepData>(List<WorkflowStep> steps, WorkflowStep tryStep)
    : WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps), ICatchBuilder<TWorkflowData, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    private readonly WorkflowStep _tryStep = tryStep ?? throw new ArgumentNullException(nameof(tryStep));

    public ICatchBuilder<TWorkflowData, TPreviousStepData> Catch(
        Action<ICatchWorkflowBuilder<TWorkflowData, Exception, TPreviousStepData>> configure)
        => Catch<Exception>(configure);

    public ICatchBuilder<TWorkflowData, TPreviousStepData> Catch<TException>(
        Action<ICatchWorkflowBuilder<TWorkflowData, TException, TPreviousStepData>> configure)
        where TException : Exception
    {
        // Create another catch block for this exception type
        var catchBlock = new WorkflowStep
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"Catch<{typeof(TException).Name}>",
            StepType = WorkflowStepType.CatchBlock,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            ExceptionType = typeof(TException),
            Order = _tryStep.CatchBlocks.Count
        };

        // Configure catch block with specialized builder
        var catchWorkflowBuilder =
            new CatchWorkflowBuilder<TWorkflowData, TException, TPreviousStepData>(catchBlock.SequenceSteps);
        configure(catchWorkflowBuilder);

        // Add to try step's catch blocks
        _tryStep.CatchBlocks.Add(catchBlock);

        // Return this for chaining
        return this;
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Finally(
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        // Create finally block using SequenceBuilderContinuation pattern
        var finallyBuilder = new WorkflowBuilder<TWorkflowData, TPreviousStepData>(_tryStep.FinallySteps);
        configure(finallyBuilder);

        // Return original workflow builder to continue workflow
        return new WorkflowBuilder<TWorkflowData, TPreviousStepData>(_steps);
    }
}

/// <summary>
///     Catch workflow builder that provides access to exception context (with previous step data)
/// </summary>
public class CatchWorkflowBuilder<TWorkflowData, TException, TPreviousStepData>(List<WorkflowStep> steps)
    : ICatchWorkflowBuilder<TWorkflowData, TException, TPreviousStepData>
    where TWorkflowData : class
    where TException : Exception
    where TPreviousStepData : class
{
    private readonly List<WorkflowStep> _steps = steps ?? throw new ArgumentNullException(nameof(steps));

    public IWorkflowBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TException, TPreviousStepData>> configure)
        where TActivity : class, IAsyncActivity
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid().ToString(),
            Name = typeof(TActivity).Name,
            StepType = WorkflowStepType.Activity,
            ActivityType = typeof(TActivity),
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            ExceptionType = typeof(TException),
            Order = _steps.Count
        };

        // Create catch activity builder and configure it
        var activityBuilder = new CatchActivitySetupBuilder<TWorkflowData, TActivity, TException, TPreviousStepData>(step);
        configure(activityBuilder);

        _steps.Add(step);

        // Return workflow builder with TActivity as previous step type
        return new WorkflowBuilder<TWorkflowData, TActivity>(_steps);
    }
}

/// <summary>
///     Catch activity builder that provides exception context input mappings (with previous step data)
/// </summary>
public class CatchActivitySetupBuilder<TWorkflowData, TActivity, TException, TPreviousStepData>(WorkflowStep step)
    : ICatchActivitySetupBuilder<TWorkflowData,
        TActivity, TException, TPreviousStepData>
    where TWorkflowData : class
    where TActivity : class, IAsyncActivity
    where TException : Exception
    where TPreviousStepData : class
{
    private readonly WorkflowStep _step = step ?? throw new ArgumentNullException(nameof(step));

    public ICatchActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TException, TPreviousStepData>
        Input<TProperty>(Expression<Func<TActivity, TProperty>> property)
    {
        return new CatchActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TException, TPreviousStepData>(_step,
            property);
    }

    public ICatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TException, TPreviousStepData>
        Output<TProperty>(Expression<Func<TActivity, TProperty>> property)
    {
        return new CatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TException, TPreviousStepData>(_step,
            property);
    }
}

/// <summary>
///     Catch activity input builder for exception context mappings (with previous step data)
/// </summary>
public class CatchActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TException, TPreviousStepData>(
    WorkflowStep step, Expression<Func<TActivity, TProperty>> property)
    : ICatchActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TException, TPreviousStepData>
    where TWorkflowData : class
    where TActivity : class, IAsyncActivity
    where TException : Exception
    where TPreviousStepData : class
{
    private readonly Expression<Func<TActivity, TProperty>> _property = property ?? throw new ArgumentNullException(nameof(property));
    private readonly WorkflowStep _step = step ?? throw new ArgumentNullException(nameof(step));

    public ICatchActivitySetupBuilder<TWorkflowData, TActivity, TException, TPreviousStepData> From(
        Expression<Func<CatchContext<TWorkflowData, TException, TPreviousStepData>, TProperty>> source)
    {
        // Extract property name from the target property expression
        var targetPropertyName = GetPropertyName(_property);

        // Compile the expression to a fast Func delegate
        var compiledFunction = source.Compile();

        // Create wrapped function for object context with more flexible type checking
        Func<object, object?> wrappedFunction = context =>
        {
            // Try direct cast first
            if (context is CatchContext<TWorkflowData, TException, TPreviousStepData> typedContext)
                return compiledFunction(typedContext);

            // Check if it's a compatible CatchContext type (for inheritance scenarios)
            var contextType = context.GetType();
            if (contextType.IsGenericType && contextType.GetGenericTypeDefinition() == typeof(CatchContext<,,>))
            {
                var genericArgs = contextType.GetGenericArguments();

                // Check if the workflow data type matches
                if (genericArgs[0] == typeof(TWorkflowData))
                    // Check if the exception type is assignable (for inheritance)
                    if (typeof(TException).IsAssignableFrom(genericArgs[1]))
                        // Check if the previous step data type matches
                        if (genericArgs[2] == typeof(TPreviousStepData))
                            // Use reflection to access the properties
                            try
                            {
                                // Create a compatible context by copying properties
                                var compatibleContext =
                                    new CatchContext<TWorkflowData, TException, TPreviousStepData>();

                                // Copy WorkflowData
                                var workflowDataProp = contextType.GetProperty("WorkflowData");
                                if (workflowDataProp != null)
                                    compatibleContext.WorkflowData = (TWorkflowData)workflowDataProp.GetValue(context)!;

                                // Copy Exception (with casting)
                                var exceptionProp = contextType.GetProperty("Exception");
                                if (exceptionProp != null)
                                    compatibleContext.Exception = (TException)exceptionProp.GetValue(context)!;

                                // Copy PreviousStep
                                var previousStepProp = contextType.GetProperty("PreviousStep");
                                if (previousStepProp != null)
                                    compatibleContext.PreviousStep =
                                        (TPreviousStepData)previousStepProp.GetValue(context)!;

                                // Copy other properties
                                var props = new[]
                                {
                                    "WorkflowInstanceId", "WorkflowName", "WorkflowVersion", "CurrentStepNumber",
                                    "StartedAt", "CorrelationId", "FailedStepName", "RetryAttempt", "PreviousStepName",
                                    "PreviousStepExecutedAt"
                                };

                                foreach (var propName in props)
                                {
                                    var sourceProp = contextType.GetProperty(propName);
                                    var targetProp = typeof(CatchContext<TWorkflowData, TException, TPreviousStepData>)
                                        .GetProperty(propName);
                                    if (sourceProp != null && targetProp != null && targetProp.CanWrite)
                                    {
                                        var value = sourceProp.GetValue(context);
                                        targetProp.SetValue(compatibleContext, value);
                                    }
                                }

                                // Copy Properties dictionary
                                var propertiesProp = contextType.GetProperty("Properties");
                                if (propertiesProp != null &&
                                    propertiesProp.GetValue(context) is Dictionary<string, object> sourceProps)
                                    compatibleContext.Properties = new Dictionary<string, object>(sourceProps);

                                // Copy ExecutionStackTrace
                                var stackTraceProp = contextType.GetProperty("ExecutionStackTrace");
                                if (stackTraceProp != null && stackTraceProp.GetValue(context) is string[] stackTrace)
                                    compatibleContext.ExecutionStackTrace = stackTrace;

                                return compiledFunction(compatibleContext);
                            }
                            catch (Exception ex)
                            {
                                throw new InvalidOperationException($"Failed to convert CatchContext: {ex.Message}",
                                    ex);
                            }
            }

            throw new InvalidOperationException(
                $"Expected CatchContext<{typeof(TWorkflowData).Name}, {typeof(TException).Name}, {typeof(TPreviousStepData).Name}>, got {context.GetType().Name}");
        };

        var mapping = new PropertyMapping
        {
            Direction = PropertyMappingDirection.Input,
            TargetProperty = targetPropertyName,
            TargetType = typeof(TProperty),
            SourceType = typeof(TProperty),
            SourceFunction = wrappedFunction
        };

        _step.InputMappings.Add(mapping);

        return new CatchActivitySetupBuilder<TWorkflowData, TActivity, TException, TPreviousStepData>(_step);
    }

    // Implement all other interface methods by returning the base activity builder
    public ICatchActivitySetupInputBuilder<TWorkflowData, TActivity, TNewProperty, TException, TPreviousStepData>
        Input<TNewProperty>(Expression<Func<TActivity, TNewProperty>> property)
    {
        return new CatchActivitySetupInputBuilder<TWorkflowData, TActivity, TNewProperty, TException, TPreviousStepData>(
            _step, property);
    }

    public ICatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TNewProperty, TException, TPreviousStepData>
        Output<TNewProperty>(Expression<Func<TActivity, TNewProperty>> property)
    {
        return new CatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TNewProperty, TException, TPreviousStepData>(
            _step, property);
    }

    private string GetPropertyName<T>(Expression<Func<TActivity, T>> propertyExpression)
    {
        if (propertyExpression.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        throw new ArgumentException("Property expression must be a member access expression",
            nameof(propertyExpression));
    }
}

/// <summary>
///     Catch activity output builder for exception context mappings (with TException and TPreviousStepData)
/// </summary>
public class CatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TException, TPreviousStepData>(
    WorkflowStep step, Expression<Func<TActivity, TProperty>> property)
    : ICatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TException, TPreviousStepData>
    where TWorkflowData : class
    where TActivity : class, IAsyncActivity
    where TException : Exception
    where TPreviousStepData : class
{
    private readonly Expression<Func<TActivity, TProperty>> _property = property ?? throw new ArgumentNullException(nameof(property));
    private readonly WorkflowStep _step = step ?? throw new ArgumentNullException(nameof(step));

    public ICatchActivitySetupBuilder<TWorkflowData, TActivity, TException, TPreviousStepData> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination)
    {
        // Extract property name from the property expression
        var propertyName = GetPropertyName(_property);

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

        return new CatchActivitySetupBuilder<TWorkflowData, TActivity, TException, TPreviousStepData>(_step);
    }

    // Implement ICatchActivitySetupBuilder methods
    public ICatchActivitySetupInputBuilder<TWorkflowData, TActivity, TNewProperty, TException, TPreviousStepData>
        Input<TNewProperty>(Expression<Func<TActivity, TNewProperty>> property)
    {
        return new CatchActivitySetupInputBuilder<TWorkflowData, TActivity, TNewProperty, TException, TPreviousStepData>(
            _step, property);
    }

    public ICatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TNewProperty, TException, TPreviousStepData>
        Output<TNewProperty>(Expression<Func<TActivity, TNewProperty>> property)
    {
        return new CatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TNewProperty, TException, TPreviousStepData>(
            _step, property);
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

    private string GetPropertyName<T>(Expression<Func<TActivity, T>> propertyExpression)
    {
        if (propertyExpression.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        throw new ArgumentException("Property expression must be a member access expression",
            nameof(propertyExpression));
    }
}
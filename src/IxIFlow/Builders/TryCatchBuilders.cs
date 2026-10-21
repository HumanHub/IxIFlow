using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Builders;

/// <summary>Builds a try block and its typed fault handlers.</summary>
public sealed class TryBuilder<TWorkflowData, TPreviousStepData>(
    List<WorkflowStep> steps, WorkflowStep tryStep, string name, int workflowVersion, List<WorkflowDemand> demands)
    : ITryBuilder<TWorkflowData, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    public ICatchBuilder<TWorkflowData, TPreviousStepData> Catch<TException>(
        Action<ICatchWorkflowBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TException : Exception => Catch<TException, EmptyFault>(configure, id, sourcePath, sourceLine);

    public ICatchBuilder<TWorkflowData, TPreviousStepData> Catch(
        Action<ICatchWorkflowBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        Catch<Exception, EmptyFault>(configure, id, sourcePath, sourceLine);

    public ICatchBuilder<TWorkflowData, TPreviousStepData> Catch<TException, TFault>(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(configure);
        FaultProjection.For(typeof(TException), typeof(TFault));
        var catchBlock = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Name = $"Catch<{typeof(TException).Name}>",
            StepType = WorkflowStepType.CatchBlock,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            ExceptionType = typeof(TException),
            FaultType = typeof(TFault),
            Id = id ?? Guid.NewGuid().ToString(),
            SourceLocation = sourcePath + ":" + sourceLine,
            Order = tryStep.CatchBlocks.Count
        };
        configure(new CatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(
            catchBlock.SequenceSteps, typeof(TException)));
        tryStep.CatchBlocks.Add(catchBlock);
        return new CatchBuilder<TWorkflowData, TPreviousStepData>(steps, tryStep, name, workflowVersion, demands);
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Finally(
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        configure(new WorkflowBuilder<TWorkflowData, TPreviousStepData>(tryStep.FinallySteps, string.Empty, demands: demands));
        return new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, name, workflowVersion, demands);
    }
}

/// <summary>Adds another catch or a finally block to a try structure.</summary>
public sealed class CatchBuilder<TWorkflowData, TPreviousStepData>(
    List<WorkflowStep> steps, WorkflowStep tryStep, string name, int workflowVersion, List<WorkflowDemand> demands)
    : WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, name, workflowVersion, demands),
        ICatchBuilder<TWorkflowData, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    public ICatchBuilder<TWorkflowData, TPreviousStepData> Catch(
        Action<ICatchWorkflowBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        Catch<Exception, EmptyFault>(configure, id, sourcePath, sourceLine);

    public ICatchBuilder<TWorkflowData, TPreviousStepData> Catch<TException>(
        Action<ICatchWorkflowBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TException : Exception => Catch<TException, EmptyFault>(configure, id, sourcePath, sourceLine);

    public ICatchBuilder<TWorkflowData, TPreviousStepData> Catch<TException, TFault>(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(configure);
        FaultProjection.For(typeof(TException), typeof(TFault));
        var catchBlock = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Name = $"Catch<{typeof(TException).Name}>",
            StepType = WorkflowStepType.CatchBlock,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            ExceptionType = typeof(TException),
            FaultType = typeof(TFault),
            Id = id ?? Guid.NewGuid().ToString(),
            SourceLocation = sourcePath + ":" + sourceLine,
            Order = tryStep.CatchBlocks.Count
        };
        configure(new CatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(
            catchBlock.SequenceSteps, typeof(TException)));
        tryStep.CatchBlocks.Add(catchBlock);
        return this;
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Finally(
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        configure(new WorkflowBuilder<TWorkflowData, TPreviousStepData>(tryStep.FinallySteps, string.Empty, demands: demands));
        // This builder already continues the same steps, name and version.
        return this;
    }
}

/// <summary>Builds activities inside one catch body.</summary>
public sealed class CatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(
    List<WorkflowStep> steps, Type exceptionType)
    : ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        Core.Runtime.WorkflowStepIdentity.Assign(steps, id, sourcePath, sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }

    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> Saga(
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var result = new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).Saga(configure, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return result;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TActivity> Step<TActivity>(
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity
    {
        var step = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Name = typeof(TActivity).Name,
            StepType = WorkflowStepType.Activity,
            ActivityType = typeof(TActivity),
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            ExceptionType = exceptionType,
            FaultType = typeof(TFault),
            Order = steps.Count
        };
        configure?.Invoke(new CatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>(step));
        steps.Add(step);
        CaptureFaults(steps[^1]);
        return new CatchWorkflowBuilder<TWorkflowData, TFault, TActivity>(steps, exceptionType);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Expression<Func<TEvent, WorkflowContext<TWorkflowData>, bool>>? matchesExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        var result = WaitFor<TEvent>(key, matchesExpression?.Compile(), configure, sourcePath: sourcePath, sourceLine: sourceLine);
        steps[^1].ConditionExpression = matchesExpression;
        return result;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, WorkflowContext<TWorkflowData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty)
            .WaitFor(key, matches, configure, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return new CatchWorkflowBuilder<TWorkflowData, TFault, TEvent>(steps, exceptionType);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> Suspend<TEvent>(
        string reason,
        Expression<Func<TEvent, WorkflowContext<TWorkflowData>, bool>>? matchesExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        var result = Suspend<TEvent>(reason, matchesExpression?.Compile(), configure, sourcePath: sourcePath, sourceLine: sourceLine);
        steps[^1].ConditionExpression = matchesExpression;
        return result;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> Suspend<TEvent>(
        string reason,
        Func<TEvent, WorkflowContext<TWorkflowData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty)
            .Suspend(reason, matches, configure, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return new CatchWorkflowBuilder<TWorkflowData, TFault, TEvent>(steps, exceptionType);
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Parallel(
        Action<IParallelBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).Parallel(configure, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TWorkflow, TInvokedData>(
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TWorkflow : IWorkflow<TInvokedData>
        where TInvokedData : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty)
            .Invoke<TWorkflow, TInvokedData>(configure, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return new CatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData>(steps, exceptionType);
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TInvokedData>(
        string workflowName, int version,
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty)
            .Invoke(workflowName, version, configure, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return new CatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData>(steps, exceptionType);
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Sequence(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).Sequence(configure, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> If(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> condition,
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> then,
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>>? @else = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).If(condition, then, @else, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> conditionExpression, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var result = DoWhile(configure, conditionExpression.Compile(), sourcePath: sourcePath, sourceLine: sourceLine);
        steps[^1].ConditionExpression = conditionExpression;
        return result;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).DoWhile(configure, condition, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WhileDo(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> conditionExpression,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var result = WhileDo(conditionExpression.Compile(), configure, sourcePath: sourcePath, sourceLine: sourceLine);
        steps[^1].ConditionExpression = conditionExpression;
        return result;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WhileDo(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).WhileDo(condition, configure, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> ForEach<TItem>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, IEnumerable<TItem>?>> itemsExpression,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var result = ForEach(itemsExpression.Compile(), body, setup, sourcePath: sourcePath, sourceLine: sourceLine);
        steps[^1].ForEachItemsExpression = itemsExpression;
        return result;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> ForEach<TItem>(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, IEnumerable<TItem>?> items,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).ForEach(items, body, setup, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }

    public ITryBuilder<TWorkflowData, TPreviousStepData> Try(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var result = new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).Try(configure, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return result;
    }
    private static void CaptureFaults(WorkflowStep step)
    {
        step.Runtime = step.Runtime.ForFault<TFault>();
        foreach (var child in step.ChildScopes().Where(scope => scope.Kind != "catch")
                     .SelectMany(scope => scope.Steps))
            CaptureFaults(child);
    }

}

public sealed class CatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>(WorkflowStep step)
    : ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>
    where TWorkflowData : class
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    public ICatchActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TFault, TPreviousStepData>
        Input<TProperty>(Expression<Func<TActivity, TProperty>> property) => new
            CatchActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TFault, TPreviousStepData>(step, property);

    public ICatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TFault, TPreviousStepData>
        Output<TProperty>(Expression<Func<TActivity, TProperty>> property) => new
            CatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TFault, TPreviousStepData>(step, property);
}

public sealed class CatchActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TFault, TPreviousStepData>(
    WorkflowStep step, Expression<Func<TActivity, TProperty>> property)
    : ICatchActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TFault, TPreviousStepData>
    where TWorkflowData : class
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    public ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData> From(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TProperty>> source)
    {
        if (property.Body is not MemberExpression member)
            throw new ArgumentException("Input property must be a member", nameof(property));
        var compiled = source.Compile();
        step.InputMappings.Add(new PropertyMapping
        {
            Direction = PropertyMappingDirection.Input,
            TargetProperty = member.Member.Name,
            Expression = source,
            ContextFactory = Core.Runtime.WorkflowStepDelegates.Fault<TWorkflowData, TFault, TPreviousStepData>,
            TargetType = typeof(TProperty),
            SourceType = typeof(TProperty),
            SourceFunction = context => compiled((FaultContext<TWorkflowData, TFault, TPreviousStepData>)context)
        });
        return new CatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>(step);
    }

    public ICatchActivitySetupInputBuilder<TWorkflowData, TActivity, TNewProperty, TFault, TPreviousStepData>
        Input<TNewProperty>(Expression<Func<TActivity, TNewProperty>> next) => new
            CatchActivitySetupInputBuilder<TWorkflowData, TActivity, TNewProperty, TFault, TPreviousStepData>(step, next);

    public ICatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TNewProperty, TFault, TPreviousStepData>
        Output<TNewProperty>(Expression<Func<TActivity, TNewProperty>> next) => new
            CatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TNewProperty, TFault, TPreviousStepData>(step, next);
}

public sealed class CatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TFault, TPreviousStepData>(
    WorkflowStep step, Expression<Func<TActivity, TProperty>> property)
    : ICatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TFault, TPreviousStepData>
    where TWorkflowData : class
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    public ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination)
    {
        new ActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty>(step, property).To(destination);
        return new CatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>(step);
    }

    public ICatchActivitySetupInputBuilder<TWorkflowData, TActivity, TNewProperty, TFault, TPreviousStepData>
        Input<TNewProperty>(Expression<Func<TActivity, TNewProperty>> next) => new
            CatchActivitySetupInputBuilder<TWorkflowData, TActivity, TNewProperty, TFault, TPreviousStepData>(step, next);

    public ICatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TNewProperty, TFault, TPreviousStepData>
        Output<TNewProperty>(Expression<Func<TActivity, TNewProperty>> next) => new
            CatchActivitySetupOutputBuilder<TWorkflowData, TActivity, TNewProperty, TFault, TPreviousStepData>(step, next);
}

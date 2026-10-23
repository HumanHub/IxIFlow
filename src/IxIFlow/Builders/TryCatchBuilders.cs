using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Builders;

/// <summary>Builds a try block and its typed fault handlers.</summary>
public sealed class TryBuilder<TWorkflowData, TPreviousStepData>(
    List<WorkflowStep> steps, WorkflowStep tryStep, string name, int workflowVersion, List<WorkflowDemand> demands, IReadOnlyList<string>? declaredInputs = null, IReadOnlyList<string>? declaredOutputs = null)
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
        return new CatchBuilder<TWorkflowData, TPreviousStepData>(steps, tryStep, name, workflowVersion, demands, declaredInputs, declaredOutputs);
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Finally(
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        configure(new WorkflowBuilder<TWorkflowData, TPreviousStepData>(tryStep.FinallySteps, string.Empty, demands: demands));
        return new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, name, workflowVersion, demands, declaredInputs, declaredOutputs);
    }
}

/// <summary>Adds another catch or a finally block to a try structure.</summary>
public sealed class CatchBuilder<TWorkflowData, TPreviousStepData>(
    List<WorkflowStep> steps, WorkflowStep tryStep, string name, int workflowVersion, List<WorkflowDemand> demands, IReadOnlyList<string>? declaredInputs = null, IReadOnlyList<string>? declaredOutputs = null)
    : WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, name, workflowVersion, demands, declaredInputs, declaredOutputs),
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
public class CatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(
    List<WorkflowStep> steps, Type exceptionType)
    : ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Throw<TException>(Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TException>> value,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(value);
        var evaluate = value.Compile();
        var step = Core.Runtime.ThrowConstruction.Leaf<TWorkflowData, TPreviousStepData>(WorkflowStepType.Throw, sourcePath, sourceLine);
        step.ExceptionType = typeof(TException);
        step.ExceptionExpression = value;
        step.ExceptionFactoryReturnType = value.ReturnType;
        step.ExceptionFactory = context => evaluate((FaultContext<TWorkflowData, TFault, TPreviousStepData>)context);
        CatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>.CaptureFaults(step);
        steps.Add(step);
        return this;
    }
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Rethrow([CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        steps.Add(Core.Runtime.ThrowConstruction.Leaf<TWorkflowData, TPreviousStepData>(WorkflowStepType.Rethrow, sourcePath, sourceLine));
        return this;
    }

    protected List<WorkflowStep> Steps => steps;
    protected Type ExceptionType => exceptionType;

    private CatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Wrap(IWorkflowBuilder<TWorkflowData, TPreviousStepData> body) =>
        new(((WorkflowBuilder<TWorkflowData, TPreviousStepData>)body).Steps, exceptionType);

    private WorkflowStep Container(WorkflowStepType kind, string path, int line) => new()
    {
        StepType = kind, Name = kind.ToString(), SourceLocation = path + ":" + line,
        WorkflowDataType = typeof(TWorkflowData), PreviousStepDataType = typeof(TPreviousStepData),
        Runtime = WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>()
    };

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Named(string name)
    {
        WorkflowStepIdentity.Name(steps, name);
        return this;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Delay(TimeSpan duration,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).Delay(duration, sourcePath, sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }
    internal ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> AppendTransition(string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).Transition(to, sourcePath, sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TActivity> Request<TActivity>(string name,
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>>? configure = null,
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, string>>? subjectExpression = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TActivity : class, IAsyncActivity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var reply = RequestPairing.ReplyTypeOf(typeof(TActivity));
        var next = Step(configure, sourcePath, sourceLine);
        var step = steps[^1];
        step.RequestName = name;
        step.ReplyType = reply;
        step.RequestSubjectExpression = subjectExpression;
        if (subjectExpression != null)
        {
            var evaluate = subjectExpression.Compile();
            step.RequestSubject = context => evaluate((FaultContext<TWorkflowData, TFault, TPreviousStepData>)context);
        }
        return next;
    }

    /// <summary>Assigns one serialized workflow-data location, retaining the previous value.</summary>
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Assign<TValue>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> target,
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> value,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        steps.Add(AssignmentRules.Step<TWorkflowData, TPreviousStepData>(target, value, typeof(TValue), sourcePath, sourceLine, steps.Count));
        CaptureFaults(steps[^1]);
        return this;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Switch<TValue>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> selector,
        Action<SwitchCases<TValue, ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var step = Container(WorkflowStepType.Switch, sourcePath, sourceLine);
        var evaluate = selector.Compile();
        step.OutcomeExpression = selector;
        step.OutcomeSelector = context => evaluate((FaultContext<TWorkflowData, TFault, TPreviousStepData>)context);
        step.SwitchValueType = typeof(TValue);
        step.SwitchEquals = (left, right) => EqualityComparer<TValue>.Default.Equals((TValue)left!, (TValue)right!);
        configure(new(step, body => new CatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(body, exceptionType)));
        steps.Add(step);
        CaptureFaults(step);
        return this;
    }

    internal ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> AppendResult<TResult>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TResult>> valueExpression,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var evaluate = valueExpression.Compile();
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).Result(
            ctx => evaluate((FaultContext<TWorkflowData, TFault, TPreviousStepData>)ctx), sourcePath, sourceLine);
        steps[^1].ResultExpression = valueExpression;
        CaptureFaults(steps[^1]);
        return this;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        Core.Runtime.WorkflowStepIdentity.Assign(steps, id, sourcePath, sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }

    public IFaultSagaContainerBuilder<TWorkflowData, TFault, TPreviousStepData> Saga(
        Action<IFaultSagaActivityBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var step = Container(WorkflowStepType.Saga, sourcePath, sourceLine);
        var sagaSteps = new List<SagaStepInfo>();
        configure(new FaultSagaActivityBuilder<TWorkflowData, TFault, TPreviousStepData>(step.SequenceSteps, sagaSteps));
        step.StepMetadata["SagaSteps"] = sagaSteps;
        steps.Add(step);
        CaptureFaults(step);
        return new FaultSagaContainerBuilder<TWorkflowData, TFault, TPreviousStepData>(steps, exceptionType);
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TScope> Scope<TScope>(
        Action<IScopeSetupBuilder<TWorkflowData, TScope, TPreviousStepData>>? setup,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TScope>> body,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TScope : class, IScopeActivity
    {
        var step = Core.Runtime.ScopeConstruction.Create<TWorkflowData, TPreviousStepData, TScope>(steps.Count, sourcePath, sourceLine);
        setup?.Invoke(new ScopeSetupBuilder<TWorkflowData, TScope, TPreviousStepData>(step));
        body(new CatchWorkflowBuilder<TWorkflowData, TFault, TScope>(step.ScopeBodySteps, exceptionType));
        steps.Add(step);
        CaptureFaults(step);
        return new CatchWorkflowBuilder<TWorkflowData, TFault, TScope>(steps, exceptionType);
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
        Expression<Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>>? matchesExpression = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        var result = WaitFor<TEvent>(key, matchesExpression?.Compile(), configure, sourcePath: sourcePath, sourceLine: sourceLine);
        steps[^1].ConditionExpression = matchesExpression;
        return result;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>? matches = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty)
            .WaitFor<TEvent>(key, matches: null, configure: configure == null ? null : setup => configure(new FaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>(((SuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>)setup).Step)), sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        if (matches != null) steps[^1].CompiledCondition = context =>
        {
            var value = (FaultEventValue<TEvent>)context;
            return matches(value.Event, (FaultContext<TWorkflowData, TFault, TPreviousStepData>)value.Context);
        };
        return new CatchWorkflowBuilder<TWorkflowData, TFault, TEvent>(steps, exceptionType);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> Suspend<TEvent>(
        string reason,
        Expression<Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>>? matchesExpression = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        var result = Suspend<TEvent>(reason, matchesExpression?.Compile(), configure, sourcePath: sourcePath, sourceLine: sourceLine);
        steps[^1].ConditionExpression = matchesExpression;
        return result;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> Suspend<TEvent>(
        string reason,
        Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>? matches = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty)
            .Suspend<TEvent>(reason, resumeCondition: null, configure: configure == null ? null : setup => configure(new FaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>(((SuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>)setup).Step)), sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        if (matches != null) steps[^1].CompiledCondition = context =>
        {
            var value = (FaultEventValue<TEvent>)context;
            return matches(value.Event, (FaultContext<TWorkflowData, TFault, TPreviousStepData>)value.Context);
        };
        return new CatchWorkflowBuilder<TWorkflowData, TFault, TEvent>(steps, exceptionType);
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Parallel(
        Action<IFaultParallelBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var step = new WorkflowStep
        {
            Name = "Parallel", StepType = WorkflowStepType.Parallel,
            WorkflowDataType = typeof(TWorkflowData), PreviousStepDataType = typeof(TPreviousStepData),
            Runtime = WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            SourceLocation = sourcePath + ":" + sourceLine
        };
        configure(new FaultParallelBuilder<TWorkflowData, TFault, TPreviousStepData>(step, exceptionType));
        steps.Add(step);
        CaptureFaults(steps[^1]);
        return this;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TUsedData> Use<TWorkflow, TUsedData>(
        Action<IWorkflowInlineSetupBuilder<TWorkflowData, TUsedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TWorkflow : IWorkflow<TUsedData>, new()
        where TUsedData : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty)
            .Use<TWorkflow, TUsedData>(configure, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return new CatchWorkflowBuilder<TWorkflowData, TFault, TUsedData>(steps, exceptionType);
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TUsedData> Use<TUsedData>(
        Func<WorkflowDefinition> callee, WorkflowInlinePin pin,
        Action<IWorkflowInlineSetupBuilder<TWorkflowData, TUsedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TUsedData : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty)
            .Use<TUsedData>(callee, pin, configure, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return new CatchWorkflowBuilder<TWorkflowData, TFault, TUsedData>(steps, exceptionType);
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TWorkflow, TInvokedData>(
        Action<FaultInvocationSetupBuilder<TWorkflowData, TFault, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TWorkflow : IWorkflow<TInvokedData>
        where TInvokedData : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty)
            .Invoke<TWorkflow, TInvokedData>(configure == null ? null : setup => configure(new(setup, ((WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>)setup).Step)), sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return new CatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData>(steps, exceptionType);
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TInvokedData>(
        string workflowName, int version,
        Action<FaultInvocationSetupBuilder<TWorkflowData, TFault, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty)
            .Invoke<TInvokedData>(workflowName, version, configure == null ? null : setup => configure(new(setup, ((WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>)setup).Step)), sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return new CatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData>(steps, exceptionType);
    }

    /// <summary>Invokes a workflow using an explicit pinned input and output contract.</summary>
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TInvokedData>(
        string workflowName, int version, WorkflowContract contract,
        Action<FaultInvocationSetupBuilder<TWorkflowData, TFault, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty)
            .Invoke<TInvokedData>(workflowName, version, contract, configure == null ? null : setup => configure(new(setup, ((WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>)setup).Step)), sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return new CatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData>(steps, exceptionType);
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Sequence(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).Sequence(body => configure(Wrap(body)), sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Flowchart(
        Action<FaultFlowchartBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var step = Container(WorkflowStepType.Flowchart, sourcePath, sourceLine);
        configure(new(step, body => new CatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(body, exceptionType)));
        steps.Add(step);
        CaptureFaults(steps[^1]);
        return this;
    }

    /// <summary>Runs a native state machine, retaining the incoming previous value.</summary>
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> StateMachine(
        Action<FaultStateMachineBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var step = Container(WorkflowStepType.StateMachine, sourcePath, sourceLine);
        configure(new(step, new FaultStateBodies<TWorkflowData, TFault, TPreviousStepData>(exceptionType)));
        steps.Add(step);
        CaptureFaults(steps[^1]);
        return this;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> If(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>> condition,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> then,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>>? @else = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).If(_ => false, body => then(Wrap(body)), @else == null ? null : body => @else(Wrap(body)), sourcePath: sourcePath, sourceLine: sourceLine);
        var evaluate = condition.Compile();
        steps[^1].ConditionExpression = condition;
        steps[^1].CompiledCondition = context => evaluate((FaultContext<TWorkflowData, TFault, TPreviousStepData>)context);
        CaptureFaults(steps[^1]);
        return this;
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> DoWhile(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>> conditionExpression, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var result = DoWhile(configure, conditionExpression.Compile(), sourcePath: sourcePath, sourceLine: sourceLine);
        steps[^1].ConditionExpression = conditionExpression;
        return result;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> DoWhile(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool> condition, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).DoWhile(body => configure(Wrap(body)), ctx => condition((FaultContext<TWorkflowData, TFault, TPreviousStepData>)ctx), sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WhileDo(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>> conditionExpression,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var result = WhileDo(conditionExpression.Compile(), configure, sourcePath: sourcePath, sourceLine: sourceLine);
        steps[^1].ConditionExpression = conditionExpression;
        return result;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WhileDo(
        Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool> condition,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).WhileDo(ctx => condition((FaultContext<TWorkflowData, TFault, TPreviousStepData>)ctx), body => configure(Wrap(body)), sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> ForEach<TItem>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, IEnumerable<TItem>?>> itemsExpression,
        Action<IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var result = ForEach(itemsExpression.Compile(), body, setup, sourcePath: sourcePath, sourceLine: sourceLine);
        steps[^1].ForEachItemsExpression = itemsExpression;
        return result;
    }

    public ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> ForEach<TItem>(
        Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, IEnumerable<TItem>?> items,
        Action<IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).ForEach(ctx => items((FaultContext<TWorkflowData, TFault, TPreviousStepData>)ctx), (child, item) => body(new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(((WorkflowBuilder<TWorkflowData, TPreviousStepData>)child).Steps, exceptionType), item), setup, sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return this;
    }

    public IFaultTryBuilder<TWorkflowData, TFault, TPreviousStepData> Try(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).Try(body => configure(Wrap(body)), sourcePath: sourcePath, sourceLine: sourceLine);
        CaptureFaults(steps[^1]);
        return new FaultTryBuilder<TWorkflowData, TFault, TPreviousStepData>(steps, steps[^1], exceptionType);
    }
    internal static void CaptureFaults(WorkflowStep step)
    {
        step.Runtime = step.Runtime.ForFault<TFault>();
        step.LexicalFaultType = typeof(TFault);
        foreach (var child in step.ChildScopes().Where(scope => scope.Kind is not ("catch" or "inline"))
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

using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>Configures child invocation mappings and error policies in a fault body.</summary>
public interface IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious>
    where TChild : class where TPrevious : class
{
    /// <summary>Controls whether the child is cancelled when its parent is cancelled.</summary>
    IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> CancelWithParent(bool cancel);

    /// <summary>Configures an ordered error policy for this step.</summary>
    IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> OnError<TException>(Action<IStepErrorHandler<TException>> configure)
        where TException : Exception;

    /// <summary>Configures an ordered error policy for this step.</summary>
    IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> OnError(Action<IStepErrorHandler> configure);

    /// <summary>Selects an input to bind from the fault context.</summary>
    IFaultInvocationInput<TData, TFault, TChild, TPrevious, TValue> Input<TValue>(Expression<Func<TChild, TValue>> property);

    /// <summary>Selects an output to copy into workflow data.</summary>
    IFaultInvocationOutput<TData, TFault, TChild, TPrevious, TValue> Output<TValue>(Expression<Func<TChild, TValue>> property);
}

/// <summary>Binds a child input from the current fault context.</summary>
public interface
    IFaultInvocationInput<TData, TFault, TChild, TPrevious, TValue> : IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious>
    where TChild : class where TPrevious : class
{
    /// <summary>Configures an ordered error policy for this step.</summary>
    new IFaultInvocationInput<TData, TFault, TChild, TPrevious, TValue> OnError<TException>(Action<IStepErrorHandler<TException>> configure)
        where TException : Exception;

    /// <summary>Configures an ordered error policy for this step.</summary>
    new IFaultInvocationInput<TData, TFault, TChild, TPrevious, TValue> OnError(Action<IStepErrorHandler> configure);

    /// <summary>Binds the selected input and returns the step configuration.</summary>
    IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> From(
        Expression<Func<FaultContext<TData, TFault, TPrevious>, TValue>> source);
}

/// <summary>Maps a child output into workflow data.</summary>
public interface
    IFaultInvocationOutput<TData, TFault, TChild, TPrevious, TValue> : IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious>
    where TChild : class where TPrevious : class
{
    /// <summary>Configures an ordered error policy for this step.</summary>
    new IFaultInvocationOutput<TData, TFault, TChild, TPrevious, TValue> OnError<TException>(
        Action<IStepErrorHandler<TException>> configure)
        where TException : Exception;

    /// <summary>Configures an ordered error policy for this step.</summary>
    new IFaultInvocationOutput<TData, TFault, TChild, TPrevious, TValue> OnError(Action<IStepErrorHandler> configure);

    /// <summary>Maps the selected output and returns the step configuration.</summary>
    IFaultInvocationSetupBuilder<TData, TFault, TChild, TPrevious> To(Expression<Func<WorkflowContext<TData>, TValue>> target);
}

/// <summary>Configures a saga activity, its mappings, policies and compensation in a fault body.</summary>
public interface IFaultSagaActivitySetup<TData, TFault, TActivity, TPrevious>
    where TActivity : class, IAsyncActivity where TPrevious : class
{
    /// <summary>Selects an input to bind from the fault context.</summary>
    IFaultSagaInput<TData, TFault, TActivity, TPrevious, TValue> Input<TValue>(Expression<Func<TActivity, TValue>> property);

    /// <summary>Selects an output to copy into workflow data.</summary>
    IFaultSagaOutput<TData, TFault, TActivity, TPrevious, TValue> Output<TValue>(Expression<Func<TActivity, TValue>> property);

    /// <summary>Configures an ordered error policy for this step.</summary>
    IFaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> OnError<TException>(Action<IStepErrorHandler<TException>> configure)
        where TException : Exception;

    /// <summary>Configures an ordered error policy for this step.</summary>
    IFaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> OnError(Action<IStepErrorHandler> configure);

    /// <summary>Adds a compensation activity for this saga step.</summary>
    IFaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> CompensateWith<TUndo>(
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TUndo : IAsyncActivity;

    /// <summary>Adds a compensation activity for this saga step.</summary>
    IFaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> CompensateWith<TUndo>(
        Action<ICompensationActivityBuilder<TData, TUndo, TActivity>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TUndo : IAsyncActivity;

    /// <summary>Adds a compensation activity for this saga step.</summary>
    [OverloadResolutionPriority(1)]
    IFaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> CompensateWith<TUndo, TChain>(
        Action<ICompensationActivityBuilder<TData, TUndo, TPrevious, TChain>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TUndo : IAsyncActivity where TChain : class, IAsyncActivity;

    /// <summary>Adds a compensation activity for this saga step.</summary>
    IFaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> CompensateWith<TUndo, TReply>(
        Action<IRequestCompensationActivityBuilder<TData, TUndo, TActivity, TReply>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TUndo : IAsyncActivity where TReply : class;
}

/// <summary>Binds a saga activity input from the current fault context.</summary>
public interface IFaultSagaInput<TData, TFault, TActivity, TPrevious, TValue>
    where TActivity : class, IAsyncActivity where TPrevious : class
{
    /// <summary>Binds the selected input and returns the step configuration.</summary>
    IFaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> From(
        Expression<Func<FaultContext<TData, TFault, TPrevious>, TValue>> source);
}

/// <summary>Maps a saga activity output into workflow data.</summary>
public interface IFaultSagaOutput<TData, TFault, TActivity, TPrevious, TValue>
    where TActivity : class, IAsyncActivity where TPrevious : class
{
    /// <summary>Maps the selected output and returns the step configuration.</summary>
    IFaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> To(Expression<Func<WorkflowContext<TData>, TValue>> target);
}
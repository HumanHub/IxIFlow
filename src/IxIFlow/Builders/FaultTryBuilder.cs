using System.Runtime.CompilerServices;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

internal sealed class FaultTryBuilder<TData, TFault, TPrevious>(List<WorkflowStep> steps, WorkflowStep attempt, Type exceptionType)
    : CatchWorkflowBuilder<TData, TFault, TPrevious>(steps, exceptionType), IFaultCatchBuilder<TData, TFault, TPrevious>
    where TData : class where TPrevious : class
{
    public IFaultCatchBuilder<TData, TFault, TPrevious> Catch<TException, TInnerFault>(
        Action<ICatchWorkflowBuilder<TData, TInnerFault, TPrevious>> configure, string? id = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TException : Exception
    {
        new TryBuilder<TData, TPrevious>(Steps, attempt, string.Empty, 1, []).Catch<TException, TInnerFault>(configure, id, sourcePath,
            sourceLine);

        return this;
    }

    public IFaultCatchBuilder<TData, TFault, TPrevious> Catch<TException>(
        Action<ICatchWorkflowBuilder<TData, EmptyFault, TPrevious>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TException : Exception =>
        Catch<TException, EmptyFault>(configure, id, sourcePath, sourceLine);

    public IFaultCatchBuilder<TData, TFault, TPrevious> Catch(Action<ICatchWorkflowBuilder<TData, EmptyFault, TPrevious>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        Catch<Exception, EmptyFault>(configure, id, sourcePath, sourceLine);

    public ICatchWorkflowBuilder<TData, TFault, TPrevious> Finally(Action<ICatchWorkflowBuilder<TData, TFault, TPrevious>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        configure(new CatchWorkflowBuilder<TData, TFault, TPrevious>(attempt.FinallySteps, ExceptionType));

        foreach (var step in attempt.FinallySteps)
            CaptureFaults(step);

        return this;
    }
}
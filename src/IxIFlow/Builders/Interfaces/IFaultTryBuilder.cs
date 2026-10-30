using System.Runtime.CompilerServices;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

public interface IFaultTryBuilder<TData, TFault, TPrevious> where TPrevious : class
{
    IFaultCatchBuilder<TData, TFault, TPrevious> Catch<TException, TInnerFault>(
        Action<ICatchWorkflowBuilder<TData, TInnerFault, TPrevious>> configure, string? id = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TException : Exception;

    IFaultCatchBuilder<TData, TFault, TPrevious> Catch<TException>(
        Action<ICatchWorkflowBuilder<TData, EmptyFault, TPrevious>> configure, string? id = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TException : Exception;

    IFaultCatchBuilder<TData, TFault, TPrevious> Catch(
        Action<ICatchWorkflowBuilder<TData, EmptyFault, TPrevious>> configure, string? id = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ICatchWorkflowBuilder<TData, TFault, TPrevious> Finally(Action<ICatchWorkflowBuilder<TData, TFault, TPrevious>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);
}

public interface IFaultCatchBuilder<TData, TFault, TPrevious> : IFaultTryBuilder<TData, TFault, TPrevious>,
    ICatchWorkflowBuilder<TData, TFault, TPrevious> where TPrevious : class;
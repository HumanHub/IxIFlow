using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using IxIFlow.Core;
namespace IxIFlow.Builders.Interfaces;
public interface IFaultSagaActivityBuilder<TData, TFault, TPrevious> where TPrevious : class
{
    IFaultSagaActivityBuilder<TData, TFault, TPrevious> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);
    IFaultSagaActivityBuilder<TData, TFault, TActivity> Step<TActivity>(Action<FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious>>? configure = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TActivity : class, IAsyncActivity;
    IFaultSagaActivityBuilder<TData, TFault, TActivity> Request<TActivity>(string name, Action<FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious>>? configure = null,
        Expression<Func<FaultContext<TData, TFault, TPrevious>, string>>? subjectExpression = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TActivity : class, IAsyncActivity;
    IFaultSagaActivityBuilder<TData, TFault, TEvent> WaitFor<TEvent>(string key,
        Expression<Func<TEvent, FaultContext<TData, TFault, TPrevious>, bool>>? matchesExpression = null,
        Action<IFaultSuspendSetupBuilder<TData, TFault, TEvent, TPrevious>>? configure = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TEvent : class;
    IFaultSagaActivityBuilder<TData, TFault, TEvent> Suspend<TEvent>(string suspendReason,
        Expression<Func<TEvent, FaultContext<TData, TFault, TPrevious>, bool>>? resumeExpression = null,
        Action<IFaultSuspendSetupBuilder<TData, TFault, TEvent, TPrevious>>? configure = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TEvent : class;
    IFaultSagaActivityBuilder<TData, TFault, TPrevious> Switch<TValue>(Expression<Func<FaultContext<TData, TFault, TPrevious>, TValue>> selector,
        Action<SwitchCases<TValue, IFaultSagaActivityBuilder<TData, TFault, TPrevious>>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);
}
public interface IFaultSagaContainerBuilder<TData, TFault, TPrevious> : ICatchWorkflowBuilder<TData, TFault, TPrevious> where TPrevious : class
{
    IFaultSagaContainerBuilder<TData, TFault, TPrevious> OnError<TException, TInnerFault>(Action<ISagaErrorBuilder<TData, TInnerFault, TPrevious>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TException : Exception;
    IFaultSagaContainerBuilder<TData, TFault, TPrevious> OnError<TException>(Action<ISagaErrorBuilder<TData, EmptyFault, TPrevious>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TException : Exception;
    IFaultSagaContainerBuilder<TData, TFault, TPrevious> OnError(Action<ISagaErrorBuilder<TData, EmptyFault, TPrevious>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);
}

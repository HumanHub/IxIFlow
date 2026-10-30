using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces
{
    public interface IScopeSetupBuilder<TData, TScope, TPrevious> : IActivitySetupBuilder<TData, TScope, TPrevious>
        where TScope : class, IScopeActivity where TPrevious : class
    {
        IScopeSetupBuilder<TData, TScope, TPrevious> Retry(RetryPolicy policy);
        IScopeSetupBuilder<TData, TScope, TPrevious> Threading(ScopeThreading mode);
    }
}

namespace IxIFlow.Builders
{
    public static class ScopeSetupExtensions
    {
        public static IScopeSetupBuilder<TData, TScope, TPrevious> Retry<TData, TScope, TPrevious>(
            this IActivitySetupBuilder<TData, TScope, TPrevious> builder, RetryPolicy policy)
            where TScope : class, IScopeActivity where TPrevious : class =>
            new ScopeSetupBuilder<TData, TScope, TPrevious>(((ActivitySetupBuilder<TData, TScope, TPrevious>)builder).Step).Retry(policy);

        public static IScopeSetupBuilder<TData, TScope, TPrevious> Threading<TData, TScope, TPrevious>(
            this IActivitySetupBuilder<TData, TScope, TPrevious> builder, ScopeThreading mode)
            where TScope : class, IScopeActivity where TPrevious : class =>
            new ScopeSetupBuilder<TData, TScope, TPrevious>(((ActivitySetupBuilder<TData, TScope, TPrevious>)builder).Step).Threading(mode);
    }

    internal sealed class ScopeSetupBuilder<TData, TScope, TPrevious>
        : ActivitySetupBuilder<TData, TScope, TPrevious>, IScopeSetupBuilder<TData, TScope, TPrevious>
        where TScope : class, IScopeActivity where TPrevious : class
    {
        private readonly WorkflowStep step;

        internal ScopeSetupBuilder(WorkflowStep step) : base(step)
        {
            this.step = step;
        }

        public IScopeSetupBuilder<TData, TScope, TPrevious> Retry(RetryPolicy policy)
        {
            step.ScopeRetryPolicy = policy ?? throw new ArgumentNullException(nameof(policy));

            return this;
        }

        public IScopeSetupBuilder<TData, TScope, TPrevious> Threading(ScopeThreading mode)
        {
            if (!Enum.IsDefined(mode) || mode < Core.Runtime.ActivityTypes.Threading(typeof(TScope)))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    "Scope threading cannot weaken the implementation requirement");

            step.ScopeThreading = mode;

            return this;
        }
    }
}

namespace IxIFlow.Core.Runtime
{
    internal static class ScopeConstruction
    {
        public static WorkflowStep Create<TData, TPrevious, TScope>(int order, string path, int line)
            where TPrevious : class where TScope : class, IScopeActivity => new()
        {
            StepType = WorkflowStepType.Scope, Name = typeof(TScope).Name, Order = order,
            ActivityType = typeof(TScope), ScopeValueType = ActivityTypes.ValidateScope(typeof(TScope)),
            ScopeConcurrency = ActivityTypes.Concurrency(typeof(TScope)), ScopeThreading = ActivityTypes.Threading(typeof(TScope)),
            WorkflowDataType = typeof(TData), PreviousStepDataType = typeof(TPrevious),
            Runtime = WorkflowStepDelegates.Create<TData, TPrevious>(), SourceLocation = path + ":" + line
        };
    }
}
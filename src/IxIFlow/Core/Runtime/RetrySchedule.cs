using IxIFlow.Builders;
using IxIFlow.Authoring;

namespace IxIFlow.Core.Runtime;

internal static class RetrySchedule
{
    public static DateTime? NextAttemptUtc(RetryPolicy? policy, int attempt, DateTime now)
    {
        if (policy == null) return null;
        var problem = RetryDelayRules.Validate(policy.MaximumAttempts, policy.InitialInterval,
            policy.MaximumInterval, policy.BackoffCoefficient);
        if (problem != null) throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, problem);
        var delay = RetryDelayRules.Delay(policy.InitialInterval, policy.MaximumInterval, policy.BackoffCoefficient, attempt);
        return delay == TimeSpan.Zero ? null : now + delay;
    }
}

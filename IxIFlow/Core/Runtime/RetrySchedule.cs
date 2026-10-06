using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

internal static class RetrySchedule
{
    public static DateTime? NextAttemptUtc(RetryPolicy? policy, int attempt)
    {
        if (policy == null || policy.InitialInterval == TimeSpan.Zero)
            return null;
        if (policy.InitialInterval < TimeSpan.Zero || policy.MaximumInterval < TimeSpan.Zero ||
            !double.IsFinite(policy.BackoffCoefficient) || policy.BackoffCoefficient < 1)
            throw new InvalidOperationException("Retry intervals and backoff must be nonnegative and finite");

        var maximum = policy.MaximumInterval == TimeSpan.Zero
            ? policy.InitialInterval
            : policy.MaximumInterval;
        var milliseconds = Math.Min(maximum.TotalMilliseconds,
            policy.InitialInterval.TotalMilliseconds *
            Math.Pow(policy.BackoffCoefficient, Math.Max(0, attempt - 1)));
        return DateTime.UtcNow + TimeSpan.FromMilliseconds(milliseconds);
    }
}

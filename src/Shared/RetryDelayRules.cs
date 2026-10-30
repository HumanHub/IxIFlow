using System;

namespace IxIFlow.Authoring;

// Linked into the engine and document compiler: authoring and parking use one range rule.
internal static class RetryDelayRules
{
    internal const int MaximumDurationDays = 36500;
    internal static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(MaximumDurationDays);

    internal static string? Validate(int retries, TimeSpan initial, TimeSpan maximum, double coefficient)
    {
        if (retries < 0)
            return "The additional retry count must be nonnegative";

        if (initial < TimeSpan.Zero || maximum < TimeSpan.Zero)
            return "Retry intervals must be nonnegative";

        if (double.IsNaN(coefficient) || double.IsInfinity(coefficient) || coefficient < 1)
            return "Retry backoff must be finite and at least one";

        if (retries > 0 && Delay(initial, maximum, coefficient, retries) > MaximumDuration)
            return "A reachable retry delay exceeds the maximum parking duration of 36,500 days";

        return null;
    }

    internal static TimeSpan Delay(TimeSpan initial, TimeSpan maximum, double coefficient, int ordinal)
    {
        if (initial == TimeSpan.Zero)
            return TimeSpan.Zero;

        var cap = maximum == TimeSpan.Zero ? initial : maximum;

        if (ordinal <= 1 || coefficient == 1 || initial >= cap)
            return initial < cap ? initial : cap;

        // Monotone capped arithmetic avoids both a budget-sized loop and overflowing an intermediate duration.
        var ticks = Math.Min((double)cap.Ticks,
            initial.Ticks * Math.Pow(coefficient, Math.Max(0, ordinal - 1)));

        if (ticks >= cap.Ticks)
            return cap;

        return TimeSpan.FromTicks((long)ticks);
    }
}
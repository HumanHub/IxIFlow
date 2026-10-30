namespace IxIFlow.Core;

internal static class TimerIntervals
{
    internal static readonly TimeSpan Maximum = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    internal static void Validate(TimeSpan value, string name, bool allowZero = false)
    {
        if (value < (allowZero ? TimeSpan.Zero : TimeSpan.FromMilliseconds(1)) || value > Maximum)
            throw new ArgumentOutOfRangeException(name, value,
                $"{name} must be between {(allowZero ? 0 : 1)} and {uint.MaxValue - 1} milliseconds.");
    }
}
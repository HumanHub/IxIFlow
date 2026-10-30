using System.Text.Json;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>Bounded test polling with the instance, expectation and last observed state in a failure.</summary>
public static class TestEventually
{
    public static async Task<T> UntilAsync<T>(
        string instanceId,
        string expectation,
        Func<Task<T>> read,
        Func<T, bool> reached,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        var started = time.GetTimestamp();
        var patience = timeout ?? TimeSpan.FromSeconds(10);
        T? last = default;

        while (time.GetElapsedTime(started) < patience)
        {
            var remaining = patience - time.GetElapsedTime(started);

            if (remaining <= TimeSpan.Zero)
                break;

            try
            {
                last = await read().WaitAsync(remaining, time);
            }
            catch (TimeoutException)
            {
                break;
            }

            if (reached(last))
                return last;

            await Task.Delay(TimeSpan.FromMilliseconds(10), time);
        }

        throw new TimeoutException(
            $"Instance '{instanceId}': expected {expectation}; last seen {JsonSerializer.Serialize(last)} after {patience}");
    }
}
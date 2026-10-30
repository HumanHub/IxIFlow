using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
public sealed class Slice4PollingTests
{
    [Fact]
    public async Task APollingDeadlineNamesTheInstanceExpectationAndLastStateEvenWhenAReadHangs()
    {
        var time = new FakeTimeProvider();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;

        var polling = TestEventually.UntilAsync(
            "polling-instance", "Completed", async () =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                    return "Running";

                reading.TrySetResult();

                return await blocked.Task;
            }, state => state == "Completed", TimeSpan.FromSeconds(1), time);

        try
        {
            time.Advance(TimeSpan.FromMilliseconds(10));
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(1));
            var error = await Assert.ThrowsAsync<TimeoutException>(() => polling.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("polling-instance", error.Message);
            Assert.Contains("Completed", error.Message);
            Assert.Contains("Running", error.Message);
        }
        finally
        {
            blocked.TrySetResult("Completed");
        }
    }
}
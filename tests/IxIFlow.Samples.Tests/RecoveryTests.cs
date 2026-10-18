using IxIFlow.Core;
using IxIFlow.Samples;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Samples.Tests;

public sealed class RecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReservationRunsOrRecoversWithoutRepeatingItsEffect(bool interrupted, bool needsOperator)
    {
        var time = new FakeTimeProvider();
        var reservations = new Reservations { Found = !needsOperator };
        var store = new FaultInjectingRepository(time);
        var collection = Hosting.Services();
        collection.AddSingleton<TimeProvider>(time);
        collection.AddSingleton<IWorkflowStateRepository>(store);
        collection.AddSingleton<IQuoteReservations>(reservations);
        collection.AddTransient<ReserveQuote>();
        await using var services = collection.BuildServiceProvider();
        var client = SampleTests.Client(services);
        var instanceId = Guid.NewGuid().ToString("N");
        WorkflowExecutionResult result;
        if (interrupted)
        {
            // The provider accepted the effect, but the engine could not commit its end.
            store.FailCommit = _ => reservations.Calls != 0;
            await Assert.ThrowsAnyAsync<Exception>(() => client.RunAsync(Recovery.Definition(), new ReservationData(),
                new WorkflowOptions { InstanceId = instanceId }));
            store.FailCommit = _ => false;
            result = await client.RecoverAsync(instanceId);
            if (needsOperator)
            {
                Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
                result = await Recovery.ConfirmAsync(client, instanceId, "reservation-1");
            }
        }
        else
            result = await client.RunAsync(Recovery.Definition(), new ReservationData());
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("reservation-1", Assert.IsType<ReservationData>(result.WorkflowData).Reference);
        Assert.Equal(1, reservations.Calls);
        if (interrupted)
            Assert.Equal(reservations.InvocationId, reservations.LookedUp);
    }

    internal sealed class Reservations : IQuoteReservations
    {
        public bool Found { get; init; }
        public int Calls { get; private set; }
        public string? InvocationId { get; private set; }
        public string? LookedUp { get; private set; }

        public Task<string> ReserveAsync(string invocationId, CancellationToken cancellationToken)
        {
            Calls++;
            InvocationId = invocationId;
            return Task.FromResult("reservation-1");
        }

        public Task<string?> FindAsync(string invocationId, CancellationToken cancellationToken)
        {
            LookedUp = invocationId;
            return Task.FromResult(Found ? "reservation-1" : null);
        }
    }
}

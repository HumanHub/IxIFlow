using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class DebugPayloadFixTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RemoteControlsDrainTheActivityAndReleaseThePausedLease(bool sqlite, bool breakpoint)
    {
        var probe = new BoundaryProbe();
        ControlProbeStore watched = null!;
        await using var h = new Harness(sqlite, s => watched = new(s), s => s.AddSingleton(probe));
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddSingleton<TimeProvider>(h.Clock);
        registrations.AddSingleton(h.Store);
        registrations.AddIxIFlow(o => o.Debugging = true);
        registrations.AddSingleton(h.Services.GetRequiredService<IWorkflowCatalog>());
        await using var writer = registrations.BuildServiceProvider();
        var operations = writer.GetRequiredService<IWorkflowOperations>();
        var definition = Workflow.Create<Data>("remote-control-boundary")
            .Step<BoundaryBlock>(s => s.Output(a => a.Text).To(c => c.WorkflowData.Text))
            .Step<BoundaryAfter>().WithId("after").Build();
        var id = Guid.NewGuid().ToString("N");
        var run = h.Client.RunAsync(definition, new Data(), new() { InstanceId = id });
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (breakpoint) await operations.SetBreakpointsAsync(id, [new("remote", "after")], "writer");
            else await operations.PauseAsync(id, "writer");
            await DriveWatchUntil(() => Task.FromResult(watched.Observed.Task.IsCompleted));
            if (breakpoint)
                await DriveWatchUntil(async () => (await h.Store.GetDebugStatesAsync([id]))[id]
                    .Commands.All(c => c.AcknowledgedRevision != null));
            Assert.False(run.IsCompleted);
            probe.Finish.TrySetResult();
            var paused = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
            Assert.Equal("committed", Assert.IsType<Data>(paused.WorkflowData).Text);
            Assert.Equal(0, probe.After);
            Assert.NotNull(await h.Store.TryAcquireExecutionLeaseAsync(id, "inspect", TimeSpan.FromSeconds(10)));
            await h.Store.ReleaseExecutionLeaseAsync(id, "inspect");
            Assert.Equal(WorkflowExecutionStatus.Success, (await h.Services.GetRequiredService<IWorkflowOperations>()
                .ContinueAsync(id, "writer")).Status);
            Assert.Equal(1, probe.After);
        }
        finally { probe.Finish.TrySetResult(); }

        async Task DriveWatchUntil(Func<Task<bool>> condition)
        {
            var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!await condition())
            {
                Assert.True(DateTime.UtcNow < end, "The running owner did not observe its remote control");
                h.Clock.Advance(TimeSpan.FromMilliseconds(250));
                await Task.Delay(10);
            }
        }
    }

    private sealed class ControlProbeStore(IWorkflowStateRepository inner) : FaultInjectingRepository(inner)
    {
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<WorkflowControlRequests> GetControlRequestsAsync(IReadOnlyCollection<string> ids)
        {
            var controls = await base.GetControlRequestsAsync(ids);
            if (controls.DebugStates.Values.Any(s => s.Commands.Any(c => c.AcknowledgedRevision == null)))
                Observed.TrySetResult();
            return controls;
        }
    }
    public sealed class BoundaryProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int After;
    }
    public sealed class BoundaryBlock(BoundaryProbe probe) : IAsyncActivity
    {
        [ActivityOutput] public string Text { get; set; } = "";
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { probe.Started.TrySetResult(); await probe.Finish.Task.WaitAsync(cancellationToken); Text = "committed"; }
    }
    public sealed class BoundaryAfter(BoundaryProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref probe.After); return Task.CompletedTask; }
    }
}

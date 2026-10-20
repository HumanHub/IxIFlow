using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class DebugPayloadFixTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADebugDisabledOwnerAppliesRemoteBreakpointsAndPause(bool sqlite)
    {
        var probe = new BoundaryProbe();
        await using var h = MixedFleetOwner(sqlite, services => services.AddSingleton(probe));
        await using var writer = DebugWriter(h);
        var host = writer.GetRequiredService<WorkflowHost>();
        var id = Guid.NewGuid().ToString("N");
        var run = h.Client.RunAsync(Workflow.Create<Data>("mixed-running")
            .Step<BoundaryBlock>().Step<BoundaryAfter>().WithId("after").Build(), new Data(),
            new() { InstanceId = id });
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await host.ReadAsync(client => client.SetBreakpointsAsync(id, [new("remote", "after")], "writer", default));
            await DriveControlsUntil(h, async () => (await h.Store.GetDebugStatesAsync([id]))[id]
                .Commands.All(c => c.AcknowledgedRevision != null));
            var saved = (await h.Store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal("remote", Assert.Single(ExecutionCheckpoint.ReadTrunk(saved.ExecutionStateJson).Debug.Breakpoints).Id);
            Assert.False(run.IsCompleted);
            await SendPause(host, id);
            await DriveControlsUntil(h, () => Task.FromResult(((MixedFleetControlStore)h.Store).PauseObserved));
            probe.Finish.TrySetResult();
            Assert.Equal(WorkflowExecutionStatus.Paused, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Status);
            Assert.Equal(0, probe.After);
            Assert.All((await h.Store.GetDebugStatesAsync([id]))[id].Commands,
                command => Assert.NotNull(command.AcknowledgedRevision));
        }
        finally { probe.Finish.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADebugDisabledOwnerRecoversASuspendedInstanceWithPendingPause(bool sqlite)
    {
        await using var h = MixedFleetOwner(sqlite);
        var started = await h.Client.RunAsync(Workflow.Create<Data>("mixed-suspended")
            .Step<Noop>().WithId("before").WaitFor<Go>("go").Build(), new Data());
        await using var writer = DebugWriter(h);
        // Keep the idle command pending, as when the requesting host cannot take ownership.
        Assert.NotNull(await h.Store.TryAcquireExecutionLeaseAsync(started.InstanceId, "busy", TimeSpan.FromSeconds(10)));
        await SendPause(writer.GetRequiredService<WorkflowHost>(), started.InstanceId);
        await h.Store.ReleaseExecutionLeaseAsync(started.InstanceId, "busy");
        Assert.Contains(await h.Store.GetRunnableInstancesAsync(10), row => row.InstanceId == started.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await h.Client.RecoverAsync(started.InstanceId)).Status);
        Assert.DoesNotContain(await h.Store.GetRunnableInstancesAsync(10), row => row.InstanceId == started.InstanceId);
        Assert.NotNull(Assert.Single((await h.Store.GetDebugStatesAsync([started.InstanceId]))[started.InstanceId]
            .Commands).AcknowledgedRevision);
        await writer.GetRequiredService<WorkflowHost>().ReadAsync(client => client.SetBreakpointsAsync(
            started.InstanceId, [new("paused-remote", "before")], "writer", default));
        Assert.Equal(WorkflowExecutionStatus.Paused, (await h.Client.RecoverAsync(started.InstanceId)).Status);
        var paused = (await h.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal("paused-remote", Assert.Single(ExecutionCheckpoint.ReadTrunk(paused.ExecutionStateJson).Debug.Breakpoints).Id);
        Assert.All((await h.Store.GetDebugStatesAsync([started.InstanceId]))[started.InstanceId].Commands,
            command => Assert.NotNull(command.AcknowledgedRevision));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADebugDisabledOwnerKeepsAPendingPauseAfterACrash(bool sqlite)
    {
        await using var h = MixedFleetOwner(sqlite);
        var started = await h.Client.RunAsync(Workflow.Create<Data>("mixed-crashed")
            .Step<Noop>().WaitFor<Go>("go").Build(), new Data());
        var saved = (await h.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        var lease = (await h.Store.TryAcquireExecutionLeaseAsync(started.InstanceId, "crashed", TimeSpan.FromSeconds(1)))!;
        saved.Status = WorkflowStatus.Running;
        Assert.Equal(WorkflowCommitStatus.Applied, (await h.Store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(saved, lease.Epoch, saved.Revision))).Status);
        await using var writer = DebugWriter(h);
        await SendPause(writer.GetRequiredService<WorkflowHost>(), started.InstanceId);
        // An intermediate commit must retain the command until the executor applies the pause.
        saved = (await h.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal(WorkflowCommitStatus.Applied, (await h.Store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(saved, lease.Epoch, saved.Revision))).Status);
        Assert.Null(Assert.Single((await h.Store.GetDebugStatesAsync([started.InstanceId]))[started.InstanceId]
            .Commands).AcknowledgedRevision);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(WorkflowExecutionStatus.Paused, (await h.Client.RecoverAsync(started.InstanceId)).Status);
        Assert.NotNull(Assert.Single((await h.Store.GetDebugStatesAsync([started.InstanceId]))[started.InstanceId]
            .Commands).AcknowledgedRevision);
    }

    private static Harness MixedFleetOwner(bool sqlite, Action<IServiceCollection>? configure = null) =>
        new(sqlite, store => new MixedFleetControlStore(store), configureServices: services =>
        {
            services.AddSingleton(HostSettings.Default with { Debugging = false });
            configure?.Invoke(services);
        });

    private static ServiceProvider DebugWriter(Harness h)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(h.Clock);
        services.AddSingleton(h.Store);
        services.AddIxIFlow(options => options.Debugging = true);
        services.AddSingleton(h.Services.GetRequiredService<IWorkflowCatalog>());
        return services.BuildServiceProvider();
    }

    private static Task<WorkflowExecutionResult> SendPause(WorkflowHost host, string id) =>
        host.ReadAsync(client => client.RequestDebugAsync(id, DebugCommandKind.Pause, "writer", default));

    private sealed class MixedFleetControlStore(IWorkflowStateRepository inner) : FaultInjectingRepository(inner)
    {
        private int _pauseObserved;
        public bool PauseObserved => Volatile.Read(ref _pauseObserved) >= 2;
        public override async Task<WorkflowControlRequests> GetControlRequestsAsync(IReadOnlyCollection<string> ids)
        {
            var controls = await base.GetControlRequestsAsync(ids);
            if (controls.DebugStates.Values.Any(state => state.Commands.Any(command =>
                command.Kind == DebugCommandKind.Pause && command.AcknowledgedRevision == null)))
                Interlocked.Increment(ref _pauseObserved);
            return controls;
        }
    }

    private static async Task DriveControlsUntil(Harness h, Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The debug-disabled owner did not apply its remote control");
            h.Clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(10);
        }
    }
}

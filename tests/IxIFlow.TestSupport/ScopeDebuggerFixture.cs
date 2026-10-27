using System.Collections.Concurrent;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IxIFlow.Tests.Infrastructure;

public static class ScopeDebuggerFixture
{
    public static ServiceProvider Services(IWorkflowStateRepository repository, WorkflowCatalog catalog, Probe probe, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(repository); services.AddSingleton<IWorkflowCatalog>(catalog); services.AddSingleton(probe);
        services.AddIxIFlow(o => { o.Debugging = true; o.MaxActivitiesPerRun = 1; o.VerifyCheckpointParts = true; });
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
    public static WorkflowDefinition Definition() => new WorkflowBuilder<Data, object>([], "AffineDebugScope")
        .WaitFor<Signal>("start")
        .Scope<Container>(s => s.Input(a => a.Label).From(c => "outer"), b => b
            .Sequence(n => n.Step<Read>().WithId("read").Step<Read>()).WithId("nested")
            .Step<Read>().WithId("still-inside"))
        .WithId("container").Named("Connection session")
        .Step<Noop>().WithId("after").Build();

    public static async Task IntoOverOut(IWorkflowStateRepository repository, WorkflowCatalog catalog, Probe probe,
        Action<IServiceCollection>? configure = null, Func<ServiceProvider, Task>? start = null)
    {
        await using var host = Services(repository, catalog, probe, configure);
        if (start != null) await start(host);
        var client = host.GetRequiredService<IWorkflowClient>(); var ops = host.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(Definition(), new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("container", "container")], "setup");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "start", new Signal())).Status);
        await ops.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        var thread = Assert.Single((await ops.InspectAsync(idle.InstanceId))!.Threads).ContinuationId;
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(idle.InstanceId, thread, DebugStepKind.Into, "into")).Status);
        var inspection = (await ops.InspectAsync(idle.InstanceId))!;
        Assert.Equal("nested", Assert.Single(inspection.Threads).AuthorStepId);
        Assert.Equal("Connection session", Assert.Single(inspection.Threads.Single().Frames, f => f.Kind == "Scope").Name);
        Assert.Equal(ScopeCloseReason.Suspended, probe.Closes.Last());
        // Enter the sequence, then Out must leave the resource rather than merely that sequence.
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(idle.InstanceId, thread, DebugStepKind.Into, "into-body")).Status);
        Assert.Equal("read", Assert.Single((await ops.InspectAsync(idle.InstanceId))!.Threads).AuthorStepId);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(idle.InstanceId, thread, DebugStepKind.Out, "out")).Status);
        Assert.Equal("after", Assert.Single((await ops.InspectAsync(idle.InstanceId))!.Threads).AuthorStepId);
        Assert.Equal(3, probe.Reads); Assert.Equal(ScopeCloseReason.Completed, probe.Closes.Last());
        Assert.Equal(2, probe.Threads.Distinct().Count()); // one thread before pause, a fresh one for re-open
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "finish")).Status);
        await host.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);

        probe = new();
        await using var overHost = Services(repository, catalog, probe, configure);
        if (start != null) await start(overHost);
        client = overHost.GetRequiredService<IWorkflowClient>(); ops = overHost.GetRequiredService<IWorkflowOperations>();
        idle = await client.RunAsync(Definition(), new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("container", "container")], "setup");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "start", new Signal())).Status);
        await ops.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        thread = Assert.Single((await ops.InspectAsync(idle.InstanceId))!.Threads).ContinuationId;
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(idle.InstanceId, thread, DebugStepKind.Over, "over")).Status);
        Assert.Equal("after", Assert.Single((await ops.InspectAsync(idle.InstanceId))!.Threads).AuthorStepId);
        Assert.Equal(3, probe.Reads); Assert.Single(probe.Threads.Distinct());
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "finish")).Status);
    }

    public sealed class Data;
    public sealed class Signal;
    public sealed class Probe
    {
        // Thread objects, not managed IDs: a finished thread's ID can be reused by the next one.
        public ConcurrentQueue<Thread> Threads { get; } = new();
        public ConcurrentQueue<ScopeCloseReason> Closes { get; } = new();
        public ConcurrentQueue<string> Labels { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads;
    }
    public sealed class Resource(string label) : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        public bool Disposed;
        public string Read() { Assert.False(Disposed); Assert.Equal(_thread, Environment.CurrentManagedThreadId); return label; }
        public void Dispose() { Read(); Disposed = true; }
    }
    [ScopeThreading(ScopeThreading.ThreadAffine)]
    public sealed class Container(Probe probe) : IScopeActivity<Resource>
    {
        public string Label { get; set; } = "";
        [Sensitive] public string Secret { get; set; } = "scope-secret";
        public string Opened { get; set; } = "";
        public Task<Resource> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken = default)
        { probe.Threads.Enqueue(Thread.CurrentThread); Opened = Label; return Task.FromResult(new Resource(Label)); }
        public Task CloseAsync(Resource value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken = default)
        { value.Read(); value.Dispose(); probe.Closes.Enqueue(closing.Reason); return Task.CompletedTask; }
    }
    [RequiresScope<Resource>]
    public sealed class Read(Probe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { context.Scope<Resource>().Read(); await Task.Yield(); probe.Labels.Enqueue(context.Scope<Resource>().Read()); Interlocked.Increment(ref probe.Reads); }
    }
    [RequiresScope<Resource>]
    public sealed class Block(Probe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            var value = context.Scope<Resource>(); value.Read(); probe.Started.TrySetResult();
            await probe.Resume.Task.WaitAsync(cancellationToken);
            Assert.Same(value, context.Scope<Resource>()); value.Read(); Interlocked.Increment(ref probe.Reads);
        }
    }
    public sealed class Noop : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }

    [Workflow("InlineAffineScope")]
    public sealed class InlineScope : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> builder) => builder.Step<Noop>()
            .Scope<Container>(null, body => body.Sequence(n => n.Step<Read>().WithId("read"))
                .Step<Read>().WithId("still-inside"))
            .Step<Noop>().WithId("callee-after");
    }
}

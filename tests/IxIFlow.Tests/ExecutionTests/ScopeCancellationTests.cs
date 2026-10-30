using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeCancellationTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task LiveCancellationRunsInnerFinallyThenCancelledCloseThenOutsideFinally(bool affine)
    {
        var events = new ScopeActivityTests.Probe();
        var probe = new Probe();
        var collection = new ServiceCollection();
        collection.AddSingleton(events);
        collection.AddSingleton(probe);
        collection.AddIxIFlow();
        await using var services = collection.BuildServiceProvider();

        var definition = ScopeActivityTests.Start("ScopeLiveCancellation").Try(outside => outside.Scope<ScopeActivityTests.TestScope>(
                s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread),
                body => body.Try(x => x.Step<Blocking>()).Finally(x => x.Step<ScopeActivityTests.Read>())))
            .Finally(outside => outside.Step<ScopeActivityTests.Mark>(s => s.Input(x => x.Text).From(c => "outside"))).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var id = Guid.NewGuid().ToString("N");
        var run = client.RunAsync(definition, new ScopeActivityTests.Data(), new() { InstanceId = id });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancelled = await client.CancelAsync(id, new CancellationReason { ReasonCode = "test" }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Running, cancelled.Status);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Equal(new[] { "read:opened", "close:Cancelled", "outside" }, events.Events.Skip(1));
        Assert.Single(events.Invocations);
        Assert.True(probe.CallbackRan);

        if (affine)
            Assert.Equal(probe.BodyThread, probe.CallbackThread);
    }

    public sealed class Probe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BodyThread;
        public int CallbackThread;
        public bool CallbackRan;
    }

    public sealed class Blocking(Probe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            probe.BodyThread = Environment.CurrentManagedThreadId;
            var scheduler = SynchronizationContext.Current;
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var registration = cancellationToken.Register(() =>
            {
                void CancelResource()
                {
                    _ = context.Scope<ScopeActivityTests.Resource>();
                    probe.CallbackThread = Environment.CurrentManagedThreadId;
                    probe.CallbackRan = true;
                    cancelled.TrySetResult();
                }

                if (scheduler != null)
                    scheduler.Post(_ => CancelResource(), null);
                else
                    CancelResource();
            });

            probe.Started.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                    await cancelled.Task;
            }
        }
    }
}
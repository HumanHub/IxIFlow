using System.Collections.Concurrent;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeLifetimeTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task BlockingSynchronousCloseHasABoundedEngineWaitAndReleasesOnlyAfterItEnds(bool affine)
    {
        var probe = new Probe { BlockClose = true };
        await using var services = Services(probe);

        try
        {
            var definition = new WorkflowBuilder<Data, object>([], "ScopeBlockedClose").Try(b => b.Scope<Container>(
                    s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread), x => x.Step<Noop>()))
                .Catch<TimeoutException>(b => b.Step<MarkExecution>()).Build();

            var run = services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            await probe.Closing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
            Assert.Contains("Scope close exceeded", result.ErrorMessage);
            Assert.Equal(0, probe.Executions);
            Assert.False(probe.Disposed.Task.IsCompleted);

            if (affine)
                Assert.Equal(1, services.GetRequiredService<ScopeThreadRegistry>().Count);
        }
        finally
        {
            probe.Gate.Set();
        }

        await probe.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, probe.Disposals);

        if (affine)
            Assert.Single(probe.Threads.Distinct());
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task BlockingDisposeNeverBlocksTheExecutorOrDisposesTwice(bool affine)
    {
        var probe = new Probe { FailClose = true, BlockDispose = true };
        await using var services = Services(probe);

        try
        {
            var definition = new WorkflowBuilder<Data, object>([], "ScopeBlockedDispose").Try(b => b.Scope<Container>(
                    s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread), x => x.Step<Noop>()))
                .Catch<InvalidOperationException>(_ =>
                {
                }).Build();

            var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data())
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
            Assert.False(probe.Disposed.Task.IsCompleted);
        }
        finally
        {
            probe.Gate.Set();
        }

        await probe.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, probe.Disposals);

        if (affine)
            Assert.Single(probe.Threads.Distinct());
    }

    [Fact]
    public async Task QuarantinedThreadKeepsItsHostSlotUntilTheActualCloseAndReleaseEnd()
    {
        var probe = new Probe { BlockClose = true };
        await using var services = Services(probe, maximumThreads: 1);

        try
        {
            WorkflowDefinition Definition(string name) => new WorkflowBuilder<Data, object>([], name).Try(b => b.Scope<Container>(
                    s => s.Threading(ScopeThreading.ThreadAffine), x => x.Step<Noop>()))
                .Catch<TimeoutException>(b => b.Step<MarkExecution>())
                .Build();

            var client = services.GetRequiredService<IWorkflowClient>();
            var failed = await client.RunAsync(Definition("ScopeQuarantined"), new Data());
            Assert.Equal(WorkflowExecutionStatus.Faulted, failed.Status);
            Assert.Contains("Scope close exceeded", failed.ErrorMessage);
            Assert.Equal(0, probe.Executions);
            var stopped = await client.RunAsync(Definition("ScopeThreadLimit"), new Data());
            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
            Assert.Equal(1, services.GetRequiredService<ScopeThreadRegistry>().Count);
        }
        finally
        {
            probe.Gate.Set();
        }

        await probe.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task CancelledRunRetainsAValueUntilItsSynchronousRecoveryCaptureActuallyEnds(bool affine)
    {
        var probe = new Probe { BlockCapture = true };
        await using var services = Services(probe);
        using var cancellation = new CancellationTokenSource();

        try
        {
            var definition = new WorkflowBuilder<Data, object>([], "ScopeBlockedCapture").Scope<Container>(
                s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread), b => b.Step<Recoverable>()).Build();

            var run = services.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new Data(), cancellationToken: cancellation.Token);

            await probe.Capturing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.False(probe.Disposed.Task.IsCompleted);
            Assert.Equal(0, probe.Executions);
        }
        finally
        {
            probe.Gate.Set();
        }

        await probe.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, probe.Disposals);

        if (affine)
            Assert.Single(probe.Threads.Distinct());
    }

    [Fact]
    public async Task LateOpenAfterRunCancellationIsReleasedOnItsOriginalThreadWithoutClose()
    {
        var probe = new Probe { BlockOpen = true };
        await using var services = Services(probe);
        using var cancellation = new CancellationTokenSource();

        try
        {
            var definition = new WorkflowBuilder<Data, object>([], "ScopeLateOpen").Scope<Container>(
                s => s.Threading(ScopeThreading.ThreadAffine), b => b.Step<Noop>()).Build();

            var run = services.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new Data(), cancellationToken: cancellation.Token);

            await probe.Opening.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.False(probe.Disposed.Task.IsCompleted);
        }
        finally
        {
            probe.Gate.Set();
        }

        await probe.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(probe.Closing.Task.IsCompleted);
        Assert.Single(probe.Threads.Distinct());
        Assert.Equal(1, probe.Disposals);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task FailedOpenSettlementRetainsItsAdmissionThroughTimedOutCleanup(bool affine)
    {
        var probe = new Probe { BlockClose = true };
        await using var services = Services(probe, cap: 1);
        var client = services.GetRequiredService<IWorkflowClient>();
        string id;

        try
        {
            var definition = new WorkflowBuilder<Data, object>([], "ScopeFailedSettlement" + affine)
                .Try(b => b.Scope<BrokenSnapshot>(s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread),
                    x => x.Step<Noop>()))
                .Catch<Exception>(b => b.Step<MarkExecution>()).Build();

            var result = await client.RunAsync(definition, new Data()).WaitAsync(TimeSpan.FromSeconds(10));
            id = result.InstanceId;
            Assert.True(probe.Closing.Task.IsCompleted, result.ErrorMessage);
            Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
            Assert.Contains("Scope close exceeded", result.ErrorMessage);
            Assert.Equal(0, probe.Executions);
            Assert.False(probe.Disposed.Task.IsCompleted);
        }
        finally
        {
            probe.Gate.Set();
        }

        await probe.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var done = await client.RecoverAsync(id);
        Assert.Equal(WorkflowExecutionStatus.Faulted, done.Status);
        Assert.Equal(0, probe.Executions);
        Assert.Equal(1, probe.Disposals);
    }

    private static ServiceProvider Services(Probe probe, int maximumThreads = 64, int cap = 100)
    {
        var services = new ServiceCollection();
        services.AddSingleton(probe);
        services.AddIxIFlow(options => options.MaxScopeThreadsPerHost = maximumThreads);

        services.AddSingleton(ExecutionLimits.Default with
        {
            MaxActivitiesPerRun = cap, ScopeCloseTimeout = TimeSpan.FromMilliseconds(100), ReleaseGrace = TimeSpan.FromMilliseconds(100)
        });

        return services.BuildServiceProvider();
    }

    public sealed class Data;

    public sealed class Probe
    {
        public bool BlockOpen { get; init; }
        public bool BlockClose { get; init; }
        public bool FailClose { get; init; }
        public bool BlockDispose { get; init; }
        public bool BlockCapture { get; init; }
        public ManualResetEventSlim Gate { get; } = new(false);
        public TaskCompletionSource Opening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Capturing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<int> Threads { get; } = new();
        public int Disposals;
        public int Executions;
    }

    public sealed class Value(Probe probe) : IDisposable
    {
        public void Dispose()
        {
            probe.Threads.Enqueue(Environment.CurrentManagedThreadId);

            if (probe.BlockDispose)
                probe.Gate.Wait();

            Interlocked.Increment(ref probe.Disposals);
            probe.Disposed.TrySetResult();
        }
    }

    public sealed class Container(Probe probe) : IScopeActivity<Value>
    {
        public Task<Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken)
        {
            probe.Threads.Enqueue(Environment.CurrentManagedThreadId);
            probe.Opening.TrySetResult();

            if (probe.BlockOpen)
                probe.Gate.Wait();

            return Task.FromResult(new Value(probe));
        }

        public Task CloseAsync(Value value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken)
        {
            probe.Threads.Enqueue(Environment.CurrentManagedThreadId);
            probe.Closing.TrySetResult();

            if (probe.BlockClose)
                probe.Gate.Wait();

            if (probe.FailClose)
                throw new InvalidOperationException("close failed");

            return Task.CompletedTask;
        }
    }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class MarkExecution(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.Executions);

            return Task.CompletedTask;
        }
    }

    public sealed class BrokenSnapshot(Probe probe) : IScopeActivity<Value>
    {
        [ActivityOutput]
        public string Text
        {
            get => throw new InvalidOperationException("snapshot failed");
            set
            {
            }
        }

        public Task<Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken) =>
            new Container(probe).OpenAsync(opening, context, cancellationToken);

        public Task CloseAsync(Value value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken) =>
            new Container(probe).CloseAsync(value, closing, context, cancellationToken);
    }

    public sealed class Recoverable(Probe probe) : IRecoverableActivity<int>
    {
        public int CaptureRecoveryState(IActivityContext context)
        {
            probe.Threads.Enqueue(Environment.CurrentManagedThreadId);
            var value = context.Scope<Value>();
            probe.Capturing.TrySetResult();

            if (probe.BlockCapture)
                probe.Gate.Wait();

            Assert.Same(value, context.Scope<Value>());

            return 1;
        }

        public Task<ActivityRecoveryResult<int>> RecoverAsync(int state, IActivityContext context, CancellationToken cancellationToken) =>
            Task.FromResult<ActivityRecoveryResult<int>>(new ActivityRecoveryResult<int>.Completed());

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.Executions);

            return Task.CompletedTask;
        }
    }
}
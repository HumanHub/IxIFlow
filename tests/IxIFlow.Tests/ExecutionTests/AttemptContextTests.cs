using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// An activity attempt sees the attempts of its invocation before it: the host each ran on, the error it ended with,
/// and the note it left (D-37). A retry starts from the saved inputs, so this is how it learns to do something else.
/// </summary>
[Trait("Feature", "recovery")]
[Trait("Spec", "A-09")]
public sealed class AttemptContextTests
{
    [Fact]
    public async Task ARetrySeesTheFailedAttemptItsErrorAndItsNote()
    {
        var probe = new Probe();
        using var services = Services(new InMemoryWorkflowStateRepository(), probe, "node-1");
        var definition = Workflow.Create<Data>("RetrySeesFailure")
            .Step<Noop>()
            .Saga(saga => saga.Step<TriesAnotherEndpoint>(step => step
                .Output(activity => activity.Endpoint).To(ctx => ctx.WorkflowData.Endpoint)
                .OnError<HttpRequestException>(handler => handler.ThenRetry(1))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("eu-2", Assert.IsType<Data>(result.WorkflowData).Endpoint);
        Assert.Collection(probe.Seen,
            first =>
            {
                Assert.Equal(1, first.Number);
                Assert.Empty(first.Previous);
            },
            second =>
            {
                Assert.Equal(2, second.Number);
                var failed = Assert.Single(second.Previous);
                Assert.Equal("node-1", failed.Host);
                Assert.Equal(typeof(HttpRequestException).FullName, failed.ErrorType);
                Assert.Equal("connection reset", failed.ErrorMessage);
                Assert.Equal("eu-1", failed.Note);
                Assert.NotNull(failed.EndedAtUtc);
            });
    }

    // A host that dies mid-attempt leaves an attempt with no end; the recovery on another host sees it.
    [Fact]
    public async Task ARecoveryOnAnotherHostSeesTheAttemptItsHostLost()
    {
        var store = new FaultInjectingRepository();
        var probe = new Probe();
        var definition = Workflow.Create<Data>("RecoverySeesLostAttempt")
            .Step<RecoversByRunningAgain>()
            .Build();
        var instanceId = Guid.NewGuid().ToString("N");
        using (var first = Services(store, probe, "node-1"))
        {
            // The host dies as it saves the activity's end.
            store.FailCommit = _ => probe.Seen.Count > 0;
            await Assert.ThrowsAnyAsync<Exception>(() => first.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new Data(), new WorkflowOptions { InstanceId = instanceId }));
        }
        store.FailCommit = _ => false;
        using var second = Services(store, probe, "node-2");
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);

        var result = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var recovered = probe.Seen[^1];
        Assert.Equal(2, recovered.Number);
        var lost = Assert.Single(recovered.Previous);
        Assert.Equal("node-1", lost.Host);
        Assert.Null(lost.EndedAtUtc);
        Assert.Null(lost.ErrorType);
    }

    // The recovery state is captured knowing which attempt this is, so it can record what the attempt will try.
    [Fact]
    public async Task TheRecoveryStateIsCapturedKnowingTheAttempt()
    {
        var probe = new Probe();
        using var services = Services(new InMemoryWorkflowStateRepository(), probe, "node-1");
        var definition = Workflow.Create<Data>("CaptureSeesAttempt")
            .Step<Noop>()
            .Saga(saga => saga.Step<CapturesItsAttempt>(step => step
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(1))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal([1, 2], probe.Seen.Select(attempt => attempt.Number));
    }

    // A note that cannot be saved is the definition's mistake, reported as such.
    [Fact]
    public async Task ANoteThatCannotBeSavedFailsTheRun()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), new Probe(), "node-1");
        var definition = Workflow.Create<Data>("UnsavableNote")
            .Step<Noop>()
            .Saga(saga => saga.Step<LeavesAnUnsavableNote>(step => step
                .OnError<InvalidOperationException>(handler => handler.ThenRetry(1))))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("The attempt's note", result.ErrorMessage);
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, Probe probe, string hostName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddSingleton(probe);
        services.AddTransient<TriesAnotherEndpoint>();
        services.AddTransient<RecoversByRunningAgain>();
        services.AddTransient<CapturesItsAttempt>();
        services.AddIxIFlow(options => options.HostName = hostName);
        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        public string Endpoint { get; set; } = "";
    }

    public sealed class Probe
    {
        private readonly List<ActivityAttempt> _seen = [];

        public IReadOnlyList<ActivityAttempt> Seen
        {
            get
            {
                lock (_seen)
                    return _seen.ToArray();
            }
        }

        public void Saw(ActivityAttempt attempt)
        {
            lock (_seen)
                _seen.Add(attempt);
        }
    }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Tries eu-1 first; after a failure it reads the note and tries the next endpoint.</summary>
    public sealed class TriesAnotherEndpoint(Probe probe) : IAsyncActivity
    {
        public string Endpoint { get; private set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Saw(context.Attempt);
            if (context.Attempt.Previous is [.., { Note: "eu-1" }])
            {
                Endpoint = "eu-2";
                return Task.CompletedTask;
            }
            context.Attempt.Note = "eu-1";
            throw new HttpRequestException("connection reset");
        }
    }

    public sealed class RecoversByRunningAgain(Probe probe) : IRecoverableActivity<bool>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Saw(context.Attempt);
            return Task.CompletedTask;
        }

        public bool CaptureRecoveryState(IActivityContext context) => true;

        public Task<ActivityRecoveryResult<bool>> RecoverAsync(bool state, IActivityContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Execute());
    }

    /// <summary>Records the attempt its recovery state was captured in; fails the first time.</summary>
    public sealed class CapturesItsAttempt(Probe probe) : IRecoverableActivity<int>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            context.Attempt.Number == 1
                ? throw new InvalidOperationException("fails once")
                : Task.CompletedTask;

        public int CaptureRecoveryState(IActivityContext context)
        {
            probe.Saw(context.Attempt);
            return context.Attempt.Number;
        }

        public Task<ActivityRecoveryResult<int>> RecoverAsync(int state, IActivityContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<int>>(new ActivityRecoveryResult<int>.Execute());
    }

    public sealed class LeavesAnUnsavableNote : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            context.Attempt.Note = new Cyclic();
            throw new InvalidOperationException("fails once");
        }
    }

    /// <summary>Refers to itself, so it cannot be written as JSON.</summary>
    public sealed class Cyclic
    {
        public Cyclic Self => this;
    }
}

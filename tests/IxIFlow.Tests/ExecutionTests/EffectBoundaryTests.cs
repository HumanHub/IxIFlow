using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Control flow between effect boundaries is not committed: after an interruption it is re-derived
/// from the last checkpoint, and no activity runs twice. Whatever the outside world can depend on
/// is committed first (spec A-04).
/// </summary>
[Trait("Feature", "recovery")]
[Trait("Spec", "A-04")]
public sealed class EffectBoundaryTests
{
    [Fact]
    public async Task ControlFlowAfterTheLastCheckpointIsReDerived()
    {
        // Every attempt to commit the start of the branch's activity fails, so the run stops there.
        var repository = new FaultInjectingRepository
        {
            FailCommit = instance => instance.ExecutionStateJson.Contains("/then\"")
        };
        using var services = Services(repository);
        var definition = Workflow.Create<BoundaryData>("ReDerivedControlFlow")
            .Step<RecordActivity>(setup => setup.Input(activity => activity.Name).From(_ => "first"))
            .If(ctx => ctx.WorkflowData.Branch == "then",
                then => then.Step<RecordActivity>(setup => setup.Input(activity => activity.Name).From(_ => "then")),
                @else => @else.Step<RecordActivity>(setup => setup.Input(activity => activity.Name).From(_ => "else")))
            .Step<RecordActivity>(setup => setup.Input(activity => activity.Name).From(_ => "last"))
            .Build();

        await EngineAssert.StoreFailureAsync(() => Engine(services)
            .RunAsync(definition, new BoundaryData { Branch = "then" }));

        // The If was never committed: the last checkpoint ends after the first activity.
        var saved = Assert.Single(await repository.GetWorkflowInstancesByNameAsync("ReDerivedControlFlow"));
        var root = ExecutionCheckpoint.Read(saved.ExecutionStateJson).Continuations[0];
        Assert.Null(root.PendingActivity);
        Assert.Equal(["root"], root.Stack.Select(frame => frame.ScopeId));
        Assert.Equal(1, root.Stack[0].NextStepIndex);

        repository.FailCommit = _ => false;
        using var scanner = new WorkflowRecoveryService(services.GetRequiredService<IServiceScopeFactory>(),
            repository, NullLogger<WorkflowRecoveryService>.Instance);
        await scanner.RecoverOnceAsync(CancellationToken.None);

        Assert.Equal(WorkflowStatus.Completed, (await repository.GetWorkflowInstanceAsync(saved.InstanceId))!.Status);
        Assert.Equal(["first", "then", "last"], Probe(services).Ran);
    }

    [Fact]
    public async Task CancellingASiblingIsCommittedBeforeItsActivityIsTold()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var definition = Workflow.Create<BoundaryData>("CommittedSiblingCancellation")
            .Step<RecordActivity>(setup => setup.Input(activity => activity.Name).From(_ => "first"))
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch.Step<BlockingActivity>())
                .Do(branch => branch.Step<RecordActivity>(setup => setup.Input(activity => activity.Name).From(_ => "winner"))))
            .Build();

        var result = await Engine(services).RunAsync(definition, new BoundaryData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(Probe(services).CancellationWasSavedBeforeSignal,
            "The losing branch's cancellation reached its activity before it was committed");
    }

    [Fact]
    public async Task ScheduledSagaRetryIsCommittedBeforeTheRunWaits()
    {
        var repository = new FaultInjectingRepository();
        using var services = Services(repository);
        var definition = Workflow.Create<BoundaryData>("CommittedSagaRetry")
            .Step<RecordActivity>(setup => setup.Input(activity => activity.Name).From(_ => "first"))
            .Saga(saga => saga.Step<FailActivity>())
            .OnError<InvalidOperationException>(error => error.CompensateNone().ThenRetry(
                new RetryPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(1), 3, 1)))
            .Build();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        repository.FailCommit = instance =>
        {
            if (instance.ExecutionStateJson.Contains("\"RetryAfterUtc\":\""))
                stop.Cancel();
            return false;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine(services)
            .RunAsync(definition, new BoundaryData(), cancellationToken: stop.Token));

        var saved = Assert.Single(await repository.GetWorkflowInstancesByNameAsync("CommittedSagaRetry"));
        Assert.Contains(ExecutionCheckpoint.Read(saved.ExecutionStateJson).Continuations,
            continuation => continuation.RetryAfterUtc != null);
    }

    [Fact]
    public async Task WaitIsCommittedBeforeTheRunBlocksOnASibling()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var definition = Workflow.Create<BoundaryData>("CommittedWaitBeforeBlocking")
            .Step<RecordActivity>(setup => setup.Input(activity => activity.Name).From(_ => "first"))
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<WaitWatchingActivity>())
                .Do(branch => branch.WaitFor<Approval>("approval")))
            .Build();

        var started = await Engine(services).RunAsync(definition, new BoundaryData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.True(Probe(services).WaitWasSavedWhileRunning,
            "The wait was not saved while the sibling activity ran");

        var resumed = await Engine(services).ResumeAsync(started.InstanceId, "approval", new Approval());
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
    }

    private static ServiceProvider Services(IWorkflowStateRepository repository)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(repository);
        services.AddIxIFlow();
        services.AddSingleton<BoundaryProbe>();
        services.AddTransient<RecordActivity>();
        services.AddTransient<BlockingActivity>();
        services.AddTransient<WaitWatchingActivity>();
        services.AddTransient<FailActivity>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Engine(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowClient>();

    private static BoundaryProbe Probe(IServiceProvider services) => services.GetRequiredService<BoundaryProbe>();

    private static async Task<ExecutionCheckpoint> SavedCheckpointAsync(IWorkflowStateRepository repository,
        string instanceId) =>
        ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(instanceId))!.ExecutionStateJson);

    public sealed class BoundaryData
    {
        public string Branch { get; set; } = "";
    }

    public sealed class Approval;

    public sealed class BoundaryProbe
    {
        private readonly ConcurrentQueue<string> _ran = new();
        public IReadOnlyList<string> Ran => _ran.ToArray();
        public bool CancellationWasSavedBeforeSignal { get; set; }
        public bool WaitWasSavedWhileRunning { get; set; }
        public void Record(string name) => _ran.Enqueue(name);
    }

    public sealed class RecordActivity(BoundaryProbe probe) : IAsyncActivity
    {
        public string Name { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Record(Name);
            return Task.CompletedTask;
        }
    }

    /// <summary>Runs until cancelled, then checks that the store already says its branch is cancelled.</summary>
    public sealed class BlockingActivity(BoundaryProbe probe, IWorkflowStateRepository repository) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                var saved = await SavedCheckpointAsync(repository, context.WorkflowInstanceId);
                probe.CancellationWasSavedBeforeSignal = saved.Continuations.Any(continuation =>
                    continuation.Status == ContinuationStatus.Cancelling);
                throw;
            }
        }
    }

    /// <summary>Runs until the sibling branch's wait is in the store, or gives up after five seconds.</summary>
    public sealed class WaitWatchingActivity(BoundaryProbe probe, IWorkflowStateRepository repository) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if ((await SavedCheckpointAsync(repository, context.WorkflowInstanceId)).Waits.Count > 0)
                {
                    probe.WaitWasSavedWhileRunning = true;
                    return;
                }
                await Task.Delay(20, cancellationToken);
            }
        }
    }

    public sealed class FailActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Planned failure");
    }
}

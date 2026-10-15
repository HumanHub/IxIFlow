using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Control flow between effect boundaries is not committed: after an interruption it is re-derived
/// from the last checkpoint, and no activity runs twice (spec A-04).
/// </summary>
[Trait("Feature", "recovery")]
[Trait("Spec", "A-04")]
public sealed class EffectBoundaryTests
{
    [Fact]
    public async Task ControlFlowAfterTheLastCheckpointIsReDerived()
    {
        var commits = 0;
        // Commits: start, first activity start, first activity end, then the start of the branch's
        // activity, which fails in all three attempts. The If between them was never committed.
        var repository = new FaultInjectingRepository
        {
            FailCommit = _ => Interlocked.Increment(ref commits) is >= 4 and <= 6
        };
        using var services = Services(repository);
        var definition = Workflow.Create<BoundaryData>("ReDerivedControlFlow")
            .Step<RecordActivity>(setup => setup.Input(activity => activity.Name).From(_ => "first"))
            .If(ctx => ctx.WorkflowData.Branch == "then",
                then => then.Step<RecordActivity>(setup => setup.Input(activity => activity.Name).From(_ => "then")),
                @else => @else.Step<RecordActivity>(setup => setup.Input(activity => activity.Name).From(_ => "else")))
            .Step<RecordActivity>(setup => setup.Input(activity => activity.Name).From(_ => "last"))
            .Build();

        await EngineAssert.StoreFailureAsync(() => services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new BoundaryData { Branch = "then" }));
        var saved = Assert.Single(await repository.GetWorkflowInstancesByNameAsync("ReDerivedControlFlow"));

        using var scanner = new WorkflowRecoveryService(services.GetRequiredService<IServiceScopeFactory>(),
            repository, NullLogger<WorkflowRecoveryService>.Instance);
        await scanner.RecoverOnceAsync(CancellationToken.None);

        Assert.Equal(WorkflowStatus.Completed, (await repository.GetWorkflowInstanceAsync(saved.InstanceId))!.Status);
        Assert.Equal(["first", "then", "last"], services.GetRequiredService<BoundaryProbe>().Ran);
    }

    private static ServiceProvider Services(IWorkflowStateRepository repository)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(repository);
        services.AddIxIFlow();
        services.AddSingleton<BoundaryProbe>();
        services.AddTransient<RecordActivity>();
        return services.BuildServiceProvider();
    }

    public sealed class BoundaryData
    {
        public string Branch { get; set; } = "";
    }

    public sealed class BoundaryProbe
    {
        private readonly ConcurrentQueue<string> _ran = new();
        public IReadOnlyList<string> Ran => _ran.ToArray();
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
}

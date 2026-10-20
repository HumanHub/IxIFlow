using System.Diagnostics;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Xunit.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "recovery")]
[Collection("Coordinator dormant population scale")]
public sealed class CoordinatorDormantPopulationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ClaimAndBacklogCostDoNotGrowWithDormantMembersOfOneSet()
    {
        var store = new InMemoryWorkflowStateRepository();
        var query = new ClaimQuery(Guid.NewGuid(), new Dictionary<string, string>());
        var definition = Workflow.Create<Data>("DormantPopulation").Demand("region", "absent").Step<Noop>().Build();
        await Add("first");
        var before = await Measure();
        for (var i = 0; i < 100_000; i++) await Add("dormant-" + i);
        var after = await Measure();
        var budget = Math.Max(5, before.TotalMilliseconds * 8);
        output.WriteLine($"100,001 dormant members of one set, median claim/backlog: {before.TotalMilliseconds:F3} -> {after.TotalMilliseconds:F3} ms; budget {budget:F3} ms");
        Assert.True(after.TotalMilliseconds <= budget, $"Dormant population scan took {after.TotalMilliseconds:F3} ms; budget {budget:F3} ms");
        Assert.Empty(await store.GetRunnableInstancesAsync(query));
        Assert.Empty((await store.GetUnschedulableAsync()).Items);

        async Task Add(string id)
        {
            var (instance, checkpoint, catalog) = WorkflowInstances.NewInstance(definition, new Data(),
                new() { InstanceId = id }, DateTime.UtcNow);
            ExecutionCheckpointWriter.Prepare(instance, checkpoint, new Data(), catalog, DateTime.UtcNow);
            instance.Status = WorkflowStatus.Completed;
            await store.SeedAsync(instance);
        }
        async Task<TimeSpan> Measure()
        {
            var samples = new List<TimeSpan>();
            for (var i = 0; i < 9; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await store.GetRunnableInstancesAsync(query);
                await store.GetUnschedulableAsync();
                samples.Add(Stopwatch.GetElapsedTime(start));
            }
            return samples.Order().ElementAt(4);
        }
    }
    public sealed class Data;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

// Population setup and timing need a quiet process; they must not load other suites' timer tests.
[CollectionDefinition("Coordinator dormant population scale", DisableParallelization = true)]
public sealed class CoordinatorDormantPopulationCollection;

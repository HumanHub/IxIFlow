using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "recovery"), Trait("Spec", "E-07"), Trait("Spec", "E-10"), Trait("Spec", "J-03")]
public sealed class NestedJoinRecoveryScenarios
{
    private const string InnerStep = "root/1/branch/0/0";
    private const string OuterStep = "root/1";
    public static IEnumerable<object[]> Cases => new[] { StoreProfiles.LocalMemory, StoreProfiles.LocalSqlite,
        StoreProfiles.DistributedSqlServer, StoreProfiles.DistributedPostgreSql }.SelectMany(profile =>
        new[] { "before", "after", "lost-ack" }.Select(point => new object[] { profile, point }));

    [Theory, MemberData(nameof(Cases))]
    public async Task FreshHostRecoversTheInnerMergeExactlyOnce(string profileName, string point)
    {
        await using var profile = await StoreProfiles.CreateAsync(profileName);
        var registry = new WorkflowVersionRegistry();
        var effects = new Effects();
        var definition = Workflow.Create<Data>("NestedJoinCrash")
            .Step<Noop>()
            .Parallel(outer => outer
                .Do(branch => branch.Parallel(inner => inner
                    .Do(body => body.Step<Add>(s => s.Input(a => a.Amount).From(_ => 2)
                        .Input(a => a.Name).From(_ => "inner-a")))
                    .Do(body => body.Step<Add>(s => s.Input(a => a.Amount).From(_ => 3)
                        .Input(a => a.Name).From(_ => "inner-b"))))
                    .WithId("inner")
                    .Step<Mark>(s => s.Input(a => a.Name).From(_ => "after-inner")))
                .Do(branch => branch.Step<Add>(s => s.Input(a => a.Amount).From(_ => 7)
                    .Input(a => a.Name).From(_ => "outer-sibling"))))
            .WithId("outer")
            .Step<Mark>(s => s.Input(a => a.Name).From(_ => "after-outer"))
            .Build();
        await registry.RegisterWorkflowAsync(definition);
        var firstStore = profile.NewRepository();
        var wrapped = DispatchProxy.Create<IWorkflowStateRepository, JoinFault>();
        var fault = (JoinFault)wrapped;
        fault.Store = firstStore;
        fault.Point = point;
        var id = Guid.NewGuid().ToString("N");
        await using (var first = Services(wrapped, registry, effects))
        {
            WorkflowExecutionResult? interruptedResult = null;
            var error = await Record.ExceptionAsync(async () => interruptedResult = await first.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new Data { Total = 10 }, new WorkflowOptions { InstanceId = id }));
            if (point == "after") Assert.False(interruptedResult?.IsSuccess ?? true);
            else Assert.True(error != null, "No interruption; joins observed: " + string.Join(",", fault.Seen));
            Assert.True(fault.Interrupted, "The test must hit the inner-join commit, not an earlier activity");
            Assert.Equal(point == "before" ? 0 : 1, fault.Applied);
            Assert.DoesNotContain("after-inner", effects.Counts.Keys);
            Assert.DoesNotContain("after-outer", effects.Counts.Keys);
        }
        if (firstStore is IAsyncDisposable disposable) await disposable.DisposeAsync();
        var secondStore = profile.NewRepository();
        await using var second = Services(secondStore, registry, effects);
        var finished = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(id).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(finished.IsSuccess, finished.ErrorMessage);
        Assert.Equal(22, Assert.IsType<Data>(finished.WorkflowData).Total);
        Assert.Equal(new[] { "after-inner", "after-outer", "inner-a", "inner-b", "outer-sibling" }, effects.Counts.Keys.Order());
        Assert.All(effects.Counts.Values, count => Assert.Equal(1, count));
        var saved = (await secondStore.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(22, JsonSerializer.Deserialize<Data>(saved.WorkflowDataJson)!.Total);
        Assert.Empty(ExecutionCheckpoint.Read(saved.ExecutionStateJson).Joins);
        var joins = (await secondStore.GetTraceAsync(id)).Select(e => e.Entry.Payload).OfType<JoinTrace>().ToArray();
        Assert.Single(joins, join => join.StepId == InnerStep && join.Path == JoinPath.Merged);
        Assert.Single(joins, join => join.StepId == OuterStep && join.Path == JoinPath.Merged);
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, IWorkflowVersionRegistry registry, Effects effects)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddSingleton(store);
        services.AddSingleton(registry);
        services.AddSingleton(effects);
        services.AddTransient<Add>();
        services.AddTransient<Mark>();
        return services.BuildServiceProvider();
    }

    public class JoinFault : DispatchProxy
    {
        internal IWorkflowStateRepository Store = null!;
        internal string Point = "";
        internal bool Interrupted;
        internal int Applied;
        internal List<string> Seen = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IWorkflowStateRepository.CommitWorkflowInstanceAsync))
                return Commit((CheckpointCommit)args![0]!);
            try { return targetMethod.Invoke(Store, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        }
        private async Task<WorkflowCommitResult> Commit(CheckpointCommit commit)
        {
            if (Interrupted) throw new IOException("Host stopped at the inner join");
            var joins = commit.ReadTraceEntries().Select(e => e.Payload).OfType<JoinTrace>().ToArray();
            Seen.AddRange(joins.Select(j => j.StepId));
            var innerMerge = joins.Any(j => j.StepId == InnerStep && j.Path == JoinPath.Merged);
            if (!innerMerge) return await Store.CommitWorkflowInstanceAsync(commit);
            Interrupted = true;
            if (Point == "before") throw new IOException("Interrupted before inner join persistence");
            var result = await Store.CommitWorkflowInstanceAsync(commit);
            Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
            Applied++;
            if (Point == "lost-ack") throw new IOException("Inner join applied; acknowledgement lost");
            return new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, result.Revision);
        }
    }
    public sealed class Data { [Combine<Sum<int>>] public int Total { get; set; } }
    public sealed class Effects { public ConcurrentDictionary<string, int> Counts { get; } = new(); }
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Add(Effects effects) : IAsyncActivity
    {
        public int Amount { get; set; }
        public string Name { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { effects.Counts.AddOrUpdate(Name, 1, (_, count) => count + 1); ((Data)context.WorkflowData).Total += Amount; return Task.CompletedTask; }
    }
    public sealed class Mark(Effects effects) : IAsyncActivity, IRecoverableActivity<bool>
    {
        public string Name { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { effects.Counts.AddOrUpdate(Name, 1, (_, count) => count + 1); return Task.CompletedTask; }
        public bool CaptureRecoveryState(IActivityContext context) => true;
        public Task<ActivityRecoveryResult<bool>> RecoverAsync(bool state, IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Execute());
    }
}

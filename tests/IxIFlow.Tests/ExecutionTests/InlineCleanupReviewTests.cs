using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "F-17")]
public sealed class InlineCleanupReviewTests
{
    [Theory]
    [InlineData(false, false), InlineData(true, false), InlineData(false, true), InlineData(true, true)]
    public async Task RetiredOwnerInsideFinallyDoesNotResumeAndEnclosingCleanupRunsOnce(bool inline, bool recover)
    {
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();
        var callee = CleanupDefinition(recover);

        var pin = new WorkflowInlinePin(callee.Name, callee.Version,
            WorkflowDefinitionValidator.Inspect(callee), WorkflowDefinitionValidator.GetContract(callee).Hash);

        var definition = Workflow.Create<Data>("InlineInheritedCleanupReview").Step<Noop>()
            .Try(b =>
            {
                if (inline)
                    b.Use<Data>(() => CleanupDefinition(recover), pin);
                else
                    NestedCleanup(b, recover);
            }).Finally(b => b.Step<MarkFinally>()).Build();

        WorkflowExecutionResult result;

        await using (var first = Services(store, catalog))
        {
            result = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data())
                .WaitAsync(TimeSpan.FromSeconds(10));

            if (recover)
            {
                Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
                var saved = (await store.GetWorkflowInstanceAsync(result.InstanceId))!;
                var state = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(saved));
                Assert.Equal(2, state.Joins.Count);
                var inner = Assert.Single(state.Joins, j => state.ContinuationById(j.ParentContinuationId)!.ParentJoinId != null);
                var owner = state.ContinuationById(inner.ParentContinuationId)!;
                Assert.Equal(ContinuationStatus.Cancelled, owner.Status);
                Assert.Empty(owner.Stack);
                Assert.False(ContinuationTransitions.CanFinish(state, inner));
                Assert.False(ContinuationTransitions.Merges(state, inner));
                var ancestor = Assert.Single(state.Joins, j => j != inner);

                Assert.All(state.BranchesOf(ancestor), c =>
                    Assert.True(c.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled));

                Assert.False(ContinuationTransitions.CanFinish(state, ancestor));
                Assert.Equal("cleanup", state.UnhandledError!.Message);
                Assert.Equal(0, Assert.IsType<Data>(result.WorkflowData).Finally);
            }
        }

        if (recover)
        {
            await using var second = Services(store, catalog);

            result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(result.InstanceId, "inner-settle", new Signal())
                .WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(1, Assert.IsType<Data>(result.WorkflowData).Finally);
        var terminal = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(result.InstanceId))!));
        Assert.Equal("cleanup", terminal.UnhandledError!.Message);
        Assert.Contains(nameof(InvalidOperationException), terminal.UnhandledError.Type);
        Assert.Empty(terminal.Joins);
    }

    private static WorkflowDefinition CleanupDefinition(bool recover)
    {
        var builder = Workflow.Create<Data>("InheritedCleanupBody").Step<Noop>();
        NestedCleanup(builder, recover);

        return builder.Build();
    }

    private static void NestedCleanup<T>(IWorkflowBuilder<Data, T> builder, bool recover) where T : class =>
        builder.Try(b => b.Step<Noop>()).Finally(b => b.Parallel(p => p.WaitAny()
            .Do(x => x.Try(y => y.WaitFor<Signal>("hold")).Finally(y => y.Parallel(q => q
                .Do(z => z.Step<Fail>())
                .Do(z =>
                {
                    if (recover)
                        z.Try(w => w.WaitFor<Signal>("inner-hold"))
                            .Finally(w => w.WaitFor<Signal>("inner-settle").Step<Noop>());
                    else
                        z.Step<Noop>();
                }))))
            .Do(x => x.Step<Noop>())));

    private static ServiceProvider Services(IWorkflowStateRepository store, IWorkflowCatalog catalog)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        services.AddSingleton(store);
        services.AddSingleton(catalog);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task FailureInNestedParallelCleanupStillRunsOuterFinally()
    {
        var store = new InMemoryWorkflowStateRepository();
        var collection = new ServiceCollection().AddLogging();
        collection.AddIxIFlow();
        collection.AddSingleton<IWorkflowStateRepository>(store);
        await using var services = collection.BuildServiceProvider();

        var definition = Workflow.Create<Data>("InlineCleanupReview").Step<Noop>()
            .Try(b => b.Parallel(p => p.WaitAny()
                .Do(x => x.Try(y => y.WaitFor<Signal>("hold"))
                    .Finally(y => y.Parallel(q => q.Do(z => z.Step<Fail>()).Do(z => z.Step<Noop>()))))
                .Do(x => x.Step<Noop>())))
            .Catch<InvalidOperationException>(b => b.Step<MarkCatch>())
            .Finally(b => b.Step<MarkFinally>()).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data())
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(0, Assert.IsType<Data>(result.WorkflowData).Catch);
        Assert.Equal(1, Assert.IsType<Data>(result.WorkflowData).Finally);
    }

    public sealed class Data
    {
        public int Catch { get; set; }
        public int Finally { get; set; }
    }

    public sealed class Signal
    {
    }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext c, CancellationToken ct) => Task.CompletedTask;
    }

    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext c, CancellationToken ct) => throw new InvalidOperationException("cleanup");
    }

    public sealed class MarkCatch : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext c, CancellationToken ct)
        {
            ((Data)c.WorkflowData).Catch++;

            return Task.CompletedTask;
        }
    }

    public sealed class MarkFinally : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext c, CancellationToken ct)
        {
            ((Data)c.WorkflowData).Finally++;

            return Task.CompletedTask;
        }
    }
}
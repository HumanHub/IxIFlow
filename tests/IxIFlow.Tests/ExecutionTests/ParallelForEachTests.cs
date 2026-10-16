using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// A parallel <c>ForEach</c> runs each item as a branch from a copy of the data at the fork, at most
/// <c>maxConcurrency</c> at a time, joins them as a parallel step does, and stores one result per item
/// with its status (D-34, spec E-12, E-13).
/// </summary>
[Trait("Feature", "control-flow")]
[Trait("Spec", "E-12")]
public sealed class ParallelForEachTests
{
    [Fact]
    [Trait("Spec", "E-13")]
    public async Task EveryItemRunsFromTheForkDataAndTheJoinMergesThemInItemOrder()
    {
        using var services = Services();
        var definition = Workflow.Create<Approvals>("EveryItem")
            .Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Approvers,
                (body, approver) => body
                    .Step<Sign>(step => step
                        .Input(activity => activity.Name).From(ctx => ctx.Item(approver))
                        .Input(activity => activity.Signatures).From(ctx => ctx.WorkflowData.Signatures)
                        .Output(activity => activity.Signatures).To(ctx => ctx.WorkflowData.Signatures))
                    .Result(ctx => ctx.WorkflowData.Signatures.Count),
                setup => setup.Parallel().Into(ctx => ctx.WorkflowData.Results))
            .Build();

        var result = await Engine(services).RunAsync(definition, new Approvals { Approvers = ["ann", "bob", "cid"] });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<Approvals>(result.WorkflowData);
        // Each item saw only its own signature: the data at the fork plus its own change.
        Assert.Equal(["ann", "bob", "cid"], data.Signatures);
        Assert.Equal(
            [
                new ItemResult<int>(0, ItemStatus.Completed, 1),
                new ItemResult<int>(1, ItemStatus.Completed, 1),
                new ItemResult<int>(2, ItemStatus.Completed, 1)
            ],
            data.Results);
    }

    [Fact]
    public async Task WithACapQueuedItemsStartOnlyAfterAnotherCompletes()
    {
        using var services = Services();
        var engine = Engine(services);
        var outbox = services.GetRequiredService<Outbox>();
        var definition = Workflow.Create<Approvals>("CappedRequests")
            .Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Approvers,
                (body, approver) => body
                    .Request<Ask>("approval", step => step
                        .Input(activity => activity.Name).From(ctx => ctx.Item(approver)))
                    .WaitFor<Answer>("approval")
                    .Result(ctx => ctx.PreviousStep.Approved),
                setup => setup.Parallel(maxConcurrency: 2).Into(ctx => ctx.WorkflowData.Answers))
            .Build();

        var started = await engine.RunAsync(definition, new Approvals { Approvers = ["ann", "bob", "cid", "dan", "eve"] });

        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal(["ann", "bob"], outbox.Names);
        EventAssert.Consumed(await engine.ReplyAsync(outbox.TokenOf("bob"), new Answer { Approved = true }));
        Assert.Equal(["ann", "bob", "cid"], outbox.Names);
        foreach (var name in new[] { "ann", "cid", "dan", "eve" })
            await engine.ReplyAsync(outbox.TokenOf(name), new Answer { Approved = name != "dan" });

        var finished = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal([true, true, true, false, true],
            Assert.IsType<Approvals>(finished.WorkflowData).Answers.Select(item => item.Value));
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public async Task WaitAnyTakesTheFirstItemCancelsTheRestAndNeverStartsTheQueued()
    {
        using var services = Services();
        var engine = Engine(services);
        var outbox = services.GetRequiredService<Outbox>();
        var definition = Workflow.Create<Approvals>("AnyApprover")
            .Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Approvers,
                (body, approver) => body
                    .Request<Ask>("approval", step => step
                        .Input(activity => activity.Name).From(ctx => ctx.Item(approver)))
                    .WaitFor<Answer>("approval")
                    .Result(ctx => ctx.PreviousStep.Approved),
                setup => setup.Parallel(maxConcurrency: 2).WaitAny().Into(ctx => ctx.WorkflowData.Answers))
            .Build();
        var started = await engine.RunAsync(definition, new Approvals { Approvers = ["ann", "bob", "cid"] });

        var finished = await engine.ReplyAsync(outbox.TokenOf("bob"), new Answer { Approved = true });

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(
            [
                new ItemResult<bool>(0, ItemStatus.Cancelled, false),
                new ItemResult<bool>(1, ItemStatus.Completed, true),
                new ItemResult<bool>(2, ItemStatus.NotStarted, false)
            ],
            Assert.IsType<Approvals>(finished.WorkflowData).Answers);
        Assert.Equal(["ann", "bob"], outbox.Names);
        EventAssert.Rejected(await engine.ReplyAsync(outbox.TokenOf("ann"), new Answer { Approved = true }),
            EventRejection.WaitCancelled);
        Assert.Equal(started.InstanceId, finished.InstanceId);
    }

    [Fact]
    [Trait("Spec", "E-09")]
    public async Task AConditionalJoinReadsTheResultsSoFar()
    {
        using var services = Services();
        var engine = Engine(services);
        var outbox = services.GetRequiredService<Outbox>();
        var definition = Workflow.Create<Approvals>("TwoApprovals")
            .Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Approvers,
                (body, approver) => body
                    .Request<Ask>("approval", step => step
                        .Input(activity => activity.Name).From(ctx => ctx.Item(approver)))
                    .WaitFor<Answer>("approval")
                    .Result(ctx => ctx.PreviousStep.Approved),
                setup => setup.Parallel()
                    .WaitConditionally<bool>((_, results) => results.Count(item => item.Value) >= 2)
                    .Into(ctx => ctx.WorkflowData.Answers))
            .Build();
        var started = await engine.RunAsync(definition, new Approvals { Approvers = ["ann", "bob", "cid", "dan"] });

        await engine.ReplyAsync(outbox.TokenOf("ann"), new Answer { Approved = false });
        await engine.ReplyAsync(outbox.TokenOf("cid"), new Answer { Approved = true });
        var finished = await engine.ReplyAsync(outbox.TokenOf("dan"), new Answer { Approved = true });

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal([ItemStatus.Completed, ItemStatus.Cancelled, ItemStatus.Completed, ItemStatus.Completed],
            Assert.IsType<Approvals>(finished.WorkflowData).Answers.Select(item => item.Status));
        Assert.Equal(started.InstanceId, finished.InstanceId);
    }

    [Fact]
    [Trait("Spec", "E-13")]
    public async Task AnEmptyListCompletesAtOnceAndAFailedItemStoresNoResults()
    {
        using var services = Services();
        var definition = Workflow.Create<Approvals>("FailingItem")
            .Step<Noop>()
            .Try(attempt => attempt.ForEach(ctx => ctx.WorkflowData.Approvers,
                (body, approver) => body
                    .Step<FailFor>(step => step.Input(activity => activity.Name).From(ctx => ctx.Item(approver)))
                    .Result(_ => true),
                setup => setup.Parallel().Into(ctx => ctx.WorkflowData.Answers)))
            .Catch<InvalidOperationException>(handler => handler.Step<Sign>(step => step
                .Input(activity => activity.Name).From(_ => "caught")
                .Input(activity => activity.Signatures).From(ctx => ctx.WorkflowData.Signatures)
                .Output(activity => activity.Signatures).To(ctx => ctx.WorkflowData.Signatures)))
            .Build();

        var empty = await Engine(services).RunAsync(definition, new Approvals { Answers = null! });
        var failed = await Engine(services).RunAsync(definition,
            new Approvals { Approvers = ["ann", "bob"], Answers = [new ItemResult<bool>(9, ItemStatus.Completed, true)] });

        Assert.Equal(WorkflowExecutionStatus.Success, empty.Status);
        Assert.Empty(Assert.IsType<Approvals>(empty.WorkflowData).Answers);
        Assert.Equal(WorkflowExecutionStatus.Success, failed.Status);
        var data = Assert.IsType<Approvals>(failed.WorkflowData);
        Assert.Equal(["caught"], data.Signatures);
        Assert.Equal(9, Assert.Single(data.Answers).Index);
    }

    [Fact]
    public void JoinOptionsNeedParallelAndItemsMayNotAllWriteOnePropertyWithoutACombiner()
    {
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Approvals>("SequentialJoin")
            .Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Approvers, (body, _) => body.Step<Noop>(),
                setup => setup.WaitAny())
            .Build());
        var write = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Approvals>("EveryItemWrites")
            .Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Approvers,
                (body, approver) => body.Step<Sign>(step => step
                    .Input(activity => activity.Name).From(ctx => ctx.Item(approver))
                    .Output(activity => activity.Name).To(ctx => ctx.WorkflowData.Last)),
                setup => setup.Parallel())
            .Build());
        Assert.Contains("no combiner", write.Message);
    }

    [Fact]
    public void TheFingerprintTellsSequentialFromParallelAndTheCap()
    {
        WorkflowDefinition Define(Action<Builders.Interfaces.IForEachSetup<Approvals>> setup) =>
            Workflow.Create<Approvals>("Modes")
                .Step<Noop>()
                .ForEach(ctx => ctx.WorkflowData.Approvers, (body, _) => body.Step<Noop>(), setup)
                .Build();

        var fingerprints = new[]
        {
            Define(_ => { }), Define(setup => setup.Parallel()), Define(setup => setup.Parallel(2))
        }.Select(definition => new WorkflowScopeCatalog(definition).Fingerprint);

        Assert.Equal(3, fingerprints.Distinct().Count());
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Outbox>();
        services.AddTransient<Ask>();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Engine(IServiceProvider services) => services.GetRequiredService<IWorkflowClient>();

    public sealed class Approvals
    {
        public List<string> Approvers { get; set; } = [];

        [Combine<Append<string>>]
        public List<string> Signatures { get; set; } = [];

        public string Last { get; set; } = "";
        public List<ItemResult<int>> Results { get; set; } = [];
        public List<ItemResult<bool>> Answers { get; set; } = [];
    }

    public sealed class Answer
    {
        public bool Approved { get; set; }
    }

    public sealed class Outbox
    {
        private readonly List<(string Name, string Token)> _sent = [];

        public IReadOnlyList<string> Names
        {
            get { lock (_sent) return _sent.Select(item => item.Name).ToArray(); }
        }

        public string TokenOf(string name)
        {
            lock (_sent)
                return _sent.Single(item => item.Name == name).Token;
        }

        public void Add(string name, string token)
        {
            lock (_sent)
                _sent.Add((name, token));
        }
    }

    public sealed class Ask(Outbox outbox) : IRequestActivity<Answer>
    {
        public string Name { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            outbox.Add(Name, context.Reply!.Token);
            return Task.CompletedTask;
        }
    }

    public sealed class Sign : IAsyncActivity
    {
        public string Name { get; set; } = "";
        public List<string> Signatures { get; set; } = [];

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Signatures = [.. Signatures, Name];
            return Task.CompletedTask;
        }
    }

    public sealed class FailFor : IAsyncActivity
    {
        public string Name { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Name == "bob" ? throw new InvalidOperationException("bob failed") : Task.CompletedTask;
    }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

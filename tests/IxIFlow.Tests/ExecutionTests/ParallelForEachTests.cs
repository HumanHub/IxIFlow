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

    // Recipe 20 of spec D-17: the host dies after some items started. Another host continues the same
    // items from the saved list, and an approver added to the source afterwards is never asked.
    [Fact]
    [Trait("Spec", "D-17")]
    public async Task AnotherHostContinuesTheSavedListAfterTheFirstStops()
    {
        var store = new InMemoryWorkflowStateRepository();
        var outbox = new Outbox();
        var definition = Workflow.Create<Approvals>("ContinuedFanOut")
            .Step<Noop>()
            .ForEach(_ => Source.Approvers,
                (body, approver) => body
                    .Request<Ask>("approval", step => step
                        .Input(activity => activity.Name).From(ctx => ctx.Item(approver)))
                    .WaitFor<Answer>("approval")
                    .Result(ctx => ctx.PreviousStep.Approved),
                setup => setup.Parallel(maxConcurrency: 2).Into(ctx => ctx.WorkflowData.Answers))
            .Build();
        Source.Approvers = ["ann", "bob", "cid"];
        string instanceId;
        using (var first = Services(store, outbox))
            instanceId = (await Engine(first).RunAsync(definition, new Approvals())).InstanceId;
        Source.Approvers = ["ann", "bob", "cid", "zed"];

        using var second = Services(store, outbox);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        foreach (var name in new[] { "ann", "bob" })
            await Engine(second).ReplyAsync(outbox.TokenOf(name), new Answer { Approved = true });
        var finished = await Engine(second).ReplyAsync(outbox.TokenOf("cid"), new Answer { Approved = false });

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(["ann", "bob", "cid"], outbox.Names);
        Assert.Equal([true, true, false], Assert.IsType<Approvals>(finished.WorkflowData).Answers.Select(item => item.Value));
        Assert.Equal(instanceId, finished.InstanceId);
    }

    // Recipe 20 again, with the host dying in the commit that holds an item's completion and the next
    // item's admission: the next host completes the same item and admits the next one once.
    [Fact]
    [Trait("Spec", "D-17")]
    public async Task AHostThatDiesBetweenACompletionAndTheNextAdmissionLeavesBothToTheNext()
    {
        var store = new FaultInjectingRepository();
        var outbox = new Outbox();
        var definition = CappedApprovals("DiesAtAdmission");
        string instanceId;
        using (var first = Services(store, outbox))
        {
            instanceId = (await Engine(first).RunAsync(definition,
                new Approvals { Approvers = ["ann", "bob", "cid"] })).InstanceId;
            store.FailCommit = instance => instance.ExecutionStateJson.Contains("\"Finished\":[1]");
            await IgnoreFailureAsync(Engine(first).ReplyAsync(outbox.TokenOf("bob"), new Answer { Approved = true }));
            Assert.True(store.FailedCommits > 0);
        }
        store.FailCommit = _ => false;

        using var second = Services(store, outbox);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await Engine(second).RecoverAsync(instanceId)).Status);
        await Engine(second).ReplyAsync(outbox.TokenOf("ann"), new Answer { Approved = true });
        var finished = await Engine(second).ReplyAsync(outbox.LastTokenOf("cid"), new Answer { Approved = false });

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(["ann", "bob", "cid"], outbox.Names.Distinct());
        Assert.Equal([true, true, false], Assert.IsType<Approvals>(finished.WorkflowData).Answers.Select(item => item.Value));
    }

    // Recipe 20: a join decided before the host died is not decided again by the next host, even where
    // the condition would now say otherwise. The losing item's cleanup runs after the decision is saved.
    [Fact]
    [Trait("Spec", "D-17")]
    public async Task AJoinDecidedBeforeTheHostDiedIsNotReconsidered()
    {
        var store = new FaultInjectingRepository();
        var outbox = new Outbox();
        var definition = Workflow.Create<Approvals>("DecidedThenDied")
            .Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Approvers,
                (body, approver) => body
                    .Try(attempt => attempt
                        .Request<Ask>("approval", step => step
                            .Input(activity => activity.Name).From(ctx => ctx.Item(approver)))
                        .WaitFor<Answer>("approval"))
                    .Finally(cleanup => cleanup.Step<Cleanup>())
                    .Result(_ => true),
                setup => setup.Parallel()
                    .WaitConditionally<bool>((_, results) => results.Count(item => item.Value) >= Source.Needed)
                    .Into(ctx => ctx.WorkflowData.Answers))
            .Build();
        Source.Needed = 2;
        string instanceId;
        using (var first = Services(store, outbox))
        {
            instanceId = (await Engine(first).RunAsync(definition,
                new Approvals { Approvers = ["ann", "bob", "cid"] })).InstanceId;
            await Engine(first).ReplyAsync(outbox.TokenOf("ann"), new Answer { Approved = true });
            // The host dies in the first commit after the one that saved the decision.
            var decided = false;
            store.FailCommit = instance =>
            {
                if (decided)
                    return true;
                decided = instance.ExecutionStateJson.Contains("\"IsCompleting\":true");
                return false;
            };
            await IgnoreFailureAsync(Engine(first).ReplyAsync(outbox.TokenOf("bob"), new Answer { Approved = true }));
            Assert.True(decided);
        }
        store.FailCommit = _ => false;
        Source.Needed = 3;

        using var second = Services(store, outbox);
        await second.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(definition);
        var finished = await Engine(second).RecoverAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal([ItemStatus.Completed, ItemStatus.Completed, ItemStatus.Cancelled],
            Assert.IsType<Approvals>(finished.WorkflowData).Answers.Select(item => item.Status));
        EventAssert.Rejected(await Engine(second).ReplyAsync(outbox.TokenOf("cid"), new Answer { Approved = true }),
            EventRejection.WaitCancelled);
    }

    [Fact]
    [Trait("Spec", "E-05")]
    public async Task AParallelForEachChoosesACombinerForAProperty()
    {
        using var services = Services();
        var definition = Workflow.Create<Approvals>("CountedItems")
            .Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Approvers,
                (body, _) => body.Step<AddOne>(step => step
                    .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Total)
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Total)),
                setup => setup.Parallel().Combine<int, Sum<int>>(data => data.Total))
            .Build();

        var result = await Engine(services).RunAsync(definition, new Approvals { Approvers = ["ann", "bob", "cid"] });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(3, Assert.IsType<Approvals>(result.WorkflowData).Total);
        // A sequential ForEach has no join to merge in.
        Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Approvals>("SequentialChoice")
            .Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Approvers, (body, _) => body.Step<Noop>(),
                setup => setup.Combine<int, Sum<int>>(data => data.Total))
            .Build());
    }

    [Fact]
    public async Task ParallelForEachesNestInsideEachOtherAParallelBranchAndASequentialForEach()
    {
        using var services = Services();
        var nested = Workflow.Create<Approvals>("NestedFanOut")
            .Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Approvers,
                (outer, approver) => outer
                    .ForEach(_ => new[] { 1, 2 },
                        (inner, round) => inner.Step<Sign>(step => step
                            .Input(activity => activity.Name).From(ctx => ctx.Item(approver) + ctx.Item(round))
                            .Input(activity => activity.Signatures).From(ctx => ctx.WorkflowData.Signatures)
                            .Output(activity => activity.Signatures).To(ctx => ctx.WorkflowData.Signatures)),
                        setup => setup.Parallel())
                    .Result(ctx => ctx.WorkflowData.Signatures.Count),
                setup => setup.Parallel().Into(ctx => ctx.WorkflowData.Results))
            .Build();
        var inBranch = Workflow.Create<Approvals>("FanOutInABranch")
            .Step<Noop>()
            .Parallel(parallel => parallel
                .Do(list => list.ForEach(ctx => ctx.WorkflowData.Approvers,
                    (body, approver) => body.Step<Sign>(step => step
                        .Input(activity => activity.Name).From(ctx => ctx.Item(approver))
                        .Input(activity => activity.Signatures).From(ctx => ctx.WorkflowData.Signatures)
                        .Output(activity => activity.Signatures).To(ctx => ctx.WorkflowData.Signatures)),
                    setup => setup.Parallel(maxConcurrency: 1)))
                .Do(other => other.Step<Sign>(step => step
                    .Input(activity => activity.Name).From(_ => "other")
                    .Output(activity => activity.Name).To(ctx => ctx.WorkflowData.Last))))
            .Build();
        var inSequence = Workflow.Create<Approvals>("FanOutPerRound")
            .Step<Noop>()
            .ForEach(_ => new[] { "x", "y" },
                (rounds, round) => rounds.ForEach(ctx => ctx.WorkflowData.Approvers,
                    (body, approver) => body.Step<Sign>(step => step
                        .Input(activity => activity.Name).From(ctx => ctx.Item(round) + ctx.Item(approver))
                        .Input(activity => activity.Signatures).From(ctx => ctx.WorkflowData.Signatures)
                        .Output(activity => activity.Signatures).To(ctx => ctx.WorkflowData.Signatures)),
                    setup => setup.Parallel()))
            .Build();

        var nestedData = Data(await Engine(services).RunAsync(nested, new Approvals { Approvers = ["ann", "bob"] }));
        var branchData = Data(await Engine(services).RunAsync(inBranch, new Approvals { Approvers = ["ann", "bob"] }));
        var sequenceData = Data(await Engine(services).RunAsync(inSequence, new Approvals { Approvers = ["ann", "bob"] }));

        // Each outer item merged its own inner items, and saw only its own.
        Assert.Equal(["ann1", "ann2", "bob1", "bob2"], nestedData.Signatures);
        Assert.Equal([2, 2], nestedData.Results.Select(result => result.Value));
        Assert.Equal(["ann", "bob"], branchData.Signatures);
        Assert.Equal("other", branchData.Last);
        // Each round starts from the data the previous round's join left.
        Assert.Equal(["xann", "xbob", "yann", "ybob"], sequenceData.Signatures);
    }

    [Fact]
    public async Task OneAtATimeStillGivesEveryItemItsOwnCopy()
    {
        using var services = Services();
        var definition = Workflow.Create<Approvals>("OneAtATime")
            .Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Approvers,
                (body, approver) => body
                    .Step<Sign>(step => step
                        .Input(activity => activity.Name).From(ctx => ctx.Item(approver))
                        .Input(activity => activity.Signatures).From(ctx => ctx.WorkflowData.Signatures)
                        .Output(activity => activity.Signatures).To(ctx => ctx.WorkflowData.Signatures))
                    .Result(ctx => ctx.WorkflowData.Signatures.Count),
                setup => setup.Parallel(maxConcurrency: 1).Into(ctx => ctx.WorkflowData.Results))
            .Build();

        var data = Data(await Engine(services).RunAsync(definition, new Approvals { Approvers = ["ann", "bob", "cid"] }));

        // Unlike a sequential ForEach, the third item did not see the first two signatures.
        Assert.Equal([1, 1, 1], data.Results.Select(result => result.Value));
        Assert.Equal(["ann", "bob", "cid"], data.Signatures);
    }

    [Fact]
    [Trait("Spec", "E-09")]
    public async Task AConditionalJoinWhoseItemsConflictFailsAtOnceIntoTheCatch()
    {
        using var services = Services();
        var outbox = services.GetRequiredService<Outbox>();
        var definition = Workflow.Create<Approvals>("ConflictingVotes")
            .Step<Noop>()
            .Try(attempt => attempt.ForEach(ctx => ctx.WorkflowData.Approvers,
                (body, approver) => body
                    .Step<Sign>(step => step
                        .Input(activity => activity.Name).From(ctx => ctx.Item(approver))
                        .Output(activity => activity.Name).To(ctx => ctx.WorkflowData.Votes["final"].By))
                    .Request<Ask>("approval", step => step
                        .Input(activity => activity.Name).From(ctx => ctx.Item(approver)))
                    .WaitFor<Answer>("approval")
                    .Result(ctx => ctx.PreviousStep.Approved),
                setup => setup.Parallel()
                    .WaitConditionally<bool>((_, results) => results.Count >= 3)
                    .Into(ctx => ctx.WorkflowData.Answers)))
            .Catch<JoinConflictException>(handler => handler.Step<Sign>(step => step
                .Input(activity => activity.Name).From(_ => "conflict")
                .Output(activity => activity.Name).To(ctx => ctx.WorkflowData.Last)))
            .Build();
        var started = await Engine(services).RunAsync(definition,
            new Approvals { Approvers = ["ann", "bob", "cid"], Votes = { ["final"] = new Vote() } });

        await Engine(services).ReplyAsync(outbox.TokenOf("ann"), new Answer { Approved = true });
        // The second item's vote conflicts with the first's, so the join cannot hold and decides now.
        var finished = await Engine(services).ReplyAsync(outbox.TokenOf("bob"), new Answer { Approved = true });

        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        var data = Data(finished);
        Assert.Equal(("conflict", ""), (data.Last, data.Votes["final"].By));
        Assert.Empty(data.Answers);
        EventAssert.Rejected(await Engine(services).ReplyAsync(outbox.TokenOf("cid"), new Answer { Approved = true }),
            EventRejection.WaitCancelled);
        Assert.Equal(started.InstanceId, finished.InstanceId);
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

    private static ServiceProvider Services(IWorkflowStateRepository? store = null, Outbox? outbox = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (store != null)
            services.AddSingleton(store);
        services.AddSingleton(outbox ?? new Outbox());
        services.AddTransient<Ask>();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    /// <summary>A list and a threshold read from outside the workflow data, changed between hosts by tests.</summary>
    private static class Source
    {
        public static List<string> Approvers { get; set; } = [];
        public static int Needed { get; set; }
    }

    private static IWorkflowClient Engine(IServiceProvider services) => services.GetRequiredService<IWorkflowClient>();

    private static Approvals Data(WorkflowExecutionResult result) => Assert.IsType<Approvals>(result.WorkflowData);

    private static WorkflowDefinition CappedApprovals(string name) =>
        Workflow.Create<Approvals>(name)
            .Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Approvers,
                (body, approver) => body
                    .Request<Ask>("approval", step => step
                        .Input(activity => activity.Name).From(ctx => ctx.Item(approver)))
                    .WaitFor<Answer>("approval")
                    .Result(ctx => ctx.PreviousStep.Approved),
                setup => setup.Parallel(maxConcurrency: 2).Into(ctx => ctx.WorkflowData.Answers))
            .Build();

    // A host that dies in a commit fails the call that drove it.
    private static async Task IgnoreFailureAsync(Task call)
    {
        try
        {
            await call;
        }
        catch (Exception)
        {
            // The run's next host takes over from the last commit.
        }
    }

    public sealed class Approvals
    {
        public List<string> Approvers { get; set; } = [];

        [Combine<Append<string>>]
        public List<string> Signatures { get; set; } = [];

        public string Last { get; set; } = "";
        public int Total { get; set; }
        public Dictionary<string, Vote> Votes { get; set; } = [];
        public List<ItemResult<int>> Results { get; set; } = [];
        public List<ItemResult<bool>> Answers { get; set; } = [];
    }

    public sealed class Vote
    {
        public string By { get; set; } = "";
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

        /// <summary>The token of the latest request to <paramref name="name"/>, which a recovery may repeat.</summary>
        public string LastTokenOf(string name)
        {
            lock (_sent)
                return _sent.Last(item => item.Name == name).Token;
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

    /// <summary>Cleanup that is safe to run again when a host died before its outcome was saved.</summary>
    public sealed class Cleanup : IAsyncActivity, IRecoverableActivity<bool>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool CaptureRecoveryState(IActivityContext context) => true;

        public Task<ActivityRecoveryResult<bool>> RecoverAsync(bool state, IActivityContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Execute());
    }

    public sealed class AddOne : IAsyncActivity
    {
        public int Value { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Value++;
            return Task.CompletedTask;
        }
    }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

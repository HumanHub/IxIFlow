using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.ExecutionTests.ParallelForEachTests;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// An instance's state is stored as a trunk and parts (A-08): each branch, each <c>ForEach</c> list and stored result,
/// and the workflow data are parts, and a commit writes only those that changed. Stores read them back whole, at one
/// revision, or read the trunk and the data alone for a status.
/// </summary>
[Trait("Feature", "durability")]
[Trait("Spec", "A-08")]
public sealed class CheckpointPartsTests : IClassFixture<SqliteTestDatabase<CheckpointPartsTests>>
{
    [Fact]
    public Task TheMemoryStoreKeepsAnInstanceAsATrunkAndParts() =>
        StoresAnInstanceAsATrunkAndPartsAsync(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerKeepsAnInstanceAsATrunkAndParts() =>
        StoresAnInstanceAsATrunkAndPartsAsync(new SqlWorkflowStateRepository(SqlTestDatabase<CheckpointPartsTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlKeepsAnInstanceAsATrunkAndParts() =>
        StoresAnInstanceAsATrunkAndPartsAsync(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<CheckpointPartsTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteKeepsAnInstanceAsATrunkAndParts()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<CheckpointPartsTests>.ConnectionString);
        await StoresAnInstanceAsATrunkAndPartsAsync(repository);
    }

    [Fact]
    public Task AMemoryReadNeverMixesTwoCommits() =>
        ReadsNeverMixTwoCommitsAsync(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task ASqlServerReadNeverMixesTwoCommits() =>
        ReadsNeverMixTwoCommitsAsync(new SqlWorkflowStateRepository(SqlTestDatabase<CheckpointPartsTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task APostgreSqlReadNeverMixesTwoCommits() =>
        ReadsNeverMixTwoCommitsAsync(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<CheckpointPartsTests>.ConnectionString));

    // The finished items of a parallel ForEach are stored in chunks of a fixed number of items, so an item finishing
    // rewrites one small part however long the list is, and they read back whole.

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task ASqliteReadNeverMixesTwoCommits()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<CheckpointPartsTests>.ConnectionString);
        await ReadsNeverMixTwoCommitsAsync(repository);
    }

    // The finished items of a parallel ForEach are stored in chunks of a fixed number of items, so an item finishing
    // rewrites one small part however long the list is, and they read back whole.
    [Fact]
    public void FinishedItemsAreStoredInChunksAndAnItemChangesOne()
    {
        var root = new ContinuationState { Status = ContinuationStatus.Joining, Stack = [new ScopePosition { ScopeId = "root" }] };
        var state = new ForEachState
        {
            Items = Enumerable.Range(0, 2_000).Select(index => $"{index}").ToList(), Finished = [3, 600, 1_500]
        };
        root.Stack[0].ForEach = state;
        var checkpoint = new ExecutionCheckpoint { StructureHash = "finished-chunks", Continuations = [root] };
        var before = StoredCheckpoint.Split(checkpoint, "{}");

        state.Finished.Add(700);
        var after = StoredCheckpoint.Split(checkpoint, "{}");

        Assert.Equal([StoredCheckpoint.FinishedKey(state.Id, 1)],
            after.Parts.Where(part => before.Parts.GetValueOrDefault(part.Key) != part.Value).Select(part => part.Key));
        Assert.DoesNotContain("Finished\":", after.Trunk);
        var assembled = ExecutionCheckpoint.Read(StoredCheckpoint.AssembleState(after.Trunk, after.Parts));
        Assert.Equal([3, 600, 700, 1_500], assembled.Continuations[0].Stack[0].ForEach!.Finished!);
    }

    // A wait's deadline is the store's: the branch that holds the wait is stored without it, so the store's stamp
    // replacing the run's estimate does not change the branch's part.
    [Fact]
    public void ABranchIsStoredWithoutItsWaitsDeadlines()
    {
        var (state, data) = State(1, 1);
        var checkpoint = ExecutionCheckpoint.Read(state);
        var branch = checkpoint.Continuations[1];
        checkpoint.Waits.Add(new WaitState
        {
            ContinuationId = branch.Id, StepId = "root/0/branch/0/0", Key = "approval", EventType = "Approval",
            Timeout = TimeSpan.FromHours(1), DeadlineUtc = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc)
        });
        var saved = StoredCheckpoint.Split(checkpoint, data, marks: checkpoint.TakeMarks()).Parts;

        checkpoint.Waits[0].DeadlineUtc = checkpoint.Waits[0].DeadlineUtc!.Value.AddMilliseconds(40);
        var stamped = StoredCheckpoint.Split(checkpoint, data, saved, checkpoint.TakeMarks(), verify: true);

        Assert.DoesNotContain("DeadlineUtc", saved[StoredCheckpoint.BranchKey(branch.Id)]);
        Assert.Same(saved[StoredCheckpoint.BranchKey(branch.Id)], stamped.Parts[StoredCheckpoint.BranchKey(branch.Id)]);
        Assert.Contains("\"Timeout\"", saved[StoredCheckpoint.BranchKey(branch.Id)]);
    }

    // Branches come back in the order they started, whatever their keys, so a join merges them in that order after
    // a recovery as it would have without one.
    [Fact]
    public void BranchesAreAssembledInTheOrderTheyStarted()
    {
        var root = new ContinuationState { Status = ContinuationStatus.Joining, Stack = [new ScopePosition { ScopeId = "root" }] };
        var join = new ParallelJoinState { StepId = "root/0", ParentContinuationId = root.Id };
        // Their IDs sort against the order they started in.
        var started = new[] { "z-first", "y-second", "x-third" };
        var checkpoint = new ExecutionCheckpoint
        {
            StructureHash = "branch-order",
            BranchesStarted = 3,
            Continuations = [root, .. started.Select((id, index) => new ContinuationState
            {
                Id = id, ParentJoinId = join.Id, Sequence = index + 1,
                Stack = [new ScopePosition { ScopeId = "root/0/branch/0" }]
            })],
            Joins = [join]
        };
        var stored = StoredCheckpoint.Split(checkpoint, "{}");

        // A store may give the parts back in key order.
        var byKey = stored.Parts.OrderBy(part => part.Key, StringComparer.Ordinal)
            .ToDictionary(part => part.Key, part => part.Value);

        var assembled = ExecutionCheckpoint.Read(StoredCheckpoint.AssembleState(stored.Trunk, byKey));

        Assert.Equal(started, assembled.Continuations.Skip(1).Select(item => item.Id));
        Assert.Equal(started, Assert.Single(assembled.Joins).ChildContinuationIds);
    }

    // The parts an instance was read with belong to its revision: a commit made from the same object at a later
    // revision, after another commit the engine did not acknowledge to it, writes every part rather than trusting them.
    [Fact]
    public async Task ACommitFromAnInstanceWhosePartsAreOutOfDateWritesThemAll()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var (state, _) = State(1, 2);
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "OutOfDate", Status = WorkflowStatus.Running,
            ExecutionStateJson = state, WorkflowDataJson = "{\"Value\":\"A\"}"
        };
        await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(repository, instance.InstanceId)).Epoch, 0));
        var read = (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!;
        read.WorkflowDataJson = "{\"Value\":\"B\"}";
        await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(read, (await StoreTestSupport.LeaseAsync(repository, read.InstanceId)).Epoch, 1));

        read.Revision = 2;
        read.WorkflowDataJson = "{\"Value\":\"A\"}";
        var commit = CheckpointCommit.Create(read, (await StoreTestSupport.LeaseAsync(repository, read.InstanceId)).Epoch, 2);
        await repository.CommitWorkflowInstanceAsync(commit);

        Assert.True(commit.ReplacesParts);
        Assert.Equal("{\"Value\":\"A\"}", (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!.WorkflowDataJson);
    }

    // The parts an instance was read with belong to that instance: an object read for one instance and committed for
    // another at the same revision writes every part rather than leaving the other's.
    [Fact]
    public async Task ACommitFromAnInstanceReadAsAnotherWritesAllItsParts()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var (state, _) = State(1, 2);
        WorkflowInstance Instance(string data) => new()
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Renamed", Status = WorkflowStatus.Running,
            ExecutionStateJson = state, WorkflowDataJson = data
        };
        var first = Instance("{\"Value\":\"A\"}");
        var second = Instance("{\"Value\":\"B\"}");
        await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(first, (await StoreTestSupport.LeaseAsync(repository, first.InstanceId)).Epoch, 0));
        await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(second, (await StoreTestSupport.LeaseAsync(repository, second.InstanceId)).Epoch, 0));
        var read = (await repository.GetWorkflowInstanceAsync(first.InstanceId))!;

        read.InstanceId = second.InstanceId;
        read.Incarnation = second.Incarnation;
        var commit = CheckpointCommit.Create(read, (await StoreTestSupport.LeaseAsync(repository, read.InstanceId)).Epoch, 1);
        await repository.CommitWorkflowInstanceAsync(commit);

        Assert.True(commit.ReplacesParts);
        Assert.Equal("{\"Value\":\"A\"}", (await repository.GetWorkflowInstanceAsync(second.InstanceId))!.WorkflowDataJson);
    }

    // The diagnostic option reaches the limits every commit is made under.
    [Fact]
    public void TheVerifyingOptionReachesTheExecutionLimits()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow(options => options.VerifyCheckpointParts = true);
        using var provider = services.BuildServiceProvider();

        Assert.True(provider.GetRequiredService<ExecutionLimits>().VerifyCheckpointParts);
    }

    // A commit serializes only the branches marked as changed; the verifying mode the tests run with fails a commit
    // whose branch changed without a mark, and a marked branch is written again.
    [Fact]
    public void TheVerifyingModeFailsACommitWhoseBranchChangedWithoutAMark()
    {
        var (state, data) = State(1, 2);
        var checkpoint = ExecutionCheckpoint.Read(state);
        var saved = StoredCheckpoint.Split(checkpoint, data, marks: checkpoint.TakeMarks()).Parts;
        var branch = checkpoint.Continuations[1];

        branch.Status = ContinuationStatus.Active;

        var unmarked = Assert.Throws<CheckpointVerificationException>(() =>
            StoredCheckpoint.Split(checkpoint, data, saved, checkpoint.TakeMarks(), verify: true));
        Assert.Contains(StoredCheckpoint.BranchKey(branch.Id), unmarked.Message);
        checkpoint.Touch(branch);
        var marked = StoredCheckpoint.Split(checkpoint, data, saved, checkpoint.TakeMarks(), verify: true);
        Assert.NotEqual(saved[StoredCheckpoint.BranchKey(branch.Id)], marked.Parts[StoredCheckpoint.BranchKey(branch.Id)]);
        Assert.Same(saved[StoredCheckpoint.BranchKey(checkpoint.Continuations[2].Id)],
            marked.Parts[StoredCheckpoint.BranchKey(checkpoint.Continuations[2].Id)]);
    }

    // A list is written once, under its own ForEach run: a second ForEach in the same frame saves its own list
    // rather than finding the first one's under the frame.
    [Fact]
    public async Task ASecondForEachInTheSameFrameSavesItsOwnList()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository, new Outbox());
        var definition = Workflow.Create<Approvals>("TwoListsOneFrame")
            .Step<Noop>()
            .ForEach(ctx => ctx.WorkflowData.Signatures, (body, _) => body.Step<Noop>())
            .ForEach(ctx => ctx.WorkflowData.Approvers, (body, _) => body.WaitFor<Answer>("go"))
            .Build();

        var started = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition,
            new Approvals { Signatures = ["first", "second"], Approvers = ["ann", "bob", "cid"] });

        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var whole = (await repository.GetWorkflowInstanceAsync(started.InstanceId))!;
        var list = Assert.Single(ExecutionCheckpoint.Read(whole.ExecutionStateJson).Continuations[0].Stack,
            frame => frame.ForEach != null).ForEach!;
        Assert.Equal(["\"ann\"", "\"bob\"", "\"cid\""], list.Items);
    }

    // A value is serialized when it is set, not at each commit, so a branch that has not moved is not rewritten even
    // when its value would serialize differently each time.
    [Fact]
    public void APreviousValueIsSerializedWhenItIsSetNotAtEachCommit()
    {
        var definition = Workflow.Create<Approvals>("PreviousOnce").Step<Noop>().Step<Noop>().Build();
        var scopes = new WorkflowScopeCatalog(definition);
        var continuation = new ContinuationState { Stack = [new ScopePosition { ScopeId = "root" }] };
        var checkpoint = new ExecutionCheckpoint { Continuations = [continuation] };
        continuation.SetPrevious(new Stamped());

        ContinuationTransitions.PrepareCheckpoint(checkpoint, scopes);
        var saved = continuation.Previous!.Json;
        ContinuationTransitions.PrepareCheckpoint(checkpoint, scopes);

        Assert.Same(saved, continuation.Previous!.Json);
        continuation.SetPrevious(new Stamped());
        ContinuationTransitions.PrepareCheckpoint(checkpoint, scopes);
        Assert.NotEqual(saved, continuation.Previous!.Json);
    }

    // Three approvers, two at a time: branches, a list and results are parts; once the instance ends, only its data is.
    private static async Task StoresAnInstanceAsATrunkAndPartsAsync(IWorkflowStateRepository repository)
    {
        var outbox = new Outbox();
        using var services = Services(repository, outbox);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Approvals($"StoredApprovals{Guid.NewGuid():N}");
        var started = await engine.RunAsync(definition, new Approvals { Approvers = ["ann", "bob", "cid"] });
        var instanceId = started.InstanceId;
        try
        {
            await engine.ReplyAsync(outbox.TokenOf("bob"), new Answer { Approved = true });

            var whole = (await repository.GetWorkflowInstanceAsync(instanceId))!;
            var checkpoint = ExecutionCheckpoint.Read(whole.ExecutionStateJson);
            Assert.Equal(2, checkpoint.Continuations.Count(item => item.ParentJoinId != null &&
                                                                   item.Status == ContinuationStatus.Waiting));
            var list = checkpoint.Continuations[0].Stack[^1].ForEach!;
            Assert.Equal(3, list.Items.Count);
            Assert.NotNull(list.Results![1]);
            Assert.Equal([1], list.Finished!);
            // Each join lists its branches again, in the order they started.
            var join = Assert.Single(checkpoint.Joins);
            Assert.Equal(checkpoint.Continuations.Where(item => item.ParentJoinId == join.Id).Select(item => item.Id),
                join.ChildContinuationIds);
            Assert.Contains(whole.SavedParts!.Keys, key => key.StartsWith("f/", StringComparison.Ordinal));
            Assert.Contains(whole.SavedParts!.Keys, key => key.StartsWith("r/", StringComparison.Ordinal));
            // Split again, what was read gives the parts it was read from, so a recovery rewrites nothing that did not move.
            Assert.Equal(whole.SavedParts!.OrderBy(part => part.Key),
                StoredCheckpoint.Split(checkpoint, whole.WorkflowDataJson).Parts.OrderBy(part => part.Key));

            // A summary has the data and the trunk, which can say what stopped the instance but cannot run it.
            var summary = (await repository.GetWorkflowInstanceSummaryAsync(instanceId))!;
            Assert.Equal(whole.Revision, summary.Revision);
            Assert.Equal(whole.WorkflowDataJson, summary.WorkflowDataJson);
            Assert.Throws<WorkflowEngineException>(() => ExecutionCheckpoint.Read(summary.ExecutionStateJson));
            Assert.Single(ExecutionCheckpoint.ReadTrunk(summary.ExecutionStateJson).Continuations);

            await engine.ReplyAsync(outbox.TokenOf("ann"), new Answer { Approved = true });
            var finished = await engine.ReplyAsync(outbox.TokenOf("cid"), new Answer { Approved = false });

            Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
            var ended = (await repository.GetWorkflowInstanceAsync(instanceId))!;
            Assert.Equal([CheckpointParts.DataKey], ended.SavedParts!.Keys);
            Assert.Equal([true, true, false],
                JsonSerializer.Deserialize<Approvals>(ended.WorkflowDataJson)!.Answers.Select(item => item.Value));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
        Assert.Null(await repository.GetWorkflowInstanceAsync(instanceId));
        if (repository is SqlWorkflowStateRepository)
        {
            await using var connection = new SqlConnection(SqlTestDatabase<CheckpointPartsTests>.ConnectionString);
            await connection.OpenAsync();
            await using var count = new SqlCommand(
                "SELECT COUNT(*) FROM dbo.IxIFlowWorkflowParts WHERE InstanceId = @InstanceId", connection);
            count.Parameters.AddWithValue("@InstanceId", instanceId);
            Assert.Equal(0, (int)(await count.ExecuteScalarAsync())!);
        }
    }

    // One owner commits two states in turn, each with its own branches, while another reads: every read is one
    // state or the other, never branches of both. Every tenth commit replaces all parts.
    private static async Task ReadsNeverMixTwoCommitsAsync(IWorkflowStateRepository repository)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        var states = new[] { State(1, 5), State(6, 5) };
        var expected = states.Select(state => Branches(state.ExecutionStateJson)).ToArray();
        var instance = new WorkflowInstance
        { ExecutionStateJson = StoreTestSupport.CheckpointJson,
            InstanceId = instanceId, WorkflowName = "MixedReads", Status = WorkflowStatus.Running
        };
        try
        {
            using var stop = new CancellationTokenSource();
            var reads = 0;
            var reader = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (await repository.GetWorkflowInstanceAsync(instanceId) is not { } read)
                        continue;
                    var branches = Branches(read.ExecutionStateJson);
                    Assert.True(expected.Any(state => state.SetEquals(branches)),
                        $"Read branches {string.Join(",", branches)} at revision {read.Revision}");
                    Assert.Equal(read.ExecutionStateJson.Contains("\"BranchesStarted\":5") ? "{\"First\":true}" : "{\"First\":false}",
                        read.WorkflowDataJson);
                    reads++;
                }
            });
            for (var revision = 0; revision < 60; revision++)
            {
                var state = states[revision % 2];
                instance.ExecutionStateJson = state.ExecutionStateJson;
                instance.WorkflowDataJson = state.WorkflowDataJson;
                instance.Revision = revision;
                var commit = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(repository, instance.InstanceId)).Epoch, revision);
                Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(commit)).Status);
                // The parts are known for the next revision, except before every tenth, which replaces them all.
                if ((revision + 1) % 10 != 0)
                    instance.Saved(commit.StoredParts, revision + 1);
            }
            stop.Cancel();
            await reader;
            Assert.True(reads > 0);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    // A root waiting on a join with <paramref name="count"/> branches numbered from <paramref name="first"/>.
    private static (string ExecutionStateJson, string WorkflowDataJson) State(int first, int count)
    {
        var root = new ContinuationState { Status = ContinuationStatus.Joining, Stack = [new ScopePosition { ScopeId = "root" }] };
        var join = new ParallelJoinState { StepId = "root/0", ParentContinuationId = root.Id };
        var checkpoint = new ExecutionCheckpoint
        {
            StructureHash = "mixed-reads",
            BranchesStarted = first + count - 1,
            Continuations = [root, .. Enumerable.Range(first, count).Select(sequence => new ContinuationState
            {
                Id = $"branch-{sequence}",
                ParentJoinId = join.Id,
                Sequence = sequence,
                Status = ContinuationStatus.Waiting,
                Stack = [new ScopePosition { ScopeId = "root/0/branch/0" }]
            })],
            Joins = [join]
        };
        return (JsonSerializer.Serialize(checkpoint), first == 1 ? "{\"First\":true}" : "{\"First\":false}");
    }

    private static HashSet<string> Branches(string state) => ExecutionCheckpoint.Read(state).Continuations
        .Where(item => item.ParentJoinId != null).Select(item => item.Id).ToHashSet();

    private static WorkflowDefinition Approvals(string name) =>
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

    private static ServiceProvider Services(IWorkflowStateRepository store, Outbox outbox)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddSingleton(outbox);
        services.AddTransient<Ask>();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    /// <summary>A value that serializes differently each time.</summary>
    public sealed class Stamped
    {
        public string Stamp
        {
            get => Guid.NewGuid().ToString("N");
            set { }
        }
    }
}

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
public sealed class CheckpointPartsTests
{
    [Fact]
    public Task TheMemoryStoreKeepsAnInstanceAsATrunkAndParts() =>
        StoresAnInstanceAsATrunkAndPartsAsync(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerKeepsAnInstanceAsATrunkAndParts() =>
        StoresAnInstanceAsATrunkAndPartsAsync(new SqlWorkflowStateRepository(SqlServerTestServer.ConnectionString));

    [Fact]
    public Task AMemoryReadNeverMixesTwoCommits() =>
        ReadsNeverMixTwoCommitsAsync(new InMemoryWorkflowStateRepository());

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task ASqlServerReadNeverMixesTwoCommits() =>
        ReadsNeverMixTwoCommitsAsync(new SqlWorkflowStateRepository(SqlServerTestServer.ConnectionString));

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
            await using var connection = new SqlConnection(SqlServerTestServer.ConnectionString);
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
        {
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
                if (revision % 10 == 0)
                    instance.SavedParts = null;
                var commit = CheckpointCommit.Create(instance, 0, revision);
                Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(commit)).Status);
                instance.SavedParts = commit.StoredParts;
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
            DefinitionFingerprint = "mixed-reads",
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

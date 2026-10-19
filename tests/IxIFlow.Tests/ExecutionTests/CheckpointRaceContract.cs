using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.ExecutionTests;

internal static class CheckpointRaceContract
{
    internal static WorkflowInstance Instance(string id, string marker)
    {
        var root = new ContinuationState { Id = "root", Status = ContinuationStatus.Joining,
            Stack = [new ScopePosition { ScopeId = "root" }] };
        var join = new ParallelJoinState { Id = "join", StepId = "root/0", ParentContinuationId = root.Id };
        var state = new ExecutionCheckpoint { DefinitionFingerprint = marker, BranchesStarted = 1,
            Continuations = [root, new ContinuationState { Id = "branch-" + marker,
                ParentJoinId = join.Id, Sequence = 1, Status = ContinuationStatus.Waiting,
                Stack = [new ScopePosition { ScopeId = "root/0/branch/0" }] }], Joins = [join] };
        return new WorkflowInstance { InstanceId = id, WorkflowName = "CheckpointRace", Status = WorkflowStatus.Running,
            ExecutionStateJson = JsonSerializer.Serialize(state), WorkflowDataJson = JsonSerializer.Serialize(new { marker }) };
    }

    internal static async Task ConcurrentWritersPersistOnlyTheWinner(Func<IWorkflowStateRepository> repository, int revision)
    {
        var stores = Enumerable.Range(0, 6).Select(_ => repository()).ToArray();
        // Initialize every independent repository before the barrier; schema creation is not the race.
        foreach (var store in stores) await store.GetWorkflowInstanceAsync("warm-up");
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var seed = Instance(id, "seed");
            if (revision > 0)
                Assert.Equal(WorkflowCommitStatus.Applied, (await stores[0].CommitWorkflowInstanceAsync(
                    CheckpointCommit.Create(seed, 0, 0))).Status);
            // Contenders after the first commit name the seeded row's incarnation.
            var commits = stores.Select((_, index) =>
            {
                var writer = Instance(id, "writer-" + index);
                writer.Incarnation = seed.Incarnation;
                return CheckpointCommit.Create(writer, 0, revision, "writer-" + index);
            }).ToArray();
            Assert.All(commits, c => Assert.True(c.Parts.Count >= 2, "The race must write branch and data parts"));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = stores.Select((store, index) => Task.Run(async () =>
            { await gate.Task; return await store.CommitWorkflowInstanceAsync(commits[index]); })).ToArray();
            gate.SetResult();
            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
            var winner = Assert.Single(Enumerable.Range(0, results.Length), index => results[index].Status == WorkflowCommitStatus.Applied);
            Assert.Equal(stores.Length - 1, results.Count(r => r.Status == WorkflowCommitStatus.Conflict));
            Assert.All(results, result => Assert.Equal(revision + 1, result.Revision));
            foreach (var store in stores)
            {
                var saved = (await store.GetWorkflowInstanceAsync(id))!;
                var expected = commits[winner].ReadInstance();
                Assert.Equal(revision + 1, saved.Revision);
                Assert.Equal(expected.WorkflowDataJson, saved.WorkflowDataJson);
                Assert.Equal(expected.ExecutionStateJson, saved.ExecutionStateJson);
                Assert.Equal(commits[winner].StoredParts.OrderBy(p => p.Key),
                    CheckpointCommit.Create(saved, 0, saved.Revision).StoredParts.OrderBy(p => p.Key));
            }
        }
        finally { await stores[0].DeleteWorkflowInstanceAsync(id); }
    }
}

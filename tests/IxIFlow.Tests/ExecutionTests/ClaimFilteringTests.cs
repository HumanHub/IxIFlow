using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "C-02")]
[Trait("Spec", "C-08")]
[Trait("Spec", "C-09")]
public sealed class ClaimFilteringTests
{
    [Fact]
    public Task MemoryClaimContract() => Contract(new InMemoryWorkflowStateRepository());

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteClaimContract()
    {
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<ClaimFilteringTests>.ConnectionString);
        await Contract(store);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerClaimContract() => Contract(new SqlWorkflowStateRepository(SqlTestDatabase<ClaimFilteringTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlClaimContract() => Contract(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<ClaimFilteringTests>.ConnectionString));

    private static ClaimQuery Host(Guid id, params (string Key, string Value)[] capabilities) =>
        new(id, capabilities.Append(("host", id.ToString("D"))).ToDictionary(item => item.Item1, item => item.Item2));

    private static async Task Contract(IWorkflowStateRepository store)
    {
        var first = Host(Guid.NewGuid(), ("gpu", "cuda"), ("region", "eu"));
        var second = Host(Guid.NewGuid(), ("gpu", "cuda"), ("region", "eu"));
        var missing = Host(Guid.NewGuid(), ("notgpu", "cuda"), ("Region", "eu"));
        var wrongValue = Host(Guid.NewGuid(), ("gpu", "CUDA"), ("region", "us"));
        var ids = new List<string>();
        async Task<WorkflowInstance> Seed(WorkflowDemand[] demands, ClaimQuery owner, TimeSpan? park = null,
            WorkflowStatus status = WorkflowStatus.Running)
        {
            var instance = new WorkflowInstance
            {
                InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "ClaimContract", Status = status,
                WorkflowDataJson = "{}", WorkflowDataType = IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(object)),
                ExecutionStateJson = JsonSerializer.Serialize(new ExecutionCheckpoint
                {
                    StructureHash = "claim-contract", Demands = demands.ToList(),
                    Continuations = [new() { Id = "root", Stack = [new() { ScopeId = "root" }] }]
                })
            };
            ids.Add(instance.InstanceId);
            var grant = Assert.IsType<ExecutionLeaseGrant>(await store.TryAcquireExecutionLeaseAsync(
                instance.InstanceId, "seed", TimeSpan.FromMinutes(1), owner));
            var result = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch, 0,
                parks: park != null, wakeAfter: park));
            Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
            await store.ReleaseExecutionLeaseAsync(instance.InstanceId, "seed");
            return (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        }

        try
        {
            var required = await Seed([new("gpu"), new("region", "eu")], first);
            Assert.Contains(await store.GetRunnableInstancesAsync(first), row => row.InstanceId == required.InstanceId);
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(missing), row => row.InstanceId == required.InstanceId);
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(wrongValue), row => row.InstanceId == required.InstanceId);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(required.InstanceId, "wrong", TimeSpan.FromMinutes(1), missing));
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(required.InstanceId, "value", TimeSpan.FromMinutes(1), wrongValue));
            var folded = new ClaimQuery(first.HostId, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { ["GPU"] = "cuda", ["region"] = "eu" });
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(folded), row => row.InstanceId == required.InstanceId);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(required.InstanceId, "folded", TimeSpan.FromMinutes(1), folded));
            var largeCapabilities = first.Capabilities.ToDictionary(item => item.Key, item => item.Value);
            for (var index = 0; index < 512; index++) largeCapabilities.Add("extra-" + index, "value");
            var large = new ClaimQuery(first.HostId, largeCapabilities);
            Assert.Equal((await store.GetRunnableInstancesAsync(first)).Select(row => row.InstanceId),
                (await store.GetRunnableInstancesAsync(large)).Select(row => row.InstanceId));
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(required.InstanceId, "large", TimeSpan.FromMinutes(1), large));
            await store.ReleaseExecutionLeaseAsync(required.InstanceId, "large");
            var empty = new ClaimQuery(first.HostId, new Dictionary<string, string>());
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(empty), row => row.InstanceId == required.InstanceId);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(required.InstanceId, "empty", TimeSpan.FromMinutes(1), empty));

            // Limiting due rows before demand evaluation would hide this later match.
            for (var index = 0; index < 33; index++) await Seed([new("unavailable")], first);
            var later = await Seed([new("later", ""), new("key-only"),
                new("expired", "different", DateTime.UtcNow.AddHours(-1))], first);
            var laterHost = Host(first.HostId, ("later", ""), ("key-only", ""));
            var laterScan = await store.GetRunnableInstancesAsync(new ClaimQuery(first.HostId, laterHost.Capabilities, 1));
            Assert.Equal(later.InstanceId, Assert.Single(laterScan).InstanceId);
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(later.InstanceId, "later", TimeSpan.FromMinutes(1), laterHost));
            await store.ReleaseExecutionLeaseAsync(later.InstanceId, "later");
            var spacedKey = await Seed([new("gpu ")], first);
            var spacedValue = await Seed([new("gpu", "cuda ")], first);
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(first), row =>
                row.InstanceId == spacedKey.InstanceId || row.InstanceId == spacedValue.InstanceId);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(spacedKey.InstanceId, "spaced-key", TimeSpan.FromMinutes(1), first));
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(spacedValue.InstanceId, "spaced-value", TimeSpan.FromMinutes(1), first));
            var matchingSpace = Host(first.HostId, ("gpu", "cuda "));
            Assert.Contains(await store.GetRunnableInstancesAsync(matchingSpace), row => row.InstanceId == spacedValue.InstanceId);
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(spacedValue.InstanceId, "matching-space", TimeSpan.FromMinutes(1), matchingSpace));
            await store.ReleaseExecutionLeaseAsync(spacedValue.InstanceId, "matching-space");
            var twoKeys = Host(Guid.NewGuid(), ("gpu", "cuda"), ("gpu ", "cuda"));
            Assert.Contains(await store.GetRunnableInstancesAsync(twoKeys), row => row.InstanceId == spacedKey.InstanceId);
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(spacedKey.InstanceId, "two-keys", TimeSpan.FromMinutes(1), twoKeys));
            await store.ReleaseExecutionLeaseAsync(spacedKey.InstanceId, "two-keys");
            // The pin cannot override a missing capability, and another matching host cannot take a live pin.
            var pinned = await Seed([new("gpu"), new("host", first.HostId.ToString("D"), DateTime.UtcNow.AddHours(1))], first);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(pinned.InstanceId, "other", TimeSpan.FromMinutes(1), second));
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(pinned.InstanceId, "pin-only", TimeSpan.FromMinutes(1),
                Host(first.HostId)));
            var expired = await Seed([new("gpu"), new("host", first.HostId.ToString("D"), DateTime.UnixEpoch)], first);
            Assert.Contains(await store.GetRunnableInstancesAsync(second), row => row.InstanceId == expired.InstanceId);
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(expired.InstanceId, "expired", TimeSpan.FromMinutes(1), second));
            await store.ReleaseExecutionLeaseAsync(expired.InstanceId, "expired");

            // A stale scan, direct recovery or resume cannot acquire an instance another owner reparked.
            var grant = Assert.IsType<ExecutionLeaseGrant>(await store.TryAcquireExecutionLeaseAsync(required.InstanceId,
                "park", TimeSpan.FromMinutes(1), second));
            var current = (await store.GetWorkflowInstanceAsync(required.InstanceId))!;
            Assert.Equal(second.HostId, current.LastHostId);
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(
                current, grant.Epoch, current.Revision, parks: true, wakeAfter: TimeSpan.FromHours(1)))).Status);
            Assert.Equal(second.HostId, (await store.GetWorkflowInstanceSummaryAsync(required.InstanceId))!.LastHostId);
            foreach (var kind in Enum.GetValues<ExecutionClaimKind>())
                Assert.Null(await store.TryAcquireExecutionLeaseAsync(required.InstanceId, "early", TimeSpan.FromMinutes(1), first, kind));
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(first), row => row.InstanceId == required.InstanceId);

            // An idle operator claim still applies demands; a normal recovery cannot enter the idle row.
            var resolution = await Seed([new("gpu")], first, status: WorkflowStatus.NeedsResolution);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(resolution.InstanceId, "resolution-wrong", TimeSpan.FromMinutes(1), missing, ExecutionClaimKind.Operator));
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(resolution.InstanceId, "recovery-idle", TimeSpan.FromMinutes(1), first));
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(resolution.InstanceId, "resolution", TimeSpan.FromMinutes(1), second, ExecutionClaimKind.Operator));
            await store.ReleaseExecutionLeaseAsync(resolution.InstanceId, "resolution");

            // An arrival racing the commit into resolution must keep another branch reachable.
            var racing = await Seed([], first);
            var racingOwner = (await store.TryAcquireExecutionLeaseAsync(racing.InstanceId, "racing",
                TimeSpan.FromMinutes(1), first))!;
            var opened = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(racing, racingOwner.Epoch,
                racing.Revision, openedWaits: [new("event", WaitKind.Event, "event", "Event", null)]));
            Assert.Equal(WorkflowCommitStatus.Applied, opened.Status);
            await store.AppendEventAsync(new WorkflowEvent(racing.InstanceId, "arrival", "Event", "{}", "event"));
            racing.Revision = opened.Revision;
            racing.Status = WorkflowStatus.NeedsResolution;
            var unresolved = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(racing, racingOwner.Epoch,
                racing.Revision, inboxReadThrough: 0, wakeAfter: TimeSpan.FromHours(1)));
            Assert.Equal(WorkflowCommitStatus.Applied, unresolved.Status);
            Assert.True(unresolved.UnreadEvents);
            Assert.Contains(await store.GetRunnableInstancesAsync(first), row => row.InstanceId == racing.InstanceId);

            // Preference never blocks another host: own rows first, each group ordered by due time and ID.
            var older = await Seed([], second);
            var own = await Seed([], first);
            var child = new WorkflowInstance { InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "NewChild",
                Status = WorkflowStatus.Running, ParentInstanceId = own.InstanceId, ParentWaitId = "child-wait",
                ExecutionStateJson = StoreTestSupport.CheckpointJson };
            ids.Add(child.InstanceId);
            var parentGrant = (await store.TryAcquireExecutionLeaseAsync(own.InstanceId, "child-parent", TimeSpan.FromMinutes(1), first))!;
            own = (await store.GetWorkflowInstanceAsync(own.InstanceId))!;
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(
                own, parentGrant.Epoch, own.Revision, childStarts: [new(child.InstanceId, JsonSerializer.Serialize(child), "child-wait")],
                openedWaits: [new("child-wait", WaitKind.Child, "", "IxIFlow.ChildCompleted", null)]))).Status);
            await store.ReleaseExecutionLeaseAsync(own.InstanceId, "child-parent");
            Assert.Null((await store.GetWorkflowInstanceSummaryAsync(child.InstanceId))!.LastHostId);
            var rows = await store.GetRunnableInstancesAsync(first);
            Assert.Contains(rows, row => row.InstanceId == child.InstanceId);
            Assert.True(rows.ToList().FindIndex(row => row.InstanceId == own.InstanceId) <
                        rows.ToList().FindIndex(row => row.InstanceId == older.InstanceId));
            var instances = await Task.WhenAll(rows.Select(row => store.GetWorkflowInstanceSummaryAsync(row.InstanceId)));
            var expected = instances.OrderByDescending(row => row!.LastHostId == first.HostId)
                .ThenBy(row => row!.RunnableAtUtc).ThenBy(row => row!.InstanceId, StringComparer.Ordinal)
                .Select(row => row!.InstanceId);
            Assert.Equal(expected, rows.Select(row => row.InstanceId));
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(own.InstanceId, "no-wait", TimeSpan.FromMinutes(1), second));
            await store.ReleaseExecutionLeaseAsync(own.InstanceId, "no-wait");
            Assert.Empty(await store.GetRunnableInstancesAsync(new ClaimQuery(first.HostId, first.Capabilities, 0)));
        }
        finally { foreach (var id in ids) await store.DeleteWorkflowInstanceAsync(id); }
    }

    [Fact]
    public async Task PinExpiryUsesTheStoreClockAtTheClaim()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var first = Host(Guid.NewGuid());
        var second = Host(Guid.NewGuid());
        var instance = new WorkflowInstance { InstanceId = "pin-clock", Status = WorkflowStatus.Running,
            ExecutionStateJson = JsonSerializer.Serialize(new ExecutionCheckpoint { StructureHash = "pin",
                Demands = [new("host", first.HostId.ToString("D"), time.GetUtcNow().UtcDateTime.AddMinutes(1))] }) };
        var grant = (await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "seed", TimeSpan.FromMinutes(1), first))!;
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, grant.Epoch, 0, parks: true, wakeAfter: TimeSpan.Zero))).Status);
        Assert.Null(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "early", TimeSpan.FromMinutes(1), second));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "due", TimeSpan.FromMinutes(1), second));
    }
}


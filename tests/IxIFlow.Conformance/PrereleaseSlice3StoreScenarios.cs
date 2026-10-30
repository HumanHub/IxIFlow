using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "durability")]
public sealed class PrereleaseSlice3StoreScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RuntimeIdentifiersRefusePaddingAndNulBeforeStoreAccess(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        foreach (var invalid in new[] { " padded", "padded ", "a\0b", "\tpadded", "padded\u00a0" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.GetWorkflowInstanceAsync(invalid));
            var valid = new WorkflowEvent("absent", "event", "type", "{}", "key", "wait", "subject");

            foreach (var item in new[]
                     {
                         valid with { InstanceId = invalid }, valid with { EventId = invalid },
                         valid with { EventType = invalid }, valid with { Key = invalid }, valid with { TargetWaitId = invalid },
                         valid with { Subject = invalid }
                     })
                await Assert.ThrowsAsync<ArgumentException>(() => store.AppendEventAsync(item));

            var instance = Instance("valid");
            Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, 1, 0, commitId: invalid));
            instance.CorrelationId = invalid;
            Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, 1, 0));
            instance.CorrelationId = "";

            foreach (var wait in new[]
                     {
                         new OpenedWait(invalid, WaitKind.Event, "key", "type", null),
                         new OpenedWait("wait", WaitKind.Event, invalid, "type", null),
                         new OpenedWait("wait", WaitKind.Event, "key", invalid, null)
                     })
                Assert.Throws<ArgumentException>(() => CheckpointCommit.Create(instance, 1, 0, openedWaits: [wait]));
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task LatestPublicationUsesGenerationAndDescendingRevisionAndAttempt(string profile)
    {
        var clock = new FakeTimeProvider();
        await using var database = await StoreProfiles.CreateAsync(profile, clock);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Latest", 1, "workflow", "source"), null);
        Assert.Null(await store.GetLatestPublicationAsync(draft.Kind, draft.Name, 1, draft.Generation, CatalogScope.Unrestricted));
        WorkflowPublication? latest = null;

        for (var revision = 0; revision < 3; revision++)
        {
            if (revision > 0)
                draft = await store.SaveDefinitionAsync(draft with { Source = "revision " + revision }, draft.Id);

            for (var retry = 0; retry < 3; retry++)
            {
                latest = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), null, TimeSpan.FromMinutes(1));
                await store.CompletePublicationAsync(latest.Id, latest.HostId, new(null, [], WorkflowPublishDiagnosticClass.Authoring));
            }
        }

        Assert.Equal(latest!.Id,
            (await store.GetLatestPublicationAsync(draft.Kind, draft.Name, 1, draft.Generation, CatalogScope.Unrestricted))!.Id);

        Assert.Null(await store.GetLatestPublicationAsync(draft.Kind, draft.Name, 1, Guid.NewGuid(), CatalogScope.Unrestricted));

        Assert.Null(await store.GetLatestPublicationAsync(draft.Kind, draft.Name, 1, draft.Generation,
            new CatalogScope(new([]), new([]), new([]), new([]))));
    }

    // The server profiles run this same commit above SQL Server's 2,100-parameter ceiling.
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ChildStartsExceedTheSqlServerParameterCeilingInOneCommit(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var parent = Instance("large-parent");
        var grant = await StoreTestSupport.LeaseAsync(store, parent.InstanceId);

        var children = Enumerable.Range(0, 2105).Select(index =>
        {
            var child = Instance("child-" + index);
            child.ParentInstanceId = parent.InstanceId;
            child.ParentWaitId = "wait-" + index;

            return new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), child.ParentWaitId);
        }).ToArray();

        var waits = children.Select(child => new OpenedWait(child.ParentWaitId, WaitKind.Child, "", "child", null)).ToArray();

        var reply = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, grant.Epoch, 0,
            childStarts: children, openedWaits: waits));

        Assert.Equal(WorkflowCommitStatus.Applied, reply.Status);
        Assert.NotNull(await store.GetWorkflowInstanceAsync(children[0].ChildInstanceId));
        Assert.NotNull(await store.GetWorkflowInstanceAsync(children[^1].ChildInstanceId));
        var collisionParent = Instance("collision-parent");
        var collisionGrant = await StoreTestSupport.LeaseAsync(store, collisionParent.InstanceId);

        var collisionChildren = children.Select((child, index) =>
        {
            var candidate = Instance(index == children.Length - 1 ? child.ChildInstanceId : "collision-child-" + index);
            candidate.ParentInstanceId = collisionParent.InstanceId;
            candidate.ParentWaitId = child.ParentWaitId;

            return new ChildStart(candidate.InstanceId, JsonSerializer.Serialize(candidate), candidate.ParentWaitId);
        }).ToArray();

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(collisionParent, collisionGrant.Epoch, 0, childStarts: collisionChildren, openedWaits: waits)));

        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Null(await store.GetWorkflowInstanceAsync(collisionParent.InstanceId));
        Assert.Null(await store.GetWorkflowInstanceAsync(collisionChildren[0].ChildInstanceId));
    }

    public static TheoryData<string> Relational => new(StoreProfiles.ProfileNames.Where(profile => profile != StoreProfiles.LocalMemory));

    [Theory, MemberData(nameof(Relational))]
    public async Task PartialControlReadsKeepHealthyRowsAndExposeTheirFailures(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.SeedAsync(Instance("healthy"));
        await store.SeedAsync(Instance("broken"));
        await using var connection = database.NewConnection();
        await connection.OpenAsync();

        await connection.ExecuteAsync(profile.StartsWith(StoreProfiles.DistributedSqlServer, StringComparison.Ordinal)
            ? "UPDATE dbo.IxIFlowWorkflowInstances SET CancellationJson='{' WHERE InstanceId='broken'"
            : "UPDATE ixiflow_workflow_instances SET cancellation_json='{' WHERE instance_id='broken'");

        var read = await store.GetControlRequestsAsync(["healthy", "broken"]);
        Assert.Contains("healthy", read.DebugStates.Keys);
        Assert.DoesNotContain("broken", read.DebugStates.Keys);
        Assert.IsAssignableFrom<JsonException>(Assert.Single(read.ReadFailures));
    }

    private static WorkflowInstance Instance(string id) => new()
    {
        InstanceId = id, WorkflowName = "Slice3", Status = WorkflowStatus.Running,
        ExecutionStateJson = StoreTestSupport.CheckpointJson, WorkflowDataJson = StoredJson.Inline("{}")
    };
}
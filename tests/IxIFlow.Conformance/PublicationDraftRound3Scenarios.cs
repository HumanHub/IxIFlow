using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
public sealed class PublicationDraftRound3Scenarios
{
    [Theory]
    [InlineData(StoreProfiles.DistributedSqlServer, false)]
    [InlineData(StoreProfiles.DistributedSqlServer, true)]
    [InlineData(StoreProfiles.DistributedPostgreSql, false)]
    [InlineData(StoreProfiles.DistributedPostgreSql, true)]
    public async Task DraftContentWithQueuedExecutableMutationDoesNotBlockRootOrChildStart(string profile, bool publication)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var registered = Version("Starts");
        await store.RegisterWorkflowVersionAsync(registered);
        registered = (await store.GetWorkflowVersionAsync(registered.Name, 1))!;
        var draft = await store.SaveDefinitionAsync(new("Publication", 1, "workflow", "source"), null);
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([1]));
        var parent = Instance("parent");
        var root = Instance("root");
        var child = Instance("child");
        child.ParentInstanceId = parent.InstanceId;
        child.ParentWaitId = "child";
        child.Status = WorkflowStatus.Running;
        await store.SeedAsync(parent, wakeAfter: TimeSpan.Zero);
        var parentLease = await StoreTestSupport.LeaseAsync(store, parent.InstanceId);
        var rootLease = await StoreTestSupport.LeaseAsync(store, root.InstanceId);
        await using var holder = database.NewConnection();
        await holder.OpenAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await holder.ExecuteAsync(profile == StoreProfiles.DistributedSqlServer
            ? "EXEC sys.sp_getapplock @Resource=N'IxIFlow.CatalogContent', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000"
            : "SELECT pg_advisory_xact_lock(hashtext('IxIFlow.CatalogContent'))", transaction: transaction);
        var mutation = publication
            ? store.PublishDefinitionAsync(draft.Id, Version(draft.Name) with
                { ArtifactDigest = digest, PublishedDraft = draft.Id, ManifestJson = "{}" }, store)
            : store.SetWorkflowVersionStateAsync(registered.Name, 1, WorkflowVersionState.Deprecated, new(registered.LifecycleRevision));
        try
        {
            // Observe the real lock wait, so neither scheduling nor a preparation read can
            // make the starts race ahead of the queued exclusive mutation.
            await using var probe = database.NewConnection();
            var timeout = Stopwatch.StartNew();
            while (await probe.ExecuteScalarAsync<int>(profile == StoreProfiles.DistributedSqlServer
                ? "SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_database_id=DB_ID() AND resource_type='APPLICATION' AND resource_description LIKE '%IxIFlow.CatalogContent%' AND request_status='WAIT'"
                : "SELECT COUNT(*) FROM pg_locks WHERE database=(SELECT oid FROM pg_database WHERE datname=current_database()) AND locktype='advisory' AND NOT granted AND objid=(hashtext('IxIFlow.CatalogContent')::bigint & 4294967295)") == 0)
            {
                Assert.False(mutation.IsCompleted);
                Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(5), "Executable mutation did not queue for content");
                await Task.Delay(20);
            }
            var starts = await Task.WhenAll(
                store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(root, rootLease.Epoch, 0)),
                store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, parentLease.Epoch, parent.Revision,
                    childStarts: [new(child.InstanceId, JsonSerializer.Serialize(child), "child")],
                    openedWaits: [new("child", WaitKind.Child, "child", ChildCompleted.EventType, null)])))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(starts, result => Assert.Equal(WorkflowCommitStatus.Applied, result.Status));
            Assert.False(mutation.IsCompleted);
        }
        finally
        {
            await transaction.RollbackAsync();
            await mutation.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.NotNull(await store.GetWorkflowInstanceAsync(root.InstanceId));
        Assert.NotNull(await store.GetWorkflowInstanceAsync(child.InstanceId));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DraftAndVersionNamesScopesAndFiltersRejectMalformedUtf16WithoutReplacingIt(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        foreach (var name in new[] { "D\ud800", "D\udc00" })
        {
            foreach (var kind in new[] { "workflow", "activity", "template" })
            {
                await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveDefinitionAsync(new(name, 1, kind, "source"), null));
                await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetDefinitionAsync(kind, name, 1));
            }
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.RegisterWorkflowVersionAsync(Version(name)));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetWorkflowVersionAsync(name, 1));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetWorkflowVersionsAsync(name));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetDefinitionsAsync(new(CatalogScope.Unrestricted, NamePrefix: name, Limit: 1)));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetPublicationsAsync(new(CatalogScope.Unrestricted, "workflow", name, 1, Limit: 1)));
            for (var kind = 0; kind < 4; kind++)
            {
                var selected = kind;
                Assert.ThrowsAny<ArgumentException>(() => Scope(selected, new(Names: [name])));
                Assert.ThrowsAny<ArgumentException>(() => Scope(selected, new(Prefixes: [name])));
            }
        }
        // Literal replacement characters remain exact identities, including in scope hashes
        // and cursors. Check their version tie and their distinction from a supplementary scalar.
        string[] valid = ["D\ufffd", "D\ufffdA", "D\U00010000"];
        foreach (var name in valid.Reverse())
            foreach (var version in new[] { 2, 1 })
                await store.SaveDefinitionAsync(new(name, version, "workflow", "source"), null);
        foreach (var scope in new[] { CatalogScope.Unrestricted, Scope(0, new(Prefixes: ["D"])) })
        {
            string? after = null;
            foreach (var name in valid)
                foreach (var version in new[] { 1, 2 })
                {
                    var page = await store.GetDefinitionsAsync(new(scope, NamePrefix: "D", After: after, Limit: 1));
                    var row = Assert.Single(page.Items);
                    Assert.Equal((name, version), (row.Name, row.Version));
                    after = page.NextCursor;
                }
            Assert.Null(after);
        }
        foreach (var kind in Enumerable.Range(0, 4))
            Assert.NotEqual(Scope(kind, new(Names: ["D\ufffd"])).Hash, Scope(kind, new(Names: ["D\U00010000"])).Hash);
        var exact = await store.GetDefinitionsAsync(new(Scope(0, new(Names: ["D\ufffd"])), NamePrefix: "D\ufffd", Limit: 1));
        Assert.Equal("D\ufffd", Assert.Single(exact.Items).Name);
        await store.RegisterWorkflowVersionAsync(Version("D\ufffd"));
        Assert.Equal("D\ufffd", (await store.GetWorkflowVersionAsync("D\ufffd", 1))!.Name);
    }

    public static IEnumerable<object[]> CorruptInventories => StoreProfiles.ProfileNames
        .Where(profile => profile != StoreProfiles.LocalMemory)
        .SelectMany(profile => new[] { "manifest", "row", "both" }.SelectMany(target =>
            new[] { "[null]", "[{}]" }.Select(inventory => new object[] { profile, target, inventory })));

    [Theory]
    [MemberData(nameof(CorruptInventories))]
    public async Task PublicationProjectionAndInvocationInventoryCorruptionAlwaysRefusesWithArtifactMismatch(string profile, string target, string inventory)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        WorkflowVersionInvocation[] invocations = [new("Child", 1, "invoke", "source")];
        var row = Version("Corrupt") with { Invocations = invocations, Dependencies = [new("Child", 1)],
            ManifestJson = JsonSerializer.Serialize(new { Invocations = invocations }) };
        await store.RegisterWorkflowVersionAsync(row);
        row = (await store.GetWorkflowVersionAsync(row.Name, 1))!;
        var json = JsonSerializer.SerializeToNode(row)!;
        json["StateChangedBy"] = "tampered";
        if (target != "manifest") json["Invocations"] = JsonNode.Parse(inventory);
        if (target != "row") json["ManifestJson"] = "{\"Invocations\":" + inventory + "}";
        await using var connection = database.NewConnection();
        await connection.ExecuteAsync(database.Profile == StoreProfiles.DistributedSqlServer
            ? "UPDATE dbo.IxIFlowWorkflowVersions SET MetadataJson=@Json WHERE Name=@Name AND Version=1"
            : "UPDATE ixiflow_workflow_versions SET metadata_json=@Json WHERE name=@Name AND version=1",
            new { Json = json.ToJsonString(), row.Name });
        foreach (var read in new Func<Task>[] {
            async () => { await store.GetWorkflowVersionAsync(row.Name, 1); },
            async () => { await store.GetWorkflowVersionsAsync(); },
            async () => { await store.GetWorkflowVersionChangesAsync(0); } })
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(read);
            Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
            Assert.Contains("invocation inventory", error.Message);
        }
    }

    private static CatalogScope Scope(int kind, CatalogNames names) => new(
        kind == 0 ? names : new(), kind == 1 ? names : new(), kind == 2 ? names : new(), kind == 3 ? names : new());

    private static WorkflowVersion Version(string name)
    {
        var contract = WorkflowContract.OfType(typeof(object));
        return new(name, 1, "hash", contract.ToJson(), contract.Hash, []);
    }

    private static WorkflowInstance Instance(string id) => new()
    {
        InstanceId = id, WorkflowName = "Starts", WorkflowVersion = 1, Status = WorkflowStatus.Suspended,
        ExecutionStateJson = StoreTestSupport.CheckpointJson, WorkflowDataJson = StoredJson.Inline("{}"),
        WorkflowDataType = WorkflowTypeIdentity.SavedName(typeof(object))
    };
}

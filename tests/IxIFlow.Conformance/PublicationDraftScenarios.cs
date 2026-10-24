using System.Reflection;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
[Trait("Spec", "F-09")]
[Trait("Spec", "F-14")]
public sealed class PublicationDraftScenarios
{
    private static readonly WorkflowPublicationOutcome Failure = new(null,
        [new("Compile", "TEST001", "Invalid source", "Error", "workflow.yaml", 1, 1, WorkflowPublishDiagnosticClass.Authoring)],
        WorkflowPublishDiagnosticClass.Authoring);

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DraftKeysAuditFullIdentityAndScopedSummaryPages(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var revision = await store.GetCatalogRevisionAsync();
        var workflow = await store.SaveDefinitionAsync(new("shared", 1, "workflow", " source\n")
            { UpdatedAtUtc = DateTime.MinValue, UpdatedBy = "forged" }, null, "creator");
        Assert.NotEqual(Guid.Empty, workflow.Generation);
        Assert.Equal(1, workflow.Revision);
        Assert.Equal("creator", workflow.UpdatedBy);
        Assert.NotEqual(DateTime.MinValue, workflow.UpdatedAtUtc);
        Assert.Equal(revision + 1, await store.GetCatalogRevisionAsync());
        foreach (var kind in new[] { "activity", "template" })
        {
            var other = await store.SaveDefinitionAsync(new("shared", 1, kind, kind), null);
            Assert.NotEqual(workflow.Generation, other.Generation);
            Assert.Equal(other, await store.GetDefinitionAsync(kind, "shared", 1));
        }
        var updated = await store.SaveDefinitionAsync(workflow with { Source = "changed", Generation = Guid.NewGuid(), Revision = 900,
            UpdatedBy = "forged" }, workflow.Id, "editor");
        Assert.Equal(workflow.Generation, updated.Generation);
        Assert.Equal(2, updated.Revision);
        Assert.Equal("editor", updated.UpdatedBy);
        await Assert.ThrowsAsync<CatalogPreconditionException>(() => store.SaveDefinitionAsync(workflow, workflow.Id));
        await Assert.ThrowsAsync<CatalogPreconditionException>(() => store.DeleteDefinitionAsync(workflow.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveDefinitionAsync(workflow with { Kind = "activity" }, workflow.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveDefinitionAsync(workflow, null));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveDefinitionAsync(new("Actor", 1, "workflow", ""), null, new string('a', 201)));
        await store.SaveDefinitionAsync(new("shared-other", 1, "workflow", "hidden"), null);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveDefinitionAsync(new("activity", 65535, "activity", ""), null));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveDefinitionAsync(new("Upper", 1, "template", ""), null));
        var scope = new CatalogScope(new(["shared"]), new([]), new(), new(["shared"]));
        var page = await store.GetDefinitionsAsync(new(scope, Limit: 1));
        Assert.Equal("template", Assert.Single(page.Items).Kind);
        Assert.NotNull(page.NextCursor);
        Assert.Null(typeof(WorkflowDraftSummary).GetProperty("Source"));
        var second = await store.GetDefinitionsAsync(new(scope, After: page.NextCursor, Limit: 1));
        Assert.Equal("workflow", Assert.Single(second.Items).Kind);
        Assert.Equal("changed".Length, second.Items[0].SourceLength);
        Assert.Equal("editor", second.Items[0].UpdatedBy);
        Assert.Null(second.NextCursor);
        Assert.Equal(await store.GetCatalogRevisionAsync(), second.CatalogRevision);
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetDefinitionsAsync(new(CatalogScope.Unrestricted, After: page.NextCursor)));
        Assert.Empty((await store.GetDefinitionsAsync(new(new(new([]), new([]), new([]), new([]))))).Items);
        // Literal prefixes must have identical ordinal behavior on SQL LIKE dialects.
        await store.SaveDefinitionAsync(new("%_[]Key", 1, "workflow", "literal"), null);
        await store.SaveDefinitionAsync(new("%_[]key", 1, "workflow", "case"), null);
        var literal = new CatalogScope(new(Prefixes: ["%_[]K"]), new([]), new([]), new([]));
        Assert.Equal("%_[]Key", Assert.Single((await store.GetDefinitionsAsync(new(literal, NamePrefix: "%_[]K"))).Items).Name);
        await store.SaveDefinitionAsync(new("😀Key", 1, "workflow", "unicode"), null);
        var unicode = new CatalogScope(new(Prefixes: ["😀K"]), new([]), new([]), new([]));
        Assert.Equal("😀Key", Assert.Single((await store.GetDefinitionsAsync(new(unicode, NamePrefix: "😀K"))).Items).Name);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DeleteRecreateDuringAwaitedPublicationCannotConsumeReplacement(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Recreated", 1, "workflow", "original"), null);
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([1, 2, 3]));
        var row = Version(draft, digest);
        var probe = new AwaitedArtifacts(store);
        var publishing = Task.Run(() => store.PublishDefinitionAsync(draft.Id, row, probe));
        WorkflowDraft replacement;
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(await store.DeleteDefinitionAsync(draft.Id));
            replacement = await store.SaveDefinitionAsync(new(draft.Name, 1, "workflow", "replacement"), null);
            Assert.NotEqual(draft.Generation, replacement.Generation);
            Assert.Equal(1, replacement.Revision);
        }
        finally { probe.Release.TrySetResult(); }
        await Assert.ThrowsAsync<CatalogPreconditionException>(() => publishing);
        await Assert.ThrowsAsync<CatalogPreconditionException>(() => store.DeleteDefinitionAsync(draft.Id));
        await Assert.ThrowsAsync<CatalogPreconditionException>(() => store.SaveDefinitionAsync(draft, draft.Id));
        await Assert.ThrowsAsync<CatalogPreconditionException>(() => store.PublishDefinitionAsync(draft.Id, row, store));
        Assert.Equal(replacement, await store.GetDefinitionAsync("workflow", draft.Name, 1));
        Assert.Null(await store.GetWorkflowVersionAsync(draft.Name, 1));
        var saved = await store.PublishDefinitionAsync(replacement.Id, Version(replacement, digest), store, "publisher");
        Assert.Equal(replacement.Id, saved.PublishedDraft);
        Assert.Equal("publisher", saved.PublishedBy);
        Assert.NotNull(saved.PublishedAtUtc);
        Assert.True(saved.LifecycleRevision > 0);
        Assert.Equal(saved, await store.PublishDefinitionAsync(replacement.Id, Version(replacement, digest), store, "retry"));
        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.PublishDefinitionAsync(draft.Id, row, store));
        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.SaveDefinitionAsync(new(draft.Name, 1, "workflow", "again"), null));
        Assert.Equal("publisher", (await store.GetWorkflowVersionAsync(draft.Name, 1))!.PublishedBy);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task LifecycleCompareAndSetRejectsACompetingRequestAfterAwait(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([1]));
        var draft = new WorkflowDraft("Lifecycle", 1, "workflow", "source");
        await store.RegisterWorkflowVersionAsync(Version(draft, digest) with { State = WorkflowVersionState.Draft, PublishedDraft = null }, store);
        var deprecated = (await store.GetWorkflowVersionAsync(draft.Name, 1))!;
        var probe = new AwaitedArtifacts(store);
        var first = Task.Run(() => store.SetWorkflowVersionStateAsync(draft.Name, 1, WorkflowVersionState.Published, new(deprecated.LifecycleRevision), probe, "winner"));
        Task<WorkflowVersion> stale;
        try
        {
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            stale = Task.Run(() => store.SetWorkflowVersionStateAsync(draft.Name, 1, WorkflowVersionState.Retired, new(deprecated.LifecycleRevision), actor: "loser"));
        }
        finally { probe.Release.TrySetResult(); }
        var winner = await first;
        await Assert.ThrowsAsync<CatalogPreconditionException>(() => stale);
        Assert.Equal("winner", winner.StateChangedBy);
        Assert.True(winner.LifecycleRevision > deprecated.LifecycleRevision);
        Assert.Equal(WorkflowVersionState.Published, (await store.GetWorkflowVersionAsync(draft.Name, 1))!.State);
        var before = await store.GetCatalogRevisionAsync();
        var unchanged = await store.SetWorkflowVersionStateAsync(draft.Name, 1, winner.State, new(winner.LifecycleRevision), actor: "noop");
        Assert.Equal("winner", unchanged.StateChangedBy);
        Assert.Equal(before, await store.GetCatalogRevisionAsync());
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PublicationAttemptsRetainFailuresAndAbandonedOwners(string profile)
    {
        var clock = new FakeTimeProvider();
        await using var database = await StoreProfiles.CreateAsync(profile, clock);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Attempts", 1, "workflow", "source"), null);
        var host = Guid.NewGuid();
        var first = await store.BeginPublicationAsync(draft.Id, host, "author", TimeSpan.FromMinutes(1));
        var revision = await store.GetCatalogRevisionAsync();
        Assert.Equal(first.Id, (await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "joiner", TimeSpan.FromMinutes(1))).Id);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await store.HeartbeatPublicationAsync(first.Id, host));
        Assert.True(await store.GetCatalogRevisionAsync() > revision);
        Assert.False(await store.HeartbeatPublicationAsync(first.Id, Guid.NewGuid()));
        var failed = await store.CompletePublicationAsync(first.Id, host, Failure);
        Assert.Equal(WorkflowPublicationState.Failed, failed.State);
        Assert.Equal("author", failed.StartedBy);
        Assert.NotNull(failed.FinishedAtUtc);
        var second = await store.BeginPublicationAsync(draft.Id, host, "second", TimeSpan.FromMinutes(1));
        await AgeAsync(database, clock, "heartbeat");
        var third = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "third", TimeSpan.FromMinutes(1));
        Assert.Equal(3, third.Attempt);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(second.Id, third.Id);
        Assert.False(await store.HeartbeatPublicationAsync(second.Id, host));
        await Assert.ThrowsAsync<CatalogPreconditionException>(() => store.CompletePublicationAsync(second.Id, host, Failure));
        var abandoned = await store.GetPublicationAsync(second.Id, CatalogScope.Unrestricted);
        Assert.Equal(WorkflowPublicationState.Abandoned, abandoned!.State);
        Assert.NotNull(abandoned.FinishedAtUtc);
        Assert.Equal("second", abandoned.StartedBy);
        var history = await store.GetPublicationsAsync(new(CatalogScope.Unrestricted, "workflow", draft.Name, 1, Limit: 1));
        var ids = new HashSet<Guid>();
        while (true)
        {
            Assert.True(ids.Add(Assert.Single(history.Items).Id));
            if (history.NextCursor == null) break;
            history = await store.GetPublicationsAsync(new(CatalogScope.Unrestricted, "workflow", draft.Name, 1, After: history.NextCursor, Limit: 1));
        }
        Assert.Equal(3, ids.Count);
        await store.DeleteDefinitionAsync(draft.Id);
        Assert.Equal("TEST001", Assert.Single((await store.GetPublicationAsync(first.Id, CatalogScope.Unrestricted))!.Outcome!.Diagnostics).Code);
        var denied = new CatalogScope(new([]), new(), new(), new());
        Assert.Null(await store.GetPublicationAsync(first.Id, denied));
        Assert.Empty((await store.GetPublicationsAsync(new(denied, "workflow", draft.Name, 1))).Items);
        await AgeAsync(database, clock, "finished");
        Assert.Equal(2, await store.PurgePublicationsAsync(TimeSpan.FromDays(30), 100));
        Assert.NotNull(await store.GetPublicationAsync(third.Id, CatalogScope.Unrestricted));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task GenerationLedgerAndAttemptNumbersSurviveHistoryCollection(string profile)
    {
        var clock = new FakeTimeProvider();
        await using var database = await StoreProfiles.CreateAsync(profile, clock);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Permanent", 1, "workflow", "source"), null);
        var first = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), null, TimeSpan.FromMinutes(1));
        await store.CompletePublicationAsync(first.Id, first.HostId, Failure);
        await AgeAsync(database, clock, "finished");
        Assert.Equal(1, await store.PurgePublicationsAsync(TimeSpan.FromDays(30), 10));
        var next = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), null, TimeSpan.FromMinutes(1));
        Assert.Equal(2, next.Attempt);
        await store.DeleteDefinitionAsync(draft.Id);
        var recreated = await store.SaveDefinitionAsync(new(draft.Name, 1, "workflow", "new"), null);
        Assert.NotEqual(draft.Generation, recreated.Generation);
        Assert.Equal(1, recreated.Revision);
        await Assert.ThrowsAsync<CatalogPreconditionException>(() => store.SaveDefinitionAsync(recreated, draft.Id));
        if (profile == StoreProfiles.LocalMemory)
        {
            var issued = (HashSet<Guid>)typeof(InMemoryWorkflowStateRepository).GetField("_draftGenerations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            Assert.Contains(draft.Generation, issued);
            Assert.Contains(recreated.Generation, issued);
        }
        else
        {
            await using var connection = database.NewConnection();
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(profile.StartsWith(StoreProfiles.DistributedSqlServer, StringComparison.Ordinal)
                ? "SELECT COUNT(*) FROM dbo.IxIFlowDraftGenerations WHERE Generation=@Generation"
                : "SELECT COUNT(*) FROM ixiflow_draft_generations WHERE generation=@Generation", new { draft.Generation }));
        }
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CompletionReconcilesOnlyTheExactAuditedReceiptAndReadsNeverWrite(string profile)
    {
        var clock = new FakeTimeProvider();
        await using var database = await StoreProfiles.CreateAsync(profile, clock);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Receipt", 1, "workflow", "source"), null);
        var old = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "starter", TimeSpan.FromMinutes(1));
        await AgeAsync(database, clock, "heartbeat");
        var attempt = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "replacement", TimeSpan.FromMinutes(1));
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([9]));
        // An abandoned build can commit the same immutable publication. Its replacement reconciles it.
        var row = await store.PublishDefinitionAsync(draft.Id, Version(draft, digest), store, "publisher");
        await Assert.ThrowsAsync<CatalogPreconditionException>(() => store.CompletePublicationAsync(old.Id, old.HostId, Failure));
        var revision = await store.GetCatalogRevisionAsync();
        Assert.Equal(WorkflowPublicationState.Running, (await store.GetPublicationAsync(attempt.Id, CatalogScope.Unrestricted))!.State);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        var receipt = new WorkflowPublicationReceipt(draft.Id, JsonSerializer.Serialize(row));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompletePublicationAsync(attempt.Id, attempt.HostId,
            new(receipt with { Draft = draft.Id with { Generation = Guid.NewGuid() } }, [], null)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CompletePublicationAsync(attempt.Id, attempt.HostId,
            new(receipt with { ReceiptJson = "{}" }, [], null)));
        var completed = await store.CompletePublicationAsync(attempt.Id, attempt.HostId, new(receipt, [], null));
        Assert.Equal(WorkflowPublicationState.Published, completed.State);
        Assert.Equal(receipt, completed.Outcome!.Receipt);
        Assert.Equal(attempt.Id, (await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "retry", TimeSpan.FromMinutes(1))).Id);
        Assert.Null(await store.GetDefinitionAsync("workflow", draft.Name, 1));
        await AgeAsync(database, clock, "finished");
        Assert.Equal(2, await store.PurgePublicationsAsync(TimeSpan.FromDays(30), 100));
        var recovered = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "after-purge", TimeSpan.FromMinutes(1));
        Assert.Equal(3, recovered.Attempt);
        Assert.Equal(WorkflowPublicationState.Published, (await store.CompletePublicationAsync(recovered.Id, recovered.HostId, new(receipt, [], null))).State);
        var code = new WorkflowVersion("Code", 1, "hash", row.ContractJson, row.ContractHash, [], PublishedDraft: draft.Id)
            { LifecycleRevision = 999, PublishedBy = "forged" };
        await store.RegisterWorkflowVersionAsync(code);
        var registered = (await store.GetWorkflowVersionAsync(code.Name, 1))!;
        Assert.Null(registered.PublishedDraft);
        Assert.Null(registered.PublishedBy);
        Assert.NotEqual(999, registered.LifecycleRevision);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RetirementBlockersMaskOutsideIdentitiesAndKeepTheWriteGuard(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var contract = WorkflowContract.OfType(typeof(object));
        WorkflowVersion Row(string name) => new(name, 1, "hash", contract.ToJson(), contract.Hash, []);
        await store.RegisterWorkflowVersionAsync(Row("Target"));
        foreach (var name in new[] { "Visible", "Hidden" })
            await store.RegisterWorkflowVersionAsync(Row(name) with
                { Dependencies = [new("Target", 1)], Invocations = [new("Target", 1, "invoke", "fixture")] });
        var scope = new CatalogScope(new(["Target", "Visible"]), new(), new(), new());
        var blockers = await store.GetRetirementBlockersAsync("Target", 1, scope);
        Assert.Equal("Visible", Assert.Single(blockers.Callers).Name);
        Assert.True(blockers.OutsideScope);
        Assert.Null(blockers.UnfinishedCaller);
        var target = (await store.GetWorkflowVersionAsync("Target", 1))!;
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.SetWorkflowVersionStateAsync("Target", 1,
            WorkflowVersionState.Retired, new(target.LifecycleRevision)));
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Equal(target.LifecycleRevision, (await store.GetWorkflowVersionAsync("Target", 1))!.LifecycleRevision);
    }

    private static WorkflowVersion Version(WorkflowDraft draft, ArtifactDigest digest)
    {
        var contract = WorkflowContract.OfType(typeof(object));
        return new(draft.Name, draft.Version, "hash", contract.ToJson(), contract.Hash, [], ArtifactDigest: digest,
            PublishedDraft: draft.Id, ManifestJson: "{}");
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ImmutableActivityIdentityRefusesDraftEditsEvenWithoutPackageBytes(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("activity-name", 2, "activity", "source"), null);
        var digest = await store.PutAsync(ArtifactKind.Package, new MemoryStream([9]));
        await store.RegisterPackageAsync(new("activity-name", "2.0.0", digest,
            new Dictionary<string, ArtifactDigest> { ["fixture.nuspec"] = ArtifactDigest.Of([1]) }, []), new([], PackageExtraction.Pending, null, null, []), store);
        await store.DeleteAsync(digest);
        var changed = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.SaveDefinitionAsync(draft with { Source = "edit" }, draft.Id));
        Assert.Equal(WorkflowErrorKind.InvalidState, changed.Kind);
        var created = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.SaveDefinitionAsync(new(draft.Name, 2, "activity", "new"), null));
        Assert.Equal(WorkflowErrorKind.InvalidState, created.Kind);
        Assert.Equal(draft, await store.GetDefinitionAsync("activity", draft.Name, 2));
        Assert.NotNull(await store.SaveDefinitionAsync(new(draft.Name, 2, "workflow", "independent"), null));
    }

    private static async Task AgeAsync(ProfileStore database, FakeTimeProvider clock, string column)
    {
        if (database.Profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite)
        {
            clock.Advance(column == "finished" ? TimeSpan.FromDays(31) : TimeSpan.FromMinutes(2));
            return;
        }
        await using var connection = database.NewConnection();
        await connection.ExecuteAsync(database.Profile == StoreProfiles.DistributedSqlServer
            ? column == "finished" ? "UPDATE dbo.IxIFlowPublicationAttempts SET FinishedAtUtc=DATEADD(DAY,-31,SYSUTCDATETIME()) WHERE FinishedAtUtc IS NOT NULL"
                : "UPDATE dbo.IxIFlowPublicationAttempts SET HeartbeatAtUtc=DATEADD(MINUTE,-2,SYSUTCDATETIME()) WHERE State='Running'"
            : column == "finished" ? "UPDATE ixiflow_publication_attempts SET finished_at_utc=clock_timestamp()-INTERVAL '31 days' WHERE finished_at_utc IS NOT NULL"
                : "UPDATE ixiflow_publication_attempts SET heartbeat_at_utc=clock_timestamp()-INTERVAL '2 minutes' WHERE state='Running'");
    }

    private sealed class AwaitedArtifacts(IArtifactStore inner) : IArtifactStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.ExistsAsync(digest, cancellationToken);
        }
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) => inner.PutAsync(kind, content, cancellationToken);
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.OpenAsync(digest, cancellationToken);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.DeleteAsync(digest, cancellationToken);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) =>
            inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    }
}

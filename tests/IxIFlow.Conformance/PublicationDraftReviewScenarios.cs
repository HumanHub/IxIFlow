using System.Collections;
using System.Reflection;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Relational;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
[Trait("Spec", "F-09")]
[Trait("Spec", "F-14")]
public sealed class PublicationDraftReviewScenarios
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(1);
    private static readonly WorkflowPublicationOutcome Failure = new(null, [], WorkflowPublishDiagnosticClass.Authoring);

    [Theory]
    [InlineData(StoreProfiles.DistributedSqlServer)]
    [InlineData(StoreProfiles.DistributedPostgreSql)]
    public async Task DraftAndPublicationContentDoNotWaitForTheStartGuard(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.GetCatalogRevisionAsync();
        await using var holder = database.NewConnection();
        await holder.OpenAsync();
        await using var transaction = await holder.BeginTransactionAsync();

        await holder.ExecuteAsync(profile == StoreProfiles.DistributedSqlServer
            ? "EXEC sys.sp_getapplock @Resource=N'IxIFlow.Catalog', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000"
            : "SELECT pg_advisory_xact_lock(hashtext('IxIFlow.Catalog'))", transaction: transaction);

        try
        {
            await ExerciseContent().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        async Task ExerciseContent()
        {
            var draft = await store.SaveDefinitionAsync(new("Independent", 1, "workflow", "source"), null);
            draft = await store.SaveDefinitionAsync(draft with { Source = "edited" }, draft.Id);
            var running = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), null, StaleAfter);
            Assert.True(await store.HeartbeatPublicationAsync(running.Id, running.HostId));
            var finished = await store.CompletePublicationAsync(running.Id, running.HostId, Failure);
            Assert.Equal(WorkflowPublicationState.Failed, finished.State);
            var summary = Assert.Single((await store.GetDefinitionsAsync(new(CatalogScope.Unrestricted))).Items);

            Assert.Equal((draft.Kind, draft.Name, draft.Version, draft.Generation, draft.Revision),
                (summary.Kind, summary.Name, summary.Version, summary.Generation, summary.Revision));

            Assert.Equal(finished.Id,
                Assert.Single((await store.GetPublicationsAsync(new(CatalogScope.Unrestricted, "workflow", draft.Name, 1))).Items).Id);

            await Age(database, new FakeTimeProvider(), finished: true);
            Assert.Equal(1, await store.PurgePublicationsAsync(TimeSpan.FromDays(30), 100));
            Assert.True(await store.DeleteDefinitionAsync(draft.Id));
        }
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PublicationCompletionAfterLifecycleChangeValidatesImmutableContentAndAudit(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([1]));

        foreach (var state in new[] { WorkflowVersionState.Deprecated, WorkflowVersionState.Retired })
        {
            var draft = await store.SaveDefinitionAsync(new("Receipt-" + state, 1, "workflow", "source"), null);
            var attempt = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "starter", StaleAfter);
            var published = await store.PublishDefinitionAsync(draft.Id, Version(draft, digest), store, "publisher");
            var receipt = new WorkflowPublicationReceipt(draft.Id, JsonSerializer.Serialize(published));

            var changed = await store.SetWorkflowVersionStateAsync(draft.Name, 1, state, new(published.LifecycleRevision),
                actor: "operator");

            var revision = await store.GetCatalogRevisionAsync();
            Assert.True(changed.LifecycleRevision > published.LifecycleRevision);

            foreach (var forged in new[]
                     {
                         published with { StructureHash = "forged" },
                         published with { ContractJson = "{}" },
                         published with { ContractHash = "forged" },
                         published with { ManifestJson = "{\"forged\":true}" },
                         published with { ArtifactDigest = ArtifactDigest.Of([2]) },
                         published with { Invocations = [new("Other", 1, "step", "source")] },
                         published with { Dependencies = [new("Other", 1)] },
                         published with { ArtifactReferences = [ArtifactDigest.Of([3])] },
                         published with { PublishedDraft = draft.Id with { Generation = Guid.NewGuid() } },
                         published with { PublishedAtUtc = published.PublishedAtUtc!.Value.AddSeconds(1) },
                         published with { PublishedBy = "forged" }
                     })
                await Assert.ThrowsAsync<ArgumentException>(() => store.CompletePublicationAsync(attempt.Id, attempt.HostId,
                    new(receipt with { ReceiptJson = JsonSerializer.Serialize(forged) }, [], null)));

            Assert.Equal(revision, await store.GetCatalogRevisionAsync());
            var completed = await store.CompletePublicationAsync(attempt.Id, attempt.HostId, new(receipt, [], null));
            Assert.Equal(WorkflowPublicationState.Published, completed.State);
            Assert.Equal(draft.Id, completed.Outcome!.Receipt!.Draft);

            // Completion derives its persisted receipt from the trusted row, including current lifecycle audit.
            Assert.Equal(JsonSerializer.Serialize(changed), completed.Outcome.Receipt.ReceiptJson);
            Assert.Equal("publisher", changed.PublishedBy);
            Assert.Equal(published.PublishedAtUtc, changed.PublishedAtUtc);
            Assert.Equal("operator", changed.StateChangedBy);

            Assert.Equal(completed.Outcome.Receipt,
                (await store.GetPublicationAsync(attempt.Id, CatalogScope.Unrestricted))!.Outcome!.Receipt);
        }
    }

    public static IEnumerable<object[]> SupersededDrafts => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "edit", "recreate" }.Select(change => new object[] { profile, change }));

    [Theory]
    [MemberData(nameof(SupersededDrafts))]
    public async Task PublicationHostLossAbandonsSupersededDraftWithoutStartingReplacement(string profile, string change)
    {
        var clock = new FakeTimeProvider();
        await using var database = await StoreProfiles.CreateAsync(profile, clock);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Superseded", 1, "workflow", "old"), null);
        var lost = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "lost-host", StaleAfter);
        WorkflowDraft replacement;

        if (change == "edit")
            replacement = await store.SaveDefinitionAsync(draft with { Source = "edited" }, draft.Id, "editor");
        else
        {
            await store.DeleteDefinitionAsync(draft.Id);
            replacement = await store.SaveDefinitionAsync(new(draft.Name, 1, "workflow", "recreated"), null, "editor");
        }

        await Age(database, clock, finished: false);

        // Server profiles age the stored heartbeat; local profiles advance the injected clock.
        lost = (await store.GetPublicationAsync(lost.Id, CatalogScope.Unrestricted))!;
        var replacementAttempt = await store.BeginPublicationAsync(replacement.Id, Guid.NewGuid(), "replacement", StaleAfter);
        var revision = await store.GetCatalogRevisionAsync();
        var abandoned = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "recovery", StaleAfter);
        Assert.Equal(lost with { State = WorkflowPublicationState.Abandoned, FinishedAtUtc = abandoned.FinishedAtUtc }, abandoned);
        Assert.NotNull(abandoned.FinishedAtUtc);
        Assert.Equal(revision + 1, await store.GetCatalogRevisionAsync());
        Assert.False(await store.HeartbeatPublicationAsync(lost.Id, lost.HostId));
        await Assert.ThrowsAsync<CatalogPreconditionException>(() => store.CompletePublicationAsync(lost.Id, lost.HostId, Failure));
        Assert.Equal(replacement, await store.GetDefinitionAsync("workflow", draft.Name, 1));
        Assert.Equal(replacementAttempt.Id, (await store.BeginPublicationAsync(replacement.Id, Guid.NewGuid(), null, StaleAfter)).Id);
        Assert.Equal(abandoned.Id, (await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), null, StaleAfter)).Id);
        Assert.Equal(revision + 1, await store.GetCatalogRevisionAsync());
        var history = await store.GetPublicationsAsync(new(CatalogScope.Unrestricted, "workflow", draft.Name, 1));
        Assert.Equal(2, history.Items.Count);
        await Age(database, clock, finished: true);
        Assert.Equal(1, await store.PurgePublicationsAsync(TimeSpan.FromDays(30), 100));
        Assert.Null(await store.GetPublicationAsync(lost.Id, CatalogScope.Unrestricted));

        Assert.Equal(WorkflowPublicationState.Running,
            (await store.GetPublicationAsync(replacementAttempt.Id, CatalogScope.Unrestricted))!.State);

        Assert.Equal(replacement, await store.GetDefinitionAsync("workflow", draft.Name, 1));

        await Assert.ThrowsAsync<CatalogPreconditionException>(() =>
            store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), null, StaleAfter));

        // Neither the old permanent head nor the replacement head is collected with history.
        if (store is InMemoryWorkflowStateRepository)
        {
            var heads = (IDictionary)typeof(InMemoryWorkflowStateRepository).GetField("_publicationHeads",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

            Assert.True(heads.Contains(draft.Id));
            Assert.True(heads.Contains(replacement.Id));
            Assert.Equal(2, heads.Count);
        }
        else
        {
            await using var connection = database.NewConnection();

            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(database.Profile == StoreProfiles.DistributedSqlServer
                ? "SELECT COUNT(*) FROM dbo.IxIFlowPublicationHeads WHERE LastAttempt=1"
                : "SELECT COUNT(*) FROM ixiflow_publication_heads WHERE last_attempt=1"));
        }
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DraftUnicodeScalarOrderingMatchesEveryLimitOneCursor(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        string[] names = ["A", "D\uD7FF", "D\uE000", "D\uFFFF", "D\U00010000", "D\U00010000A", "D\U00010001", "D\U0001F600", "D\U0010FFFF"];

        foreach (var name in names.Reverse())
        {
            await store.SaveDefinitionAsync(new(name, 2, "workflow", "second"), null);
            await store.SaveDefinitionAsync(new(name, 1, "workflow", "first"), null);
        }

        await store.SaveDefinitionAsync(new("a", 1, "activity", "activity"), null);
        await store.SaveDefinitionAsync(new("a", 1, "template", "template"), null);

        var expected = new[] { ("activity", "a", 1), ("template", "a", 1) }
            .Concat(names.SelectMany(name => new[] { ("workflow", name, 1), ("workflow", name, 2) })).ToArray();

        foreach (var prefix in new string?[] { null, "D", "D\U00010000" })
        {
            var actual = new List<(string, string, int)>();
            var cursors = new HashSet<string>();
            string? after = null;

            do
            {
                var page = await store.GetDefinitionsAsync(new(CatalogScope.Unrestricted, NamePrefix: prefix, After: after, Limit: 1));
                var row = Assert.Single(page.Items);
                actual.Add((row.Kind, row.Name, row.Version));
                after = page.NextCursor;

                if (after != null)
                    Assert.True(cursors.Add(after));

                Assert.True(actual.Count <= expected.Length);
            } while (after != null);

            Assert.Equal(expected.Where(row => prefix == null || row.Item2.StartsWith(prefix, StringComparison.Ordinal)), actual);
        }
    }

    public static IEnumerable<object[]> PreliminaryRaces => StoreProfiles.ProfileNames
        .Where(profile => profile != StoreProfiles.LocalMemory)
        .SelectMany(profile => new[] { "missing-draft", "stale-draft", "missing-artifact" }.Select(race => new object[] { profile, race }));

    [Theory]
    [MemberData(nameof(PreliminaryRaces))]
    public async Task PublicationPreliminaryRefusalsResolveConcurrentSameIdWinner(string profile, string race)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var relational = Assert.IsAssignableFrom<RelationalWorkflowStateRepository>(store);
        var draft = await store.SaveDefinitionAsync(new("Concurrent", 1, "workflow", "source"), null);
        var requested = race == "stale-draft" ? draft with { Revision = draft.Revision + 1 } : draft;
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([4]));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new MissingArtifactBarrier(store, entered, release);
        var barrierStage = race == "missing-draft" ? "Version" : "Draft";

        if (race != "missing-artifact")
            SetPreparationRead(relational, async stage =>
            {
                if (stage != barrierStage)
                    return;

                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            });

        var retry = Task.Run(() =>
            store.PublishDefinitionAsync(requested.Id, Version(requested, digest), race == "missing-artifact" ? probe : store, "loser"));

        WorkflowVersion winner;

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

            // The barrier belongs only to caller A. Caller B commits while A is between hint reads/preparation.
            SetPreparationRead(relational, null);

            if (race == "stale-draft")
                requested = await store.SaveDefinitionAsync(draft with { Source = "new source" }, draft.Id);

            winner = await store.PublishDefinitionAsync(requested.Id, Version(requested, digest) with { StructureHash = "winner" }, store,
                "winner");
        }
        finally
        {
            SetPreparationRead(relational, null);
            release.TrySetResult();
        }

        var returned = await retry;
        Assert.Equal(JsonSerializer.Serialize(winner), JsonSerializer.Serialize(returned));
        Assert.Equal("winner", returned.PublishedBy);
        Assert.Null(await store.GetDefinitionAsync("workflow", draft.Name, 1));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PublicationWithoutWinnerStillRefusesChangedDraftAndMissingArtifacts(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("NoWinner", 1, "workflow", "source"), null);
        var digest = ArtifactDigest.Of([5]);

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.PublishDefinitionAsync(draft.Id, Version(draft, digest), store));

        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
        var edited = await store.SaveDefinitionAsync(draft with { Source = "edited" }, draft.Id);
        await Assert.ThrowsAsync<CatalogPreconditionException>(() => store.PublishDefinitionAsync(draft.Id, Version(draft, digest), store));
        await store.DeleteDefinitionAsync(edited.Id);

        await Assert.ThrowsAsync<CatalogPreconditionException>(() =>
            store.PublishDefinitionAsync(edited.Id, Version(edited, digest), store));

        Assert.Null(await store.GetWorkflowVersionAsync(draft.Name, 1));
    }

    private static void SetPreparationRead(RelationalWorkflowStateRepository repository, Func<string, Task>? observer) =>
        typeof(RelationalWorkflowStateRepository).GetProperty("PublicationPreparationReadAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(repository, observer);

    private static WorkflowVersion Version(WorkflowDraft draft, ArtifactDigest digest)
    {
        var contract = WorkflowContract.OfType(typeof(object));

        return new(draft.Name, draft.Version, "hash", contract.ToJson(), contract.Hash,
            Descriptor: new WorkflowDescriptor(draft.Name, "", [], null, null,
                IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(object)),
                IxIFlow.Core.Runtime.WorkflowTypeIdentity.CSharpName(typeof(object))), [], ArtifactDigest: digest,
            PublishedDraft: draft.Id, ManifestJson: "{}");
    }

    private static async Task Age(ProfileStore database, FakeTimeProvider clock, bool finished)
    {
        if (database.Profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite)
        {
            clock.Advance(finished ? TimeSpan.FromDays(31) : TimeSpan.FromMinutes(2));

            return;
        }

        await using var connection = database.NewConnection();

        await connection.ExecuteAsync(database.Profile == StoreProfiles.DistributedSqlServer
            ? finished
                ? "UPDATE dbo.IxIFlowPublicationAttempts SET FinishedAtUtc=DATEADD(DAY,-31,SYSUTCDATETIME()) WHERE FinishedAtUtc IS NOT NULL"
                : "UPDATE dbo.IxIFlowPublicationAttempts SET HeartbeatAtUtc=DATEADD(MINUTE,-2,SYSUTCDATETIME()) WHERE State='Running'"
            : finished
                ? "UPDATE ixiflow_publication_attempts SET finished_at_utc=clock_timestamp()-INTERVAL '31 days' WHERE finished_at_utc IS NOT NULL"
                : "UPDATE ixiflow_publication_attempts SET heartbeat_at_utc=clock_timestamp()-INTERVAL '2 minutes' WHERE state='Running'");
    }

    private sealed class MissingArtifactBarrier(IArtifactStore inner, TaskCompletionSource entered, TaskCompletionSource release)
        : IArtifactStore
    {
        public async ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            return false;
        }

        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) =>
            inner.PutAsync(kind, content, cancellationToken);

        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
            inner.OpenAsync(digest, cancellationToken);

        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(digest, cancellationToken);

        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize,
            CancellationToken cancellationToken = default) =>
            inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    }
}
using System.Reflection;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Relational;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
public sealed class PublicationHistoryRound5Scenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PublicationTimestampTiesHaveTheSameCanonicalIdOrderOnEveryProvider(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.GetCatalogRevisionAsync();
        var generation = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var otherGeneration = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210");
        var time = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        // Canonical IDs increase while SQL Server's native GUID order decreases within each tie.
        // Fixed IDs and timestamps give every profile exactly the same data and expected sequence.
        var rows = Enumerable.Range(0, 3).SelectMany(batch => Enumerable.Range(0, 16).Select(index =>
            new WorkflowPublication(Guid.Parse($"{batch:x4}{index:x4}-0123-4567-89ab-{15-index:x12}"),
                new("workflow", "History", 1, index % 2 == 0 ? generation : otherGeneration, 1),
                batch * 16 + index + 1, WorkflowPublicationState.Failed, generation, "seed",
                time.AddSeconds(batch), time.AddSeconds(batch), time.AddSeconds(batch + 1), null))).ToArray();
        var decoys = new[]
        {
            rows[0] with { Id = Guid.Parse("a0000000-0000-0000-0000-000000000000"), Draft = rows[0].Draft with { Name = "Other" } },
            rows[0] with { Id = Guid.Parse("b0000000-0000-0000-0000-000000000000"), Draft = rows[0].Draft with { Version = 2 } },
            rows[0] with { Id = Guid.Parse("c0000000-0000-0000-0000-000000000000"), Draft = rows[0].Draft with { Kind = "activity", Name = "history" } }
        };
        if (store is InMemoryWorkflowStateRepository)
        {
            var attempts = (Dictionary<Guid, WorkflowPublication>)typeof(InMemoryWorkflowStateRepository)
                .GetField("_publications", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            foreach (var row in rows.Concat(decoys).Reverse()) attempts.Add(row.Id, row);
        }
        else
        {
            await using var connection = database.NewConnection();
            foreach (var row in rows.Concat(decoys).Reverse())
            {
                var parameters = typeof(RelationalWorkflowStateRepository).GetMethod("WithTimes", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(store, [new
                    {
                        row.Id, row.Draft.Kind, row.Draft.Name, row.Draft.Version, row.Draft.Generation, row.Draft.Revision,
                        row.Attempt, row.HostId, row.StartedBy
                    }, new (string, DateTime?)[] { ("StartedAtUtc", row.StartedAtUtc), ("HeartbeatAtUtc", row.HeartbeatAtUtc), ("FinishedAtUtc", row.FinishedAtUtc) }]);
                await connection.ExecuteAsync(database.Profile == StoreProfiles.DistributedSqlServer ? """
                    INSERT INTO dbo.IxIFlowPublicationAttempts
                        (Id, Kind, Name, Version, Generation, Revision, Attempt, State, HostId, StartedBy,
                         StartedAtUtc, HeartbeatAtUtc, FinishedAtUtc, OutcomeJson, CatalogRevision)
                    VALUES (@Id, @Kind, @Name, @Version, @Generation, @Revision, @Attempt, 'Failed', @HostId, @StartedBy,
                            @StartedAtUtc, @HeartbeatAtUtc, @FinishedAtUtc, NULL, 0)
                    """ : """
                    INSERT INTO ixiflow_publication_attempts
                        (id, kind, name, version, generation, revision, attempt, state, host_id, started_by,
                         started_at_utc, heartbeat_at_utc, finished_at_utc, outcome_json, catalog_revision)
                    VALUES (@Id, @Kind, @Name, @Version, @Generation, @Revision, @Attempt, 'Failed', @HostId, @StartedBy,
                            @StartedAtUtc, @HeartbeatAtUtc, @FinishedAtUtc, NULL, 0)
                    """, parameters);
            }
        }
        foreach (var selectedGeneration in new Guid?[] { null, generation, otherGeneration })
            foreach (var limit in new[] { 1, 5, 1000 })
            {
                List<Guid> actual = [];
                string? after = null;
                var query = new WorkflowPublicationQuery(CatalogScope.Unrestricted, "workflow", "History", 1,
                    Generation: selectedGeneration, Limit: limit);
                do
                {
                    var page = await store.GetPublicationsAsync(query with { After = after });
                    Assert.InRange(page.Items.Count, 1, limit);
                    actual.AddRange(page.Items.Select(row => row.Id));
                    Assert.True(actual.Count <= rows.Length, "A continuation repeated history rows");
                    after = page.NextCursor;
                } while (after != null);
                Assert.Equal(rows.Where(row => selectedGeneration == null || row.Draft.Generation == selectedGeneration)
                    .Select(row => row.Id), actual);
            }
        var denied = new CatalogScope(new([]), new(), new(), new());
        Assert.Empty((await store.GetPublicationsAsync(new(denied, "workflow", "History", 1, Limit: 1))).Items);
    }
}

using System.Collections;
using System.Data.Common;
using Dapper;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-10")]
[Trait("Spec", "F-11")]
[Trait("Spec", "F-15")]
[Trait("Spec", "H-05")]
public sealed class CatalogArtifactSecondRoundTests
{
    [Theory, InlineData(false), InlineData(true), Trait("Category", "SqlIntegration")]
    public Task SqlServerSeparateArtifactStore(bool memoryCatalog)
    {
        var cs = SqlTestDatabase<CatalogArtifactSecondRoundTests>.ConnectionString;
        return SeparateStoresAsync(new SqlWorkflowStateRepository(cs),
            memoryCatalog ? new InMemoryWorkflowStateRepository() : new SqlWorkflowStateRepository(cs),
            () => new SqlConnection(cs), true);
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlSeparateArtifactStore(bool memoryCatalog)
    {
        var cs = PostgreSqlTestDatabase<CatalogArtifactSecondRoundTests>.ConnectionString;
        return SeparateStoresAsync(new PostgreSqlWorkflowStateRepository(cs),
            memoryCatalog ? new InMemoryWorkflowStateRepository() : new PostgreSqlWorkflowStateRepository(cs),
            () => new NpgsqlConnection(cs), false);
    }

    private static async Task SeparateStoresAsync(IWorkflowStateRepository artifacts, IWorkflowStateRepository catalog,
        Func<DbConnection> connect, bool sqlServer)
    {
        var name = "Separate-" + Guid.NewGuid().ToString("N");
        var referenced = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream(Guid.NewGuid().ToByteArray()));
        var orphan = await artifacts.PutAsync(ArtifactKind.Template, new MemoryStream(Guid.NewGuid().ToByteArray()));
        await catalog.RegisterWorkflowVersionAsync(new(name, 1, "separate", ArtifactDigest: referenced), artifacts);
        await using (var connection = connect())
        {
            await connection.OpenAsync();
            await connection.ExecuteAsync(sqlServer
                ? "UPDATE dbo.IxIFlowArtifacts SET CreatedAtUtc=DATEADD(day,-91,CreatedAtUtc) WHERE Digest IN @Digests"
                : "UPDATE ixiflow_artifacts SET created_at_utc=created_at_utc - INTERVAL '91 days' WHERE digest = ANY(@Digests)",
                new { Digests = new[] { referenced.Value, orphan.Value } });
        }
        Assert.Equal(1, await catalog.PurgeArtifactsAsync(artifacts, TimeSpan.FromDays(90), 1));
        Assert.False(await artifacts.ExistsAsync(orphan));
        Assert.True(await artifacts.ExistsAsync(referenced));
        // Even an empty standalone pass must own and successfully commit a real transaction.
        Assert.Equal(0, await catalog.PurgeArtifactsAsync(artifacts, TimeSpan.FromDays(90), 1));
        Assert.True(await artifacts.ExistsAsync(referenced));
        await artifacts.DeleteAsync(referenced);
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "SqlIntegration")]
    public Task SqlServerArtifactVerificationUsesOneConnection(bool decorated)
    {
        var cs = new SqlConnectionStringBuilder(SqlTestDatabase<CatalogArtifactSecondRoundTests>.ConnectionString)
        {
            MaxPoolSize = 1, ConnectTimeout = 30, ApplicationName = "Catalog-r2-" + Guid.NewGuid().ToString("N")
        };
        return OneConnectionAsync(new SqlWorkflowStateRepository(cs.ConnectionString), decorated);
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlArtifactVerificationUsesOneConnection(bool decorated)
    {
        var cs = new NpgsqlConnectionStringBuilder(PostgreSqlTestDatabase<CatalogArtifactSecondRoundTests>.ConnectionString)
        {
            MaxPoolSize = 1, Timeout = 30, ApplicationName = "Catalog-r2-" + Guid.NewGuid().ToString("N")
        };
        return OneConnectionAsync(new PostgreSqlWorkflowStateRepository(cs.ConnectionString), decorated);
    }

    private static async Task OneConnectionAsync(IWorkflowStateRepository store, bool decorated)
    {
        var name = "OneConnection-" + Guid.NewGuid().ToString("N");
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(Guid.NewGuid().ToByteArray()));
        IArtifactStore artifacts = decorated ? new InspectingArtifacts(store, name) : store;
        await store.RegisterWorkflowVersionAsync(new(name, 1, "one", ArtifactDigest: digest), artifacts)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await store.RegisterWorkflowVersionAsync(new(name, 2, "two", WorkflowVersionState.Draft,
            ArtifactReferences: [digest]), artifacts).WaitAsync(TimeSpan.FromSeconds(5));
        await store.SetWorkflowVersionStateAsync(name, 2, WorkflowVersionState.Published, artifacts)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowVersionState.Published, (await store.GetWorkflowVersionAsync(name, 2))!.State);
        Assert.Equal(0, await store.PurgeArtifactsAsync(artifacts, TimeSpan.FromDays(90), 1)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        if (artifacts is InspectingArtifacts inspecting) Assert.Equal(3, inspecting.Checks);
    }

    [Fact, Trait("Category", "SqliteIntegration")]
    public async Task SqliteDecoratedOperationsReuseTheCatalogTransaction()
    {
        var folder = TemporaryFolder();
        try
        {
            await using var store = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "catalog.db"));
            await OneConnectionAsync(store, true);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    // Every member stays on the required store contract. Reads/writes forwarded from an artifact
    // decorator must retain the catalog transaction, including reads of its uncommitted changes.
    private sealed class InspectingArtifacts(IWorkflowStateRepository inner, string name) : IArtifactStore
    {
        public int Checks;
        public async ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
        {
            Checks++;
            Assert.True(await inner.ExistsAsync(digest, cancellationToken));
            await using var content = await inner.OpenAsync(digest, cancellationToken);
            Assert.NotNull(content);
            await inner.GetWorkflowVersionAsync(name, 1);
            await inner.GetWorkflowVersionsAsync(name);
            await inner.GetWorkflowVersionsAsync();
            var draft = await inner.GetDefinitionAsync(name, 99);
            var saved = await inner.SaveDefinitionAsync(new(name, 99, "workflow", "probe"), draft?.Revision ?? 0);
            Assert.Equal(saved, await inner.GetDefinitionAsync(name, 99));
            var scratch = await inner.PutAsync(ArtifactKind.Template, new MemoryStream(Guid.NewGuid().ToByteArray()), cancellationToken);
            await inner.DeleteAsync(scratch, cancellationToken);
            Assert.False(await inner.ExistsAsync(scratch, cancellationToken));
            Assert.Equal(0, await inner.PurgeUnreferencedAsync(new HashSet<ArtifactDigest>(), DateTime.UnixEpoch, 1, cancellationToken));
            return true;
        }
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) => inner.PutAsync(kind, content, cancellationToken);
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.OpenAsync(digest, cancellationToken);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.DeleteAsync(digest, cancellationToken);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) =>
            inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task RetentionStreamsOneCandidateAndProgressesPastReferences(bool fileSystem)
    {
        var folder = TemporaryFolder();
        try
        {
            IArtifactStore store = fileSystem ? new FileSystemArtifactStore(folder) : new InMemoryWorkflowStateRepository();
            var digests = new List<ArtifactDigest>();
            for (var i = 0; i < 256; i++)
                digests.Add(await store.PutAsync(ArtifactKind.Template, new MemoryStream(BitConverter.GetBytes(i))));
            // All entries are old relative to this cutoff; filesystem timestamps need no sleeps.
            var cutoff = DateTime.UtcNow.AddDays(1);
            var bounded = new ProbedReferences([], count => Assert.Equal(1, count));
            Assert.Equal(1, await store.PurgeUnreferencedAsync(bounded, cutoff, 1));
            Assert.Equal(1, bounded.Probes);
            var remaining = new List<ArtifactDigest>();
            foreach (var digest in digests)
                if (await store.ExistsAsync(digest)) remaining.Add(digest);
            var references = remaining.Take(240).ToHashSet();
            var orphans = remaining.Skip(240).ToArray();
            foreach (var _ in orphans)
                Assert.Equal(1, await store.PurgeUnreferencedAsync(references, cutoff, 1));
            Assert.Equal(0, await store.PurgeUnreferencedAsync(references, cutoff, 1));
            foreach (var digest in references) Assert.True(await store.ExistsAsync(digest));
            foreach (var digest in orphans) Assert.False(await store.ExistsAsync(digest));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task RetentionObservesCancellationWhileFilteringReferencedCandidates(bool fileSystem)
    {
        var folder = TemporaryFolder();
        try
        {
            IArtifactStore store = fileSystem ? new FileSystemArtifactStore(folder) : new InMemoryWorkflowStateRepository();
            var references = new HashSet<ArtifactDigest>();
            for (var i = 0; i < 256; i++)
                references.Add(await store.PutAsync(ArtifactKind.Template, new MemoryStream(BitConverter.GetBytes(i))));
            using var cancellation = new CancellationTokenSource();
            var probed = new ProbedReferences(references, _ => cancellation.Cancel());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.PurgeUnreferencedAsync(probed, DateTime.UtcNow.AddDays(1), 1, cancellation.Token).AsTask());
            Assert.Equal(1, probed.Probes);
            foreach (var digest in references) Assert.True(await store.ExistsAsync(digest));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task StagingCleanupRemovesOneStaleFilePerPass()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new FileSystemArtifactStore(folder);
            for (var i = 0; i < 64; i++)
            {
                var path = Path.Combine(folder, i + ".tmp");
                await File.WriteAllTextAsync(path, "abandoned");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
            }
            for (var remaining = 63; remaining >= 0; remaining--)
            {
                Assert.Equal(0, await store.PurgeUnreferencedAsync(new HashSet<ArtifactDigest>(), DateTime.UtcNow, 1));
                Assert.Equal(remaining, Directory.EnumerateFiles(folder, "*.tmp").Count());
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private sealed class ProbedReferences(HashSet<ArtifactDigest> values, Action<int> probe) : IReadOnlySet<ArtifactDigest>
    {
        public int Probes;
        public bool Contains(ArtifactDigest item) { probe(++Probes); return values.Contains(item); }
        public int Count => values.Count;
        public IEnumerator<ArtifactDigest> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<ArtifactDigest> other) => values.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<ArtifactDigest> other) => values.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<ArtifactDigest> other) => values.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<ArtifactDigest> other) => values.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<ArtifactDigest> other) => values.Overlaps(other);
        public bool SetEquals(IEnumerable<ArtifactDigest> other) => values.SetEquals(other);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ConcurrentUnversionedStartUsesTheWinningVersion(bool differentName)
    {
        var store = new InMemoryWorkflowStateRepository();
        var name = "Concurrent-" + Guid.NewGuid().ToString("N");
        var otherName = differentName ? name + "-other" : name;
        var catalog = new WorkflowCatalog(store, [Definition(name, 1)]);
        if (differentName) await catalog.RegisterWorkflowAsync(Definition(otherName, 1));
        var paused = new PausedLatestCatalog(catalog);
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowStateRepository>(store);
        services.AddSingleton<IWorkflowCatalog>(paused);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var options = new WorkflowOptions { InstanceId = Guid.NewGuid().ToString("N") };
        // The losing call has observed no instance, but has not selected a version yet.
        var retry = client.StartAsync(otherName, new Data(), options);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(options.InstanceId, await client.StartAsync(name, new Data(), options));
            await client.AwaitAsync(options.InstanceId);
            await catalog.RegisterWorkflowAsync(Definition(otherName, 2));
            if (differentName) await catalog.RegisterWorkflowAsync(Definition(name, 2));
        }
        finally { paused.Release.TrySetResult(); }
        if (differentName)
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => retry);
            Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        }
        else Assert.Equal(options.InstanceId, await retry.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, paused.SelectedVersion);
        Assert.Equal(1, (await store.GetWorkflowInstanceSummaryAsync(options.InstanceId))!.WorkflowVersion);
        // Explicit versions retain the existing conflict check.
        var conflict = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.StartAsync(name, 2, new Data(), options));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, conflict.Kind);
    }

    private sealed class PausedLatestCatalog(IWorkflowCatalog inner) : IWorkflowCatalog
    {
        private int _calls;
        public int? SelectedVersion;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WorkflowVersion?> GetLatestWorkflowVersionAsync(string name)
        {
            var pause = Interlocked.Increment(ref _calls) == 1;
            if (pause)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            var row = await inner.GetLatestWorkflowVersionAsync(name);
            if (pause) SelectedVersion = row?.Version;
            return row;
        }
        public Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowDefinitionsAsync() => inner.GetWorkflowDefinitionsAsync();
        public Task RegisterWorkflowAsync(WorkflowDefinition definition) => inner.RegisterWorkflowAsync(definition);
        public Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version) => inner.GetWorkflowDefinitionAsync(name, version);
        public Task<string?> GetWorkflowStructureHashAsync(string name, int version) => inner.GetWorkflowStructureHashAsync(name, version);
        public Task RetireWorkflowVersionAsync(string name, int version) => inner.RetireWorkflowVersionAsync(name, version);
        public Task<WorkflowDraft?> GetDefinitionAsync(string name, int version) => inner.GetDefinitionAsync(name, version);
        public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision) => inner.SaveDefinitionAsync(definition, expectedRevision);
        public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null) => inner.RegisterWorkflowVersionAsync(version, artifacts);
        public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) => inner.GetWorkflowVersionAsync(name, version);
        public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) => inner.GetWorkflowVersionsAsync(name);
        public Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, IArtifactStore? artifacts = null) => inner.SetWorkflowVersionStateAsync(name, version, state, artifacts);
        public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) => inner.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);
    }

    private static string TemporaryFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.PR67R2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static WorkflowDefinition Definition(string name, int version) => version == 1
        ? Workflow.Create<Data>(name, version).Step<Noop>().Build()
        : Workflow.Create<Data>(name, version).Step<Noop>().Step<Noop>().Build();
    public sealed class Data;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

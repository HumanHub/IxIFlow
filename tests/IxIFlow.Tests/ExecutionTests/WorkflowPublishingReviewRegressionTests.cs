using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dapper;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Generators;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-01")]
[Trait("Spec", "F-04")]
[Trait("Spec", "F-09")]
public sealed class WorkflowPublishingReviewRegressionTests
{
    public enum Scenario { Reupload, PurgedBeforeCommit, MissingBundle, MissingReference, ArtifactLock, Repeat, DraftChild, RetiredChild, ConcurrentPublish }
    public static TheoryData<Scenario, bool> Cases
    {
        get
        {
            var cases = new TheoryData<Scenario, bool>();
            foreach (var scenario in Enum.GetValues<Scenario>())
                foreach (var file in new[] { false, true }) cases.Add(scenario, file);
            return cases;
        }
    }

    [Theory, MemberData(nameof(Cases))]
    public Task Memory(Scenario scenario, bool file)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        return Verify(new InMemoryWorkflowStateRepository(time), scenario, file,
            _ => { time.Advance(TimeSpan.FromDays(91)); return Task.CompletedTask; });
    }

    [Theory, MemberData(nameof(Cases)), Trait("Category", "SqliteIntegration")]
    public async Task Sqlite(Scenario scenario, bool file)
    {
        var folder = Folder();
        try
        {
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using var store = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "catalog.db"), time);
            await Verify(store, scenario, file, _ => { time.Advance(TimeSpan.FromDays(91)); return Task.CompletedTask; });
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [Theory, MemberData(nameof(Cases)), Trait("Category", "SqlIntegration")]
    public async Task SqlServer(Scenario scenario, bool file)
    {
        var cs = SqlTestDatabase<WorkflowPublishingReviewRegressionTests>.ConnectionString;
        await Verify(new SqlWorkflowStateRepository(cs), scenario, file, async digest =>
        {
            await using var connection = new SqlConnection(cs);
            await connection.ExecuteAsync("UPDATE dbo.IxIFlowArtifacts SET CreatedAtUtc=DATEADD(day,-91,CreatedAtUtc) WHERE Digest=@Digest", new { Digest = digest.Value });
        });
    }

    [Theory, MemberData(nameof(Cases)), Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql(Scenario scenario, bool file)
    {
        var cs = PostgreSqlTestDatabase<WorkflowPublishingReviewRegressionTests>.ConnectionString;
        await Verify(new PostgreSqlWorkflowStateRepository(cs), scenario, file, async digest =>
        {
            await using var connection = new NpgsqlConnection(cs);
            await connection.ExecuteAsync("UPDATE ixiflow_artifacts SET created_at_utc=created_at_utc - INTERVAL '91 days' WHERE digest=@Digest", new { Digest = digest.Value });
        });
    }

    private static async Task Verify(IWorkflowStateRepository store, Scenario scenario, bool file, Func<ArtifactDigest, Task> ageStore)
    {
        var folder = Folder();
        try
        {
            IArtifactStore artifacts = file ? new FileSystemArtifactStore(folder) : store;
            var name = "PublishReview" + Guid.NewGuid().ToString("N");
            async Task Age(ArtifactDigest digest)
            {
                if (file)
                    foreach (var path in Directory.EnumerateFiles(folder, "*.artifact"))
                        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-91));
                else await ageStore(digest);
            }
            if (scenario == Scenario.ConcurrentPublish)
            {
                var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow", Yaml(name)), 0);
                var barrier = new CatalogBarrier(store);
                var losing = new WorkflowPublisher(barrier, artifacts, Options()).PublishAsync(draft.Id);
                await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                WorkflowPublishResult winner;
                try
                {
                    winner = await new WorkflowPublisher(store, artifacts, Options()).PublishAsync(draft.Id);
                    Assert.True(winner.Success, string.Join("; ", winner.Diagnostics));
                }
                finally { barrier.Release.TrySetResult(); }
                var loser = await losing.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(loser.Success, string.Join("; ", loser.Diagnostics));
                Assert.Equal(JsonSerializer.Serialize(winner.Version), JsonSerializer.Serialize(loser.Version));
                Assert.Null(await store.GetDefinitionAsync(name, 1));
                return;
            }
            if (scenario is Scenario.Reupload or Scenario.PurgedBeforeCommit)
            {
                var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow", Yaml(name)), 0);
                var orphan = new ArtifactProbe(artifacts) { AfterUpload = () => throw new InvalidOperationException("Interrupted after upload") };
                Assert.False((await new WorkflowPublisher(store, orphan, Options()).PublishAsync(draft.Id)).Success);
                Assert.True(await artifacts.ExistsAsync(orphan.Digest!.Value));
                await Age(orphan.Digest.Value);
                var observed = new ArtifactProbe(artifacts) { AfterUpload = async () =>
                {
                    // Force collection to win even after the duplicate-put age refresh.
                    if (scenario == Scenario.PurgedBeforeCommit) await Age(orphan.Digest.Value);
                    Assert.Equal(scenario == Scenario.Reupload ? 0 : 1,
                        await store.PurgeArtifactsAsync(artifacts, TimeSpan.FromDays(90), 100));
                } };
                var result = await new WorkflowPublisher(new WorkflowCatalog(store), observed, Options()).PublishAsync(draft.Id);
                Assert.Equal(orphan.Digest, observed.Digest);
                if (scenario == Scenario.Reupload)
                {
                    Assert.True(result.Success, string.Join("; ", result.Diagnostics));
                    Assert.True(await artifacts.ExistsAsync(result.Version!.ArtifactDigest!.Value));
                    Assert.Null(await store.GetDefinitionAsync(name, 1));
                }
                else
                {
                    Assert.False(result.Success);
                    Assert.Contains(result.Diagnostics, item => item.Stage == WorkflowPublishStage.Publish && item.Message.Contains("missing artifact"));
                    Assert.Equal(draft, await store.GetDefinitionAsync(name, 1));
                    Assert.Null(await store.GetWorkflowVersionAsync(name, 1));
                }
                return;
            }
            var source = await store.SaveDefinitionAsync(new(name, 1, "workflow", Yaml(name)), 0);
            var bytes = Encoding.UTF8.GetBytes(name);
            var digest = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
            var version = new WorkflowVersion(name, 1, "hash", ArtifactDigest: digest,
                PublishedDraftRevision: source.Revision, ManifestJson: "{}");
            if (scenario == Scenario.MissingBundle) await artifacts.DeleteAsync(digest);
            if (scenario == Scenario.MissingReference)
                version = version with { ArtifactReferences = [ArtifactDigest.Of([4, 5, 6])] };
            if (scenario is Scenario.DraftChild or Scenario.RetiredChild)
            {
                var child = name + "Child";
                await store.RegisterWorkflowVersionAsync(new(child, 1, "child", State: scenario == Scenario.DraftChild
                    ? WorkflowVersionState.Draft : WorkflowVersionState.Retired));
                version = version with { Dependencies = [new(child, 1)] };
            }
            if (scenario is Scenario.MissingBundle or Scenario.MissingReference or Scenario.DraftChild or Scenario.RetiredChild)
            {
                var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.PublishDefinitionAsync(source.Id, version, artifacts));
                Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
                Assert.Equal(source, await store.GetDefinitionAsync(name, 1));
                Assert.Null(await store.GetWorkflowVersionAsync(name, 1));
                return;
            }
            if (scenario == Scenario.ArtifactLock)
            {
                await Age(digest);
                var probe = new ArtifactProbe(artifacts) { PauseExists = true };
                var publishing = Task.Run(() => store.PublishDefinitionAsync(source.Id, version, probe));
                await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var purging = Task.Run(() => store.PurgeArtifactsAsync(artifacts, TimeSpan.FromDays(90), 100));
                try { await Task.Delay(100); Assert.False(purging.IsCompleted); }
                finally { probe.Release.TrySetResult(); }
                await publishing.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(0, await purging.WaitAsync(TimeSpan.FromSeconds(20)));
                Assert.True(await artifacts.ExistsAsync(digest));
                return;
            }
            var first = await store.PublishDefinitionAsync(source.Id, version, artifacts);
            Assert.True(await artifacts.ExistsAsync(first.ArtifactDigest!.Value));
            if (scenario == Scenario.Repeat)
            {
                var second = await store.PublishDefinitionAsync(source.Id, version with { StructureHash = "loser", ArtifactDigest = ArtifactDigest.Of([9]) }, artifacts);
                Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
                Assert.Null(await store.GetDefinitionAsync(name, 1));
                Assert.Single(await store.GetWorkflowVersionsAsync(name));
                var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.SaveDefinitionAsync(source, 0));
                Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static string Folder()
    {
        var path = Path.Combine(Path.GetTempPath(), "IxIFlow.PublishReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Yaml(string name) => $"workflow:\n  name: {name}\n  version: 1\n  dataType: Data\ndefinitions:\n  - kind: activity\n    activity: Echo\n";
    private static WorkflowPublisherOptions Options() => new()
    { SourceOptions = YamlTestContracts.Create(("Data", typeof(Data)), ("Echo", typeof(YamlMappingActivity))) };
    public sealed class Data { public string OrderId { get; set; } = ""; }

    [Theory]
    [InlineData("workflow.yaml:2147483648")]
    [InlineData("workflow.yaml:9999999999999999999999999999999")]
    [InlineData("workflow.yaml:0")]
    [InlineData("workflow.yaml:garbage")]
    public async Task OverflowingAndMalformedBuildPositionsReturnFailure(string message)
    {
        ThrowingDefaultAttribute.Message = message;
        var store = new InMemoryWorkflowStateRepository();
        var draft = await store.SaveDefinitionAsync(new("Overflow", 1, "workflow", Yaml("Overflow")), 0);
        var options = new WorkflowPublisherOptions { SourceOptions = YamlTestContracts.Create(("Data", typeof(Data)), ("Echo", typeof(ThrowingActivity))) };
        var result = await new WorkflowPublisher(store, store, options).PublishAsync(draft.Id);
        Assert.False(result.Success);
        var error = Assert.Single(result.Diagnostics, item => item.Severity == WorkflowDiagnosticSeverity.Error);
        Assert.Equal(WorkflowPublishStage.Test, error.Stage);
        Assert.Equal("PUB004", error.Code);
        Assert.Contains(message, error.Message);
        Assert.InRange(error.Line, 1, 7);
        Assert.Equal(draft, await store.GetDefinitionAsync(draft.Name, 1));
    }

    public sealed class ThrowingActivity : IAsyncActivity
    {
        [ThrowingDefault] public string Value { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class ThrowingDefaultAttribute() : DefaultValueAttribute(Fail())
    {
        public static string Message = "";
        private static object Fail() => throw new InvalidOperationException(Message);
    }

    [Fact]
    public async Task DiagnosticFormattingFailureIsContainedByThreadEntry()
    {
        var compilation = WorkflowCompilation.Compile("""
            public static class BrokenBuild
            {
                public static IxIFlow.Core.WorkflowDefinition Build() =>
                    throw new IxIFlow.Tests.ExecutionTests.WorkflowPublishingReviewRegressionTests.BrokenMessageException();
            }
            """);
        Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics));
        var result = await WorkflowBuild.RunAsync(compilation, new Dictionary<string, byte[]>(), TimeSpan.FromSeconds(10),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        Assert.Equal("Build() failed while inspecting its result", result.Error);
    }

    public sealed class BrokenMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("Cannot format message");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void CompilerColumnsPointAtYamlTargets(int indentation)
    {
        var indent = new string(' ', indentation);
        var yaml = $"workflow:\n{indent}name: Columns\n{indent}version: 1\n{indent}dataType: Data\ndefinitions:\n{indent}- kind: activity\n{indent}  activity: Echo\n{indent}  input:\n{indent}    - target: DoesNotExist\n{indent}      from: {{kind: constant, value: bad}}\n";
        var source = WorkflowSourceTranslator.Translate(yaml, "workflow.yaml", Options().SourceOptions);
        var result = WorkflowCompilation.Compile(source);
        var error = Assert.Single(result.Diagnostics, item => item.Code == "CS1061");
        Assert.Equal("workflow.yaml", error.Path);
        Assert.Equal(9, error.Line);
        Assert.Equal(indentation + 15, error.Column);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void CompilerColumnsPointAtYamlConstantValues(int indentation)
    {
        var indent = new string(' ', indentation);
        var yaml = $"workflow:\n{indent}name: Columns\n{indent}version: 1\n{indent}dataType: Data\ndefinitions:\n{indent}- kind: activity\n{indent}  activity: Echo\n{indent}  input:\n{indent}    - target: Value\n{indent}      from: {{kind: constant, value: true}}\n";
        var source = WorkflowSourceTranslator.Translate(yaml, "workflow.yaml", Options().SourceOptions);
        var result = WorkflowCompilation.Compile(source);
        var error = Assert.Single(result.Diagnostics, item => item.Code == "CS0029");
        Assert.Equal("workflow.yaml", error.Path);
        Assert.Equal(10, error.Line);
        Assert.Equal(yaml.Split('\n')[9].IndexOf("true", StringComparison.Ordinal) + 1, error.Column);
    }

    [Theory]
    [InlineData("Data", 4, 13)]
    [InlineData("Echo", 7, 15)]
    public void CompilerTypeErrorsPointAtYamlAliasValues(string key, int line, int column)
    {
        var options = Options().SourceOptions;
        options.Types[key] = "global::MissingApplicationType";
        var source = WorkflowSourceTranslator.Translate(Yaml("TypeColumns"), "workflow.yaml", options);
        var result = WorkflowCompilation.Compile(source);
        var error = Assert.Single(result.Diagnostics, item => item.Message.Contains("MissingApplicationType"));
        Assert.Equal("workflow.yaml", error.Path);
        Assert.Equal(line, error.Line);
        Assert.Equal(column, error.Column);
    }

    [Fact]
    public async Task AutoResolvedTypesHaveCamelCaseAliases()
    {
        var store = new InMemoryWorkflowStateRepository();
        var yaml = Yaml("CamelCase").Replace("dataType: Data", "dataType: " + typeof(Data).FullName!.Replace('+', '.'))
            .Replace("activity: Echo", "activity: " + typeof(YamlMappingActivity).FullName) +
            "    input:\n      - target: Value\n        from: {kind: path, path: workflow.orderId}\n";
        var draft = await store.SaveDefinitionAsync(new("CamelCase", 1, "workflow", yaml), 0);
        var result = await new WorkflowPublisher(store, store).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
    }

    private sealed class CatalogBarrier(IWorkflowCatalogStore inner) : IWorkflowCatalogStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RegisterPackageAsync(WorkflowPackage package, IArtifactStore artifacts) => inner.RegisterPackageAsync(package, artifacts);
        public Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null) => inner.GetPackagesAsync(id);
        public async Task<WorkflowDraft?> GetDefinitionAsync(string name, int version)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return await inner.GetDefinitionAsync(name, version);
        }
        public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision) => inner.SaveDefinitionAsync(definition, expectedRevision);
        public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null) => inner.RegisterWorkflowVersionAsync(version, artifacts);
        public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) => inner.GetWorkflowVersionAsync(name, version);
        public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) => inner.GetWorkflowVersionsAsync(name);
        public Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, IArtifactStore? artifacts = null) => inner.SetWorkflowVersionStateAsync(name, version, state, artifacts);
        public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore? artifacts = null) => inner.PublishDefinitionAsync(draftId, version, artifacts);
        public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) => inner.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);
    }

    private sealed class ArtifactProbe(IArtifactStore inner) : IArtifactStore
    {
        public Func<Task>? AfterUpload { get; init; }
        public ArtifactDigest? Digest { get; private set; }
        public bool PauseExists { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
        {
            var digest = await inner.PutAsync(kind, content, cancellationToken);
            Digest = digest;
            if (AfterUpload != null) await AfterUpload();
            return digest;
        }
        public async ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
        {
            if (PauseExists) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); }
            return await inner.ExistsAsync(digest, cancellationToken);
        }
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.OpenAsync(digest, cancellationToken);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.DeleteAsync(digest, cancellationToken);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) => inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    }
}

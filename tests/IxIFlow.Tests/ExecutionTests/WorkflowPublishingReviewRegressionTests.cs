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
                var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow", Yaml(name)), null);
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
                Assert.Equal(JsonSerializer.Serialize(winner.Workflow), JsonSerializer.Serialize(loser.Workflow));
                Assert.Null(await store.GetDefinitionAsync("workflow", name, 1));
                return;
            }
            if (scenario is Scenario.Reupload or Scenario.PurgedBeforeCommit)
            {
                var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow", Yaml(name)), null);
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
                    Assert.True(await artifacts.ExistsAsync(result.Workflow!.ArtifactDigest!.Value));
                    Assert.Null(await store.GetDefinitionAsync("workflow", name, 1));
                }
                else
                {
                    Assert.False(result.Success);
                    Assert.Contains(result.Diagnostics, item => item.Stage == WorkflowPublishStage.Publish && item.Code == "PUB041" && item.Class == WorkflowPublishDiagnosticClass.Unavailable);
                    Assert.Equal(draft, await store.GetDefinitionAsync("workflow", name, 1));
                    Assert.Null(await store.GetWorkflowVersionAsync(name, 1));
                }
                return;
            }
            var source = await store.SaveDefinitionAsync(new(name, 1, "workflow", Yaml(name)), null);
            var bytes = Encoding.UTF8.GetBytes(name);
            var digest = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
            var version = new WorkflowVersion(name, 1, "hash", WorkflowContract.OfType(typeof(object)).ToJson(), WorkflowContract.OfType(typeof(object)).Hash, Descriptor: new WorkflowDescriptor(name, "", [], null, null, IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(object)), IxIFlow.Core.Runtime.WorkflowTypeIdentity.CSharpName(typeof(object))), ArtifactDigest: digest,
                PublishedDraft: source.Id, ManifestJson: "{}", Invocations: []);
            if (scenario == Scenario.MissingBundle) await artifacts.DeleteAsync(digest);
            if (scenario == Scenario.MissingReference)
                version = version with { ArtifactReferences = [ArtifactDigest.Of([4, 5, 6])] };
            if (scenario is Scenario.DraftChild or Scenario.RetiredChild)
            {
                var child = name + "Child";
                await store.RegisterWorkflowVersionAsync(new(child, 1, "child", WorkflowContract.OfType(typeof(object)).ToJson(), WorkflowContract.OfType(typeof(object)).Hash, Descriptor: new WorkflowDescriptor(child, "", [], null, null, IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(object)), IxIFlow.Core.Runtime.WorkflowTypeIdentity.CSharpName(typeof(object))), State: scenario == Scenario.DraftChild
                    ? WorkflowVersionState.Draft : WorkflowVersionState.Retired, Invocations: []));
                version = version with { Dependencies = [new(child, 1)], Invocations = [new(child, 1, "invoke-child", "fixture")] };
            }
            if (scenario is Scenario.MissingBundle or Scenario.MissingReference or Scenario.DraftChild or Scenario.RetiredChild)
            {
                var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.PublishDefinitionAsync(source.Id, version, artifacts));
                Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
                Assert.Equal(source, await store.GetDefinitionAsync("workflow", name, 1));
                Assert.Null(await store.GetWorkflowVersionAsync(name, 1));
                return;
            }
            if (scenario == Scenario.ArtifactLock)
            {
                await Age(digest);
                var probe = new ArtifactProbe(artifacts) { PauseExists = true, PauseOnCheck = store is InMemoryWorkflowStateRepository ? 1 : 2 };
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
                Assert.Null(await store.GetDefinitionAsync("workflow", name, 1));
                Assert.Single(await store.GetWorkflowVersionsAsync(name));
                var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.SaveDefinitionAsync(source with { Generation = Guid.Empty, Revision = 0 }, null));
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
        var draft = await store.SaveDefinitionAsync(new("Overflow", 1, "workflow", Yaml("Overflow")), null);
        var options = new WorkflowPublisherOptions { SourceOptions = YamlTestContracts.Create(("Data", typeof(Data)), ("Echo", typeof(ThrowingActivity))) };
        var result = await new WorkflowPublisher(store, store, options).PublishAsync(draft.Id);
        Assert.False(result.Success);
        var error = Assert.Single(result.Diagnostics, item => item.Severity == WorkflowDiagnosticSeverity.Error);
        Assert.Equal(WorkflowPublishStage.Test, error.Stage);
        Assert.Equal("PUB004", error.Code);
        Assert.Contains(message, error.Message);
        var canonical = WorkflowYamlSerializer.Write(WorkflowYamlSerializer.Read(Yaml("Overflow")));
        Assert.InRange(error.Line, 1, canonical.Split('\n').Length);
        Assert.Equal(draft, await store.GetDefinitionAsync("workflow", draft.Name, 1));
    }

    public sealed class ThrowingActivity : IAsyncActivity
    {
        [ThrowingDefault] public string Value { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class ThrowingDefaultAttribute() : DefaultValueAttribute(Fail())
    {
        public static string Message
        {
            get => (string?)AppDomain.CurrentDomain.GetData(typeof(ThrowingDefaultAttribute).FullName!) ?? "";
            set => AppDomain.CurrentDomain.SetData(typeof(ThrowingDefaultAttribute).FullName!, value);
        }
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
        var fixture = typeof(BrokenMessageException).Assembly;
        var result = await WorkflowBuild.RunAsync(compilation, new Dictionary<string, byte[]> { [fixture.GetName().Name!] = File.ReadAllBytes(fixture.Location) }, TimeSpan.FromSeconds(10),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        Assert.Equal("An unexpected publication failure occurred", result.Error);
        Assert.Equal("PUB007", result.ErrorCode);
        Assert.Equal(WorkflowPublishDiagnosticClass.Internal, result.ErrorClass);
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
        var draft = await store.SaveDefinitionAsync(new("CamelCase", 1, "workflow", yaml), null);
        var result = await new WorkflowPublisher(store, store).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
    }

    private sealed class CatalogBarrier(IWorkflowCatalogStore inner) : IWorkflowCatalogStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CatalogPage<CatalogWorkflow>> ListWorkflowsAsync(CatalogWorkflowQuery query) => inner.ListWorkflowsAsync(query);
        public Task<CatalogPage<CatalogWorkflowVersion>> ListWorkflowVersionsAsync(string name, CatalogVersionQuery query) => inner.ListWorkflowVersionsAsync(name, query);
        public Task<CatalogWorkflowVersionDetail?> GetWorkflowVersionDetailAsync(string name, int version, CatalogScope scope) => inner.GetWorkflowVersionDetailAsync(name, version, scope);
        public Task<CatalogPage<CatalogActivity>> ListActivitiesAsync(CatalogActivityQuery query) => inner.ListActivitiesAsync(query);
    public Task<CatalogPage<CatalogActivity>> ListActivityVersionsAsync(string key, CatalogVersionQuery query) => inner.ListActivityVersionsAsync(key, query);
    public Task<CatalogActivityDetail?> GetActivityAsync(string key, string version, ActivityProviderSelector provider, CatalogScope scope) => inner.GetActivityAsync(key, version, provider, scope);
    public Task<CatalogPage<CatalogPackage>> ListPackagesAsync(CatalogPackageQuery query) => inner.ListPackagesAsync(query);
    public Task<CatalogPage<CatalogPackage>> ListPackageVersionsAsync(string id, CatalogVersionQuery query) => inner.ListPackageVersionsAsync(id, query);
    public Task<CatalogPackageDetail?> GetPackageDetailAsync(string id, string version, CatalogScope scope) => inner.GetPackageDetailAsync(id, version, scope);
    public Task<CatalogPage<CatalogActivity>> GetPackageActivitiesAsync(string id, string version, CatalogVersionQuery query) => inner.GetPackageActivitiesAsync(id, version, query);
    public Task<IReadOnlyList<WorkflowPackage>> GetPendingPackageDependentsAsync(string missingPackageId, string? afterIdentity, int limit) => inner.GetPendingPackageDependentsAsync(missingPackageId, afterIdentity, limit);
    public Task<IxIFlow.Templates.TemplateVersionEntry> RegisterTemplateVersionAsync(IxIFlow.Templates.TemplateVersion version, IArtifactStore artifacts, string? actor = null) => inner.RegisterTemplateVersionAsync(version, artifacts, actor);
    public Task<IxIFlow.Templates.TemplateVersionEntry?> GetTemplateVersionAsync(string key, int version) => inner.GetTemplateVersionAsync(key, version);
    public Task<IReadOnlyList<IxIFlow.Templates.TemplateVersionEntry>> GetTemplateVersionsAsync(string? key = null) => inner.GetTemplateVersionsAsync(key);
    public Task<IxIFlow.Templates.TemplateVersionEntry> PublishTemplateDefinitionAsync(WorkflowDraftId draftId, IxIFlow.Templates.TemplateVersion version, IArtifactStore artifacts, string? actor = null) => inner.PublishTemplateDefinitionAsync(draftId, version, artifacts, actor);
    public Task<PackageHolders> GetPackageHoldersAsync(string id, string version, CatalogScope scope) => inner.GetPackageHoldersAsync(id, version, scope);
    public Task<PackageRegistration> PublishPackageDefinitionAsync(WorkflowDraftId draftId, WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => inner.PublishPackageDefinitionAsync(draftId, package, activities, artifacts, actor);
    public Task<WorkflowPackage> SetPackageWithdrawnAsync(string id, string version, bool withdrawn, CatalogMutationToken expected, string? actor = null) => inner.SetPackageWithdrawnAsync(id, version, withdrawn, expected, actor);
    public Task<PackageRegistration> RegisterPackageAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => inner.RegisterPackageAsync(package, activities, artifacts, actor);
    public Task<PackageRegistration> CompletePackageExtractionAsync(string id, string version, ArtifactDigest expectedDigest, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => inner.CompletePackageExtractionAsync(id, version, expectedDigest, activities, artifacts, actor);
    public IArtifactStore? CatalogArtifacts { get => inner.CatalogArtifacts; set => inner.CatalogArtifacts = value; }
    public Task<IReadOnlyList<WorkflowPackage>> GetPackageExtractionPageAsync(PackageExtraction extraction, string? afterIdentity, int limit) => inner.GetPackageExtractionPageAsync(extraction, afterIdentity, limit);
        public Task<IReadOnlyList<InstalledActivityPackage>> GetInstalledPackagesAsync(string? id = null) => inner.GetInstalledPackagesAsync(id);
        public Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null) => inner.GetPackagesAsync(id);
        public Task<WorkflowPackage?> GetPackageAsync(string id, string version) => inner.GetPackageAsync(id, version);
        public Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit) => inner.GetPackagePageAsync(id, afterVersion, limit);
        public Task<bool> DeleteDefinitionAsync(WorkflowDraftId expected) => inner.DeleteDefinitionAsync(expected);
        public Task<CatalogPage<WorkflowDraftSummary>> GetDefinitionsAsync(WorkflowDraftQuery query) => inner.GetDefinitionsAsync(query);
        public Task<RetirementBlockers> GetRetirementBlockersAsync(string name, int version, CatalogScope scope) => inner.GetRetirementBlockersAsync(name, version, scope);
        public Task<WorkflowPublication> BeginPublicationAsync(WorkflowDraftId draftId, Guid hostId, string? actor, TimeSpan staleAfter) => inner.BeginPublicationAsync(draftId, hostId, actor, staleAfter);
        public Task<bool> HeartbeatPublicationAsync(Guid id, Guid hostId) => inner.HeartbeatPublicationAsync(id, hostId);
        public Task<WorkflowPublication> CompletePublicationAsync(Guid id, Guid hostId, WorkflowPublicationOutcome outcome) => inner.CompletePublicationAsync(id, hostId, outcome);
        public Task<WorkflowPublication?> GetPublicationAsync(Guid id, CatalogScope scope) => inner.GetPublicationAsync(id, scope);
        public Task<CatalogPage<WorkflowPublication>> GetPublicationsAsync(WorkflowPublicationQuery query) => inner.GetPublicationsAsync(query);
        public Task<int> PurgePublicationsAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) => inner.PurgePublicationsAsync(retention, batchSize, cancellationToken);
        public async Task<WorkflowDraft?> GetDefinitionAsync(string kind, string name, int version)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return await inner.GetDefinitionAsync(kind, name, version);
        }
        public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, WorkflowDraftId? expected, string? actor = null) => inner.SaveDefinitionAsync(definition, expected, actor);
        public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null) => inner.RegisterWorkflowVersionAsync(version, artifacts);
        public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) => inner.GetWorkflowVersionAsync(name, version);
        public Task<long> GetCatalogRevisionAsync() => inner.GetCatalogRevisionAsync();
        public Task<WorkflowCatalogChanges> GetWorkflowVersionChangesAsync(long afterRevision) => inner.GetWorkflowVersionChangesAsync(afterRevision);
        public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) => inner.GetWorkflowVersionsAsync(name);
        public Task<WorkflowVersion> SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, CatalogMutationToken expected, IArtifactStore? artifacts = null, string? actor = null) => inner.SetWorkflowVersionStateAsync(name, version, state, expected, artifacts, actor);
        public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore artifacts, string? actor = null) => inner.PublishDefinitionAsync(draftId, version, artifacts, actor);
        public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) => inner.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);
    }

    private sealed class ArtifactProbe(IArtifactStore inner) : IArtifactStore
    {
        public Func<Task>? AfterUpload { get; init; }
        public ArtifactDigest? Digest { get; private set; }
        public bool PauseExists { get; init; }
        public int PauseOnCheck { get; init; } = 1;
        private int _checks;
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
            if (PauseExists && Interlocked.Increment(ref _checks) == PauseOnCheck) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); }
            return await inner.ExistsAsync(digest, cancellationToken);
        }
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.OpenAsync(digest, cancellationToken);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.DeleteAsync(digest, cancellationToken);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) => inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    }
}

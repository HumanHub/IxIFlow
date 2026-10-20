using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-01")]
[Trait("Spec", "F-04")]
[Trait("Spec", "F-09")]
[Trait("Spec", "F-11")]
public sealed class WorkflowPublishingTests
{
    private const string Yaml = """
        workflow:
          name: Bundled
          version: 1
          dataType: Data
        definitions:
          - kind: activity
            id: echo
            activity: Echo
        """;

    private static WorkflowPublisherOptions Options() => new()
    {
        SourceOptions = YamlTestContracts.Create(("Data", typeof(Data)), ("Echo", typeof(YamlMappingActivity)))
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PublishRoundTripsArchiveAndCatalogAndRetry(bool sqlite) => WithStore(sqlite, RoundTrip);

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task PublishRoundTripsArchiveAndCatalogAndRetryOnSqlServer() =>
        RoundTrip(new SqlWorkflowStateRepository(SqlTestDatabase<RoundTripScope>.ConnectionString));

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PublishRoundTripsArchiveAndCatalogAndRetryOnPostgreSql() =>
        RoundTrip(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<RoundTripScope>.ConnectionString));

    private static async Task RoundTrip(IWorkflowStateRepository store)
    {
        var draft = await store.SaveDefinitionAsync(new("Bundled", 1, "workflow", Yaml), 0);
        Assert.Equal(WorkflowVersionState.Draft, draft.State);
        var catalog = new WorkflowCatalog(store);
        Assert.Null(await catalog.GetLatestWorkflowVersionAsync(draft.Name));
        Assert.Null(await catalog.GetWorkflowDefinitionAsync(draft.Name, draft.Version));
        var result = await new WorkflowPublisher(catalog, store, Options()).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var version = Assert.IsType<WorkflowVersion>(result.Version);
        Assert.Equal(WorkflowVersionState.Published, version.State);
        Assert.Equal(draft.Revision, version.PublishedDraftRevision);
        Assert.Null(await store.GetDefinitionAsync(draft.Name, draft.Version));
        Assert.Equal(version.ArtifactDigest, (await store.GetWorkflowVersionAsync(draft.Name, draft.Version))!.ArtifactDigest);
        await using var stored = await store.OpenAsync(version.ArtifactDigest!.Value);
        using var bytes = new MemoryStream();
        await stored!.CopyToAsync(bytes);
        Assert.Equal(version.ArtifactDigest, ArtifactDigest.Of(bytes.ToArray()));
        var bundle = await WorkflowBundleArchive.ReadAsync(new MemoryStream(bytes.ToArray()), version.ArtifactDigest.Value);
        Assert.Equal(version.StructureHash, bundle.Manifest.StructureHash);
        Assert.Equal("Bundled", bundle.Manifest.Name);
        Assert.Equal(1, bundle.Manifest.Version);
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(Data)), bundle.Manifest.DataType);
        Assert.Equal(WorkflowCompilation.Settings, bundle.Manifest.Compiler);
        Assert.Contains(typeof(WorkflowDefinition).Assembly.FullName!, bundle.Manifest.SharedAssemblies.Keys);
        Assert.DoesNotContain(typeof(Data).Assembly.FullName!, bundle.Manifest.SharedAssemblies.Keys);
        Assert.Contains("private/" + typeof(Data).Assembly.GetName().Name + ".dll", bundle.Entries.Keys);
        var expectedEntries = new HashSet<string>(StringComparer.Ordinal)
        {
            WorkflowBundleArchive.AssemblyPath, WorkflowBundleArchive.PdbPath,
            WorkflowBundleArchive.YamlPath, WorkflowBundleArchive.SourcePath
        };
        var pending = new Queue<Assembly>();
        expectedEntries.Add("private/" + typeof(Data).Assembly.GetName().Name + ".dll");
        pending.Enqueue(typeof(Data).Assembly);
        while (pending.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (!WorkflowSharedAssemblies.IsShared(reference) && expectedEntries.Add("private/" + reference.Name + ".dll"))
                    pending.Enqueue(Assembly.Load(reference));
            }
        }
        Assert.Equal(expectedEntries.Order(StringComparer.Ordinal), bundle.Entries.Keys.Order(StringComparer.Ordinal));
        foreach (var entry in bundle.Entries)
            Assert.Equal(bundle.Manifest.Entries[entry.Key], ArtifactDigest.Of(entry.Value.Span));
        Assert.Equal(WorkflowYamlSerializer.Write(WorkflowYamlSerializer.Read(Yaml)), Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.YamlPath].Span));
        Assert.Contains("#line", Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.SourcePath].Span));
        Assert.Equal(JsonSerializer.Serialize(bundle.Manifest), version.ManifestJson);
        // A fresh publisher/catalog sees the durable receipt after the draft has gone.
        var retry = await new WorkflowPublisher(new WorkflowCatalog(store), store, Options()).PublishAsync(draft.Id);
        Assert.True(retry.Success);
        Assert.Equal(version.ArtifactDigest, retry.Version!.ArtifactDigest);
        Assert.Single(await store.GetWorkflowVersionsAsync());
        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.SaveDefinitionAsync(draft, 0));
    }

    private sealed class RoundTripScope;

    [Theory]
    [InlineData("validate", WorkflowPublishStage.Validate, 4)]
    [InlineData("translate", WorkflowPublishStage.Translate, 2)]
    [InlineData("compile", WorkflowPublishStage.Compile, 10)]
    [InlineData("test", WorkflowPublishStage.Test, 6)]
    [InlineData("identity", WorkflowPublishStage.Test, 2)]
    [InlineData("imports", WorkflowPublishStage.Validate, 6)]
    public async Task FailuresKeepYamlPositionsAndLeaveDraftUntouched(string failure, WorkflowPublishStage stage, int line)
    {
        var yaml = failure switch
        {
            "validate" => Yaml.Replace("dataType: Data", "dataType: Unknown"),
            "translate" => Yaml,
            "compile" => Yaml + "\n    input:\n      - target: DoesNotExist\n        from: {kind: constant, value: bad}\n",
            "test" => Yaml,
            "identity" => Yaml.Replace("name: Bundled", "name: Different"),
            "imports" => "workflow:\n  name: Bundled\n  version: 1\n  dataType: Data\nimports:\n  catalogs: [package@1.0]\ndefinitions: []\n",
            _ => throw new ArgumentException(failure)
        };
        var options = Options();
        if (failure == "translate") options.SourceOptions.ClassName = "bad-class";
        if (failure == "test") YamlTestContracts.AddType(options.SourceOptions, "Echo", typeof(ActivityContractTests.RequiredText));
        var store = new InMemoryWorkflowStateRepository();
        var draft = await store.SaveDefinitionAsync(new("Bundled", 1, "workflow", yaml), 0);
        var upload = new ObservedArtifacts(store);
        var result = await new WorkflowPublisher(store, upload, options).PublishAsync(draft.Id);
        Assert.False(result.Success);
        var error = Assert.Single(result.Diagnostics, item => item.Severity == WorkflowDiagnosticSeverity.Error);
        Assert.Equal(stage, error.Stage);
        Assert.Equal("workflow.yaml", error.Path);
        Assert.Equal(line, error.Line);
        Assert.True(error.Column > 0);
        Assert.Equal(draft, await store.GetDefinitionAsync(draft.Name, draft.Version));
        Assert.Empty(await store.GetWorkflowVersionsAsync());
        Assert.Equal(0, upload.Uploads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConcurrentEditAfterUploadRefusesTheStaleRevision(bool sqlite) => WithStore(sqlite, StaleRevision);

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task ConcurrentEditAfterUploadRefusesTheStaleRevisionOnSqlServer() =>
        StaleRevision(new SqlWorkflowStateRepository(SqlTestDatabase<StaleRevisionScope>.ConnectionString));

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task ConcurrentEditAfterUploadRefusesTheStaleRevisionOnPostgreSql() =>
        StaleRevision(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<StaleRevisionScope>.ConnectionString));

    private static async Task StaleRevision(IWorkflowStateRepository store)
    {
        var draft = await store.SaveDefinitionAsync(new("Bundled", 1, "workflow", Yaml), 0);
        var upload = new ObservedArtifacts(store, async () =>
        {
            Assert.Null(await store.GetWorkflowVersionAsync(draft.Name, draft.Version));
            Assert.NotNull(await store.GetDefinitionAsync(draft.Name, draft.Version));
            await store.SaveDefinitionAsync(draft with { Source = Yaml + "\n# edited" }, draft.Revision);
        });
        var result = await new WorkflowPublisher(store, upload, Options()).PublishAsync(draft.Id);
        Assert.False(result.Success);
        Assert.Equal(WorkflowPublishStage.Publish, Assert.Single(result.Diagnostics).Stage);
        Assert.Equal(2, (await store.GetDefinitionAsync(draft.Name, draft.Version))!.Revision);
        Assert.Empty(await store.GetWorkflowVersionsAsync());
        Assert.True(await store.ExistsAsync(upload.Digest!.Value));
    }

    private sealed class StaleRevisionScope;

    [Fact]
    public async Task SameRevisionConcurrentPublicationsAreIdempotent()
    {
        var store = new InMemoryWorkflowStateRepository();
        var draft = await store.SaveDefinitionAsync(new("Bundled", 1, "workflow", Yaml), 0);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(index =>
        {
            var options = Options();
            options.SourceOptions.Namespace = "Publisher" + index;
            return new WorkflowPublisher(store, store, options).PublishAsync(draft.Id);
        }));
        Assert.All(results, result => Assert.True(result.Success, string.Join("; ", result.Diagnostics)));
        Assert.Equal(results[0].Version!.ArtifactDigest, results[1].Version!.ArtifactDigest);
        Assert.Single(await store.GetWorkflowVersionsAsync());
        Assert.Null(await store.GetDefinitionAsync(draft.Name, draft.Version));
    }

    [Fact]
    public async Task LoadedApplicationTypesResolveWithoutAliases()
    {
        var yaml = Yaml.Replace("dataType: Data", "dataType: " + typeof(Data).FullName!.Replace('+', '.'))
            .Replace("activity: Echo", "activity: " + typeof(YamlMappingActivity).FullName);
        var store = new InMemoryWorkflowStateRepository();
        var draft = await store.SaveDefinitionAsync(new("Bundled", 1, "workflow", yaml), 0);
        var result = await new WorkflowPublisher(store, store).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public async Task HangingBuildFailsPublicationAtTheDeadlineAndLeavesItsThreadAndContextBehind()
    {
        var store = new InMemoryWorkflowStateRepository();
        var draft = await store.SaveDefinitionAsync(new("Bundled", 1, "workflow", Yaml), 0);
        var upload = new ObservedArtifacts(store);
        var timeout = TimeSpan.FromSeconds(1);
        var options = new WorkflowPublisherOptions
        {
            BuildTimeout = timeout,
            SourceOptions = YamlTestContracts.Create(("Data", typeof(Data)), ("Echo", typeof(HangingBuildActivity)))
        };
        var logger = new BuildLogger();
        var timer = Stopwatch.StartNew();
        var publication = new WorkflowPublisher(store, upload, options, logger).PublishAsync(draft.Id);
        var (thread, context) = await HangingDefaultAttribute.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var result = await publication.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.Success);
        Assert.Null(result.Version);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(WorkflowPublishStage.Test, error.Stage);
        Assert.Equal(WorkflowDiagnosticSeverity.Error, error.Severity);
        Assert.Equal("PUB004", error.Code);
        Assert.Equal($"Build() exceeded the publish timeout of {timeout}", error.Message);
        Assert.Equal("workflow.yaml", error.Path);
        Assert.Equal(WorkflowYamlSerializer.Read(Yaml).Workflow.Location.Line, error.Line);
        Assert.True(timer.Elapsed >= timeout);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15));
        Assert.True(thread.IsAlive);
        Assert.True(thread.IsBackground);
        Assert.False(thread.IsThreadPoolThread);
        Assert.True(context.IsCollectible);
        Assert.False(HangingDefaultAttribute.Unloading);
        var warning = Assert.Single(logger.Messages);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("thread", warning.Message);
        Assert.Contains("load context are abandoned until the process restarts", warning.Message);
        Assert.Equal(draft, await store.GetDefinitionAsync(draft.Name, draft.Version));
        Assert.Empty(await store.GetWorkflowVersionsAsync());
        Assert.Equal(0, upload.Uploads);

        // The abandoned build does not prevent a normal publication in the same process.
        var published = await new WorkflowPublisher(store, store, Options()).PublishAsync(draft.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
    }

    public sealed class HangingBuildActivity : IAsyncActivity
    {
        [HangingDefault] public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class HangingDefaultAttribute : DefaultValueAttribute
    {
        public static readonly TaskCompletionSource<(Thread Thread, AssemblyLoadContext Context)> Entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static bool Unloading;

        public HangingDefaultAttribute() : base(0)
        {
            var assembly = new StackTrace().GetFrames().Select(frame => frame.GetMethod()?.DeclaringType?.Assembly)
                .First(assembly => assembly?.IsCollectible == true)!;
            var context = AssemblyLoadContext.GetLoadContext(assembly)!;
            context.Unloading += _ => Unloading = true;
            Entered.TrySetResult((Thread.CurrentThread, context));
            Thread.Sleep(Timeout.Infinite);
        }
    }

    private sealed class BuildLogger : ILogger<WorkflowPublisher>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void CompilerAndArchiveAreDeterministic()
    {
        var source = WorkflowSourceTranslator.Translate(Yaml, "workflow.yaml", Options().SourceOptions);
        var references = WorkflowCompilation.ReferencePaths();
        var first = WorkflowCompilation.Compile(source, references);
        var second = WorkflowCompilation.Compile(source, references);
        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.Assembly, second.Assembly);
        Assert.Equal(first.Pdb, second.Pdb);
        var manifest = new WorkflowBundleManifest(1, "Test", 1, "hash", "data", "entry", "1.0", 4, "",
            WorkflowCompilation.Settings, new Dictionary<string, string>(), new Dictionary<string, ArtifactDigest>());
        var entries = new Dictionary<string, byte[]> { ["workflow.dll"] = first.Assembly, ["workflow.pdb"] = first.Pdb,
            ["workflow.yaml"] = Encoding.UTF8.GetBytes(Yaml), ["workflow.g.cs"] = Encoding.UTF8.GetBytes(source) };
        Assert.Equal(WorkflowBundleArchive.Create(manifest, entries), WorkflowBundleArchive.Create(manifest, entries));
    }

    [Theory]
    [InlineData("entry")]
    [InlineData("archive")]
    [InlineData("duplicate")]
    [InlineData("case-duplicate")]
    [InlineData("escape")]
    [InlineData("absolute")]
    [InlineData("backslash")]
    [InlineData("unlisted")]
    [InlineData("missing")]
    public async Task ReaderRejectsCorruptionAndUnsafePaths(string corruption)
    {
        var entries = new Dictionary<string, byte[]>
        {
            ["workflow.dll"] = [1], ["workflow.pdb"] = [2], ["workflow.yaml"] = [3], ["workflow.g.cs"] = [4]
        };
        var manifest = new WorkflowBundleManifest(1, "Test", 1, "hash", "data", "entry", "1.0", 4, "",
            WorkflowCompilation.Settings, new Dictionary<string, string>(), new Dictionary<string, ArtifactDigest>());
        var bytes = WorkflowBundleArchive.Create(manifest, entries);
        var expected = ArtifactDigest.Of(bytes);
        using var mutated = new MemoryStream();
        mutated.Write(bytes);
        using (var zip = new ZipArchive(mutated, ZipArchiveMode.Update, leaveOpen: true))
        {
            if (corruption is "entry" or "archive")
            {
                using var content = zip.GetEntry("workflow.yaml")!.Open();
                content.WriteByte(9);
            }
            else if (corruption == "missing") zip.GetEntry("workflow.pdb")!.Delete();
            else zip.CreateEntry(corruption switch
            {
                "duplicate" => "workflow.dll", "case-duplicate" => "WORKFLOW.DLL", "escape" => "../escape.dll",
                "absolute" => "/escape.dll", "backslash" => "..\\escape.dll", _ => "extra.dll"
            });
        }
        bytes = mutated.ToArray();
        if (corruption != "archive") expected = ArtifactDigest.Of(bytes); // Reach per-entry/path verification after valid archive hashing.
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => WorkflowBundleArchive.ReadAsync(new MemoryStream(bytes), expected));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
        Assert.True(error.IsInterruption);
    }

    private static async Task WithStore(bool sqlite, Func<IWorkflowStateRepository, Task> test)
    {
        if (!sqlite) { await test(new InMemoryWorkflowStateRepository()); return; }
        var directory = Path.Combine(Path.GetTempPath(), "IxIFlow.PublishTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(directory, "catalog.db"));
            await test(store);
        }
        finally { await SqliteTestFiles.DeleteAsync(directory); }
    }

    public sealed class Data { public string Value { get; set; } = ""; }

    private sealed class ObservedArtifacts(IArtifactStore store, Func<Task>? afterUpload = null) : IArtifactStore
    {
        public int Uploads { get; private set; }
        public ArtifactDigest? Digest { get; private set; }
        public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
        {
            Uploads++;
            Digest = await store.PutAsync(kind, content, cancellationToken);
            if (afterUpload != null) await afterUpload();
            return Digest.Value;
        }
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => store.OpenAsync(digest, cancellationToken);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => store.ExistsAsync(digest, cancellationToken);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) =>
            store.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => store.DeleteAsync(digest, cancellationToken);
    }
}

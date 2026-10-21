using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
public sealed class BundleRound3Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IxIFlow.BundleRound3", Guid.NewGuid().ToString("N"));
    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DepsDiscoveryIncludesEveryFrameworkAndExcludesApplicationLibraries(bool selfContained)
    {
        var app = Folder("shared/app/1.0");
        var core = Folder("frameworks/core");
        var asp = Folder("frameworks/asp");
        var application = typeof(FirstData).Assembly;
        var runtime = new[] { typeof(object).Assembly, Assembly.Load("System.Runtime") };
        var web = typeof(Microsoft.AspNetCore.Http.HttpContext).Assembly;
        void Copy(string directory, Assembly assembly) => File.Copy(assembly.Location, Path.Combine(directory, Path.GetFileName(assembly.Location)), overwrite: true);
        string Manifest(string directory, string name, Dictionary<string, string[]> libraries)
        {
            var path = Path.Combine(directory, name + ".deps.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                runtimeTarget = new { name = "fixture/rid" },
                targets = new Dictionary<string, object> { ["fixture/rid"] = libraries.ToDictionary(pair => pair.Key,
                    pair => new { runtime = pair.Value.ToDictionary(asset => asset, _ => new { }) }) },
                libraries = libraries.ToDictionary(pair => pair.Key, pair => new { type = pair.Key == "Application/1" ? "project" : "package" })
            }));
            return path;
        }
        Copy(app, application);
        var applicationLibraries = new Dictionary<string, string[]> { ["Application/1"] = [Path.GetFileName(application.Location)] };
        string deps;
        if (selfContained)
        {
            foreach (var assembly in runtime.Append(web)) Copy(app, assembly);
            applicationLibraries["runtimepack.Microsoft.NETCore.App.Runtime.fixture/1"] = runtime.Select(a => "runtimes/fixture/lib/net10.0/" + Path.GetFileName(a.Location)).Distinct().ToArray();
            applicationLibraries["runtimepack.Microsoft.AspNetCore.App.Runtime.fixture/1"] = [Path.GetFileName(web.Location)];
            deps = Manifest(app, "Application", applicationLibraries);
        }
        else
        {
            foreach (var assembly in runtime) Copy(core, assembly);
            Copy(core, application); // Being beside CoreLib is not sufficient to be shared.
            Copy(asp, web);
            deps = string.Join(';', Manifest(app, "Application", applicationLibraries),
                Manifest(core, "Core", new() { ["Core/1"] = runtime.Select(a => Path.GetFileName(a.Location)).Distinct().ToArray() }),
                Manifest(asp, "AspNet", new() { ["AspNet/1"] = [Path.GetFileName(web.Location)] }));
        }
        var profile = WorkflowSharedAssemblies.RuntimeAssemblies(deps, app);
        Assert.Contains("System.Private.CoreLib", profile.Keys);
        Assert.Contains("System.Runtime", profile.Keys);
        Assert.Contains(web.GetName().Name!, profile.Keys);
        Assert.DoesNotContain(application.GetName().Name!, profile.Keys);
        Assert.Equal(typeof(object).Assembly.GetName().FullName, profile["System.Private.CoreLib"].FullName);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("asset")]
    [InlineData("empty")]
    public void FailedDepsDiscoveryReturnsAnEmptyRuntimeSet(string failure)
    {
        var app = Folder("app");
        var file = Path.Combine(app, "Application.deps.json");
        if (failure == "invalid") File.WriteAllText(file, "{");
        if (failure == "asset")
        {
            File.Copy(typeof(object).Assembly.Location, Path.Combine(app, "System.Private.CoreLib.dll"));
            File.WriteAllText(file, """
                {"runtimeTarget":{"name":"fixture"},"libraries":{},"targets":{"fixture":{"runtimepack.Core/1":{"runtime":{"System.Private.CoreLib.dll":{},"Missing.dll":{}}}}}}
                """);
        }
        Assert.Empty(WorkflowSharedAssemblies.RuntimeAssemblies(failure == "empty" ? null : file, app));
    }

    [Fact]
    public async Task CacheLockDeadlineBypassesPersistentIoFailureWithVerifiedBytes()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var folder = Folder("locked");
        var digest = version.ArtifactDigest!.Value;
        var path = Path.Combine(folder, digest.Value[7..] + ".bundle");
        using var held = new FileStream(path + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(folder)
        { CacheLockTimeout = TimeSpan.FromMilliseconds(100), FetchTimeout = TimeSpan.FromSeconds(5) });
        using var lease = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(digest, lease.Definition.BundleDigest);
        Assert.False(File.Exists(path));
        Assert.Throws<IOException>(() => new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None));
    }

    [Fact]
    public async Task AReadOnlyCacheDirectoryStillAllowsAVerifiedDownload()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var folder = Folder("readonly");
        DirectorySecurity? original = null;
        UnixFileMode mode = default;
        if (OperatingSystem.IsWindows())
        {
            var directory = new DirectoryInfo(folder);
            original = directory.GetAccessControl();
            var restricted = directory.GetAccessControl();
            restricted.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny));
            directory.SetAccessControl(restricted);
        }
        else
        {
            mode = File.GetUnixFileMode(folder);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        try
        {
            using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(folder));
            using var lease = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(version.ArtifactDigest, lease.Definition.BundleDigest);
            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            if (OperatingSystem.IsWindows()) new DirectoryInfo(folder).SetAccessControl(original!);
            else File.SetUnixFileMode(folder, mode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentArtifactStoreCancellationIsUnavailableAndRetryable(bool taskCancelled)
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var artifacts = new Mock<IArtifactStore>();
        var error = taskCancelled ? new TaskCanceledException("HTTP timeout") : new OperationCanceledException("store cancelled");
        artifacts.Setup(a => a.OpenAsync(It.IsAny<ArtifactDigest>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromException<Stream?>(error));
        using var caller = new CancellationTokenSource();
        using var catalog = new WorkflowCatalog(store, artifacts: artifacts.Object, bundles: new(Folder("timeout")));
        var failure = await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.ResolveWorkflowDefinitionAsync(version.Name, 1, caller.Token));
        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, failure.Kind);
        Assert.Same(error, failure.InnerException);
        Assert.True(failure.IsInterruption);
        Assert.False(caller.IsCancellationRequested);
        artifacts.Setup(a => a.OpenAsync(It.IsAny<ArtifactDigest>(), It.IsAny<CancellationToken>()))
            .Returns((ArtifactDigest digest, CancellationToken token) => store.OpenAsync(digest, token));
        using var retry = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1, caller.Token);
        Assert.Equal(version.ArtifactDigest, retry.Definition.BundleDigest);
    }

    [Fact]
    public async Task OversizedPoisonedCacheIsEvictedAndCanBeFetchedAgain()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var folder = Folder("poisoned");
        var path = Path.Combine(folder, version.ArtifactDigest!.Value.Value[7..] + ".bundle");
        using (var poison = File.Create(path)) poison.SetLength(64 * 1024 * 1024 + 1);
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(folder));
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.ResolveWorkflowDefinitionAsync(version.Name, 1));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
        Assert.False(File.Exists(path));
        using var recovered = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1);
        Assert.Equal(version.ArtifactDigest, recovered.Definition.BundleDigest);
    }

    [Fact]
    public async Task AnEntryLyingAboutItsLengthIsCorruptBeforeItExhaustsTheLimit()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open())
            entry.Write(new byte[100]);
        var bytes = stream.ToArray();
        // Central directory's uncompressed length lies; the actual first read exceeds both it and the limit.
        for (var i = 0; i < bytes.Length - 28; i++)
            if (BitConverter.ToUInt32(bytes, i) == 0x02014b50) { BitConverter.GetBytes(1).CopyTo(bytes, i + 24); break; }
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => WorkflowBundleArchive.ReadAsync(new MemoryStream(bytes),
            ArtifactDigest.Of(bytes), limits: new() { MaxManifestBytes = 10 }));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DemandRefreshIsThrottledAndFailureHandsOff(bool fail, bool sqlite)
    {
        await using var sqliteStore = sqlite ? new SqliteWorkflowStateRepository(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(Folder("db"), "state.db"), Pooling = false }.ConnectionString) : null;
        IWorkflowStateRepository store = sqliteStore ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository();
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(Folder("catalog")));
        var counted = DispatchProxy.Create<IWorkflowCatalog, CatalogProbe>();
        var probe = (CatalogProbe)(object)counted;
        probe.Inner = catalog;
        var time = new FakeTimeProvider();
        await using var host = PublishedBundleScenario.Host(store, Folder("host"), configure: services =>
        { services.AddSingleton(counted); services.AddSingleton<TimeProvider>(time); });
        var settings = host.GetRequiredService<HostSettings>();
        await settings.GetCapabilitiesAsync(counted);
        var version = await PublishedBundleScenario.PublishAsync(store);
        probe.FailVersions = fail;
        // An incompatible manifest leaves the remote workflow demand unmet after a successful refresh.
        probe.HideVersions = !fail;
        for (var i = 0; i < 3; i++)
        {
            var id = await host.GetRequiredService<IWorkflowClient>().StartAsync(version.Name, new FirstData());
            await host.GetRequiredService<WorkflowHost>().RunAsync(id,
                (_, _, _) => Task.FromResult(new WorkflowExecutionResult { InstanceId = id }), CancellationToken.None);
            Assert.Equal(WorkflowStatus.Running, (await store.GetWorkflowInstanceSummaryAsync(id))!.Status);
        }
        Assert.Equal(2, probe.VersionReads);
        Assert.Equal(2, probe.RevisionReads);
        time.Advance(settings.HeartbeatInterval);
        var refreshed = await settings.RefreshDemandCapabilitiesAsync(counted, time);
        if (fail) Assert.Null(refreshed);
        else
        {
            Assert.NotNull(refreshed);
            Assert.DoesNotContain("workflow:BundleLifecycle@1", refreshed.Keys);
        }
        // Success cached the unchanged revision; failure still needs to retry the delta.
        Assert.Equal(fail ? 3 : 2, probe.VersionReads);
        Assert.Equal(3, probe.RevisionReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnInflightDemandRefreshDoesNotQueueOtherPlacementReads(bool sqlite)
    {
        await using var sqliteStore = sqlite ? new SqliteWorkflowStateRepository(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(Folder("db"), "state.db"), Pooling = false }.ConnectionString) : null;
        IWorkflowStateRepository store = sqliteStore ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository();
        using var catalog = new WorkflowCatalog(store);
        var counted = DispatchProxy.Create<IWorkflowCatalog, CatalogProbe>();
        var probe = (CatalogProbe)(object)counted;
        probe.Inner = catalog;
        var settings = HostSettings.Default with { BundleHostingEnabled = true };
        var snapshot = await settings.GetCapabilitiesAsync(counted);
        var version = await PublishedBundleScenario.PublishAsync(store);
        probe.PausedVersions = new(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.VersionsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = settings.RefreshDemandCapabilitiesAsync(counted, TimeProvider.System);
        try
        {
            await probe.VersionsEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(refresh.IsCompleted);
            Assert.Null(await settings.RefreshDemandCapabilitiesAsync(counted, TimeProvider.System).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Same(snapshot, await settings.GetCapabilitiesAsync(counted).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(2, probe.VersionReads);
            Assert.Equal(2, probe.RevisionReads);
        }
        finally { probe.PausedVersions.TrySetResult([]); }
        var refreshed = await refresh;
        Assert.NotNull(refreshed);
        Assert.Equal(version.StructureHash, refreshed[$"workflow:{version.Name}@{version.Version}"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompatibilityFailuresAreMemoisedAsFalseLoggedOnceAndRemovedWithTheirVersions(bool sqlite)
    {
        await using var sqliteStore = sqlite ? new SqliteWorkflowStateRepository(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(Folder("db"), "state.db"), Pooling = false }.ConnectionString) : null;
        IWorkflowStateRepository store = sqliteStore ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        using var catalog = new WorkflowCatalog(store);
        var counted = DispatchProxy.Create<IWorkflowCatalog, CatalogProbe>();
        var probe = (CatalogProbe)(object)counted;
        probe.Inner = catalog;
        var logger = new RecordingLogger();
        var settings = HostSettings.Default with { BundleHostingEnabled = true, CapabilityLogger = logger };
        // This passes basic compatibility checks, then raises a non-JSON exception in validation.
        probe.VersionRows = [version with { ManifestJson = JsonSerializer.Serialize(
            JsonSerializer.Deserialize<WorkflowBundleManifest>(version.ManifestJson!)! with { SharedAssemblies = null! }) }];
        Lazy<bool>? cached = null;
        for (var i = 0; i < 2; i++)
        {
            // Revisit the same corrupt manifest through an actual changed-row delta.
            if (i != 0) await store.SetWorkflowVersionStateAsync(version.Name, version.Version, WorkflowVersionState.Deprecated);
            Assert.DoesNotContain("workflow:BundleLifecycle@1", (await settings.RefreshCapabilitiesAsync(counted)).Keys);
            var entry = Assert.Single(MemoEntries(settings.Compatibility)).Value;
            if (cached != null) Assert.Same(cached, entry);
            cached = entry;
        }
        Assert.Equal(2, probe.VersionReads);
        Assert.Equal(2, probe.RevisionReads);
        Assert.Equal(1, logger.Warnings);
        Assert.Single(MemoEntries(settings.Compatibility));
        Assert.False(Assert.Single(MemoEntries(settings.Compatibility)).Value.Value);
        // Rows are retained by the store; a policy view withdraws this manifest's last reference.
        probe.VersionRows = [version with { State = WorkflowVersionState.Retired, ManifestJson = null }];
        await store.SetWorkflowVersionStateAsync(version.Name, version.Version, WorkflowVersionState.Retired);
        Assert.DoesNotContain("workflow:BundleLifecycle@1", (await settings.RefreshCapabilitiesAsync(counted)).Keys);
        Assert.Equal(3, probe.VersionReads);
        Assert.Equal(3, probe.RevisionReads);
        Assert.Equal(1, logger.Warnings);
        Assert.Empty(MemoEntries(settings.Compatibility));
    }

    private static ConcurrentDictionary<string, Lazy<bool>> MemoEntries(WorkflowHeartbeatService.CompatibilityMemo memo) =>
        (ConcurrentDictionary<string, Lazy<bool>>)typeof(WorkflowHeartbeatService.CompatibilityMemo)
            .GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(memo)!;

    private sealed class RecordingLogger : ILogger<WorkflowHeartbeatService>
    {
        public int Warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        { if (level == LogLevel.Warning) Warnings++; }
    }

    public class CatalogProbe : DispatchProxy
    {
        public IWorkflowCatalog Inner = null!;
        public int VersionReads;
        public int RevisionReads;
        public bool FailVersions;
        public bool HideVersions;
        public IReadOnlyList<WorkflowVersion>? VersionRows;
        public TaskCompletionSource<IReadOnlyList<WorkflowVersion>>? PausedVersions;
        public TaskCompletionSource? VersionsEntered;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowCatalog.GetCatalogRevisionAsync))
                Interlocked.Increment(ref RevisionReads);
            if (method.Name == nameof(IWorkflowCatalog.GetWorkflowVersionChangesAsync))
                return ReadChangesAsync((long)args![0]!);
            if (method.Name == nameof(IWorkflowCatalog.GetWorkflowVersionsAsync))
                throw new InvalidOperationException("Capability refresh must not enumerate the full catalog");
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private async Task<WorkflowCatalogChanges> ReadChangesAsync(long afterRevision)
        {
            Interlocked.Increment(ref VersionReads);
            VersionsEntered?.TrySetResult();
            if (PausedVersions != null) await PausedVersions.Task;
            if (FailVersions) throw new IOException("catalog unavailable");
            var changes = await Inner.GetWorkflowVersionChangesAsync(afterRevision);
            return changes with { Versions = HideVersions
                ? changes.Versions.Select(row => row with { ManifestJson = null }).ToArray()
                : VersionRows ?? changes.Versions };
        }
    }
}

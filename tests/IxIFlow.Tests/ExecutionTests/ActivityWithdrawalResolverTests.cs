using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-14")]
[Trait("Spec", "F-16")]
public sealed class ActivityWithdrawalResolverTests
{
    [Theory]
    [InlineData("CatalogOnly")]
    [InlineData("Scratch")]
    [InlineData("Persist")]
    public async Task ExactWithdrawnImportsAndSeedsRefuseBeforeReadingBytesOrFeeds(string mode)
    {
        var store = new InMemoryWorkflowStateRepository();
        var bytes = ActivityPackageFixture.Package("withdrawn.seed", "1.0.0", new());
        var candidate = await new PackageIngestion(store, store).ReadPackageAsync(new MemoryStream(bytes));
        await store.PutAsync(ArtifactKind.Package, new MemoryStream(bytes));
        var row = (await store.RegisterPackageAsync(candidate.Package, new([], PackageExtraction.Indexed, null, [], []), store)).Stored;
        await store.SetPackageWithdrawnAsync(row.Id, row.Version, true, new(row.LifecycleRevision));
        var artifacts = new ReadProbe(store);
        var resolver = new WorkflowPackageResolver(store, artifacts, new());

        var error = await Assert.ThrowsAsync<WorkflowPublishException>(() => resolver.ResolveAsync([row.Id + "@1.0.0"],
            Enum.Parse<PackageAcquisition>(mode), [], default));

        Assert.Equal("PUB025", error.Code);
        Assert.Empty(artifacts.Opened);

        // Invalid bytes and even a missing config must remain unobserved for a known tombstone.
        resolver = new(store, artifacts, new() { NuGetConfigPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing") });

        error = await Assert.ThrowsAsync<WorkflowPublishException>(() => resolver.ResolveAsync([], Enum.Parse<PackageAcquisition>(mode),
            [candidate with { Bytes = new byte[] { 255 } }], default));

        Assert.Equal("PUB025", error.Code);
        Assert.Empty(artifacts.Opened);
    }

    [Fact]
    public async Task WithdrawalAfterMetadataExpansionIsRecheckedBeforeAcquisition()
    {
        var store = new InMemoryWorkflowStateRepository();
        var bytes = ActivityPackageFixture.Package("withdrawn.race", "1.0.0", new());
        var row = (await new PackageIngestion(store, store).UploadPackageAsync(new MemoryStream(bytes))).Stored;
        var catalog = DispatchProxy.Create<IWorkflowCatalogStore, CatalogProbe>();
        var probe = (CatalogProbe)catalog;
        probe.Inner = store;
        probe.BeforeSelection = async () => await store.SetPackageWithdrawnAsync(row.Id, row.Version, true, new(row.LifecycleRevision));
        var artifacts = new ReadProbe(store);
        var resolver = new WorkflowPackageResolver(catalog, artifacts, new());

        var error = await Assert.ThrowsAsync<WorkflowPublishException>(() =>
            resolver.ResolveAsync([row.Id + "@1.0.0"], PackageAcquisition.Scratch, [], default));

        Assert.Equal("PUB025", error.Code);
        Assert.Equal(3, probe.Records);
        Assert.Empty(artifacts.Opened);
    }

    [Fact]
    public async Task AllowedRefetchRetainsTheImmutableDigestGuardAfterCollection()
    {
        var store = new InMemoryWorkflowStateRepository();
        var original = ActivityPackageFixture.Package("refetch.identity", "1.0.0", new());
        var row = (await new PackageIngestion(store, store).UploadPackageAsync(new MemoryStream(original))).Stored;
        await store.DeleteAsync(row.Digest);
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Withdrawal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var config = Path.Combine(folder, "NuGet.Config");

            await File.WriteAllTextAsync(config, "<configuration><packageSources><clear/><add key=\"fixture\" value=\"" +
                System.Security.SecurityElement.Escape(folder) + "\"/></packageSources></configuration>");

            await File.WriteAllBytesAsync(Path.Combine(folder, "refetch.identity.1.0.0.nupkg"),
                ActivityPackageFixture.Package(row.Id, row.Version, new() { ["changed.txt"] = Encoding.UTF8.GetBytes("different") }));

            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                new WorkflowPackageResolver(store, store, new() { NuGetConfigPath = config })
                    .ResolveAsync([row.Id + "@1.0.0"], PackageAcquisition.Persist, [], default));

            Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
            Assert.Contains("immutable recorded digest", error.Message);
            Assert.Equal(row.Digest, (await store.GetPackageAsync(row.Id, row.Version))!.Digest);
            Assert.False(await store.ExistsAsync(row.Digest));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    public class CatalogProbe : DispatchProxy
    {
        internal IWorkflowCatalogStore Inner = null!;
        internal Func<Task> BeforeSelection = null!;
        internal int Records;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (targetMethod.Name == nameof(IWorkflowCatalogStore.GetPackageAsync) && ++Records == 3)
                return SelectAsync((string)args![0]!, (string)args[1]!);

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException!).Throw();

                throw;
            }
        }

        private async Task<WorkflowPackage?> SelectAsync(string id, string version)
        {
            await BeforeSelection();

            return await Inner.GetPackageAsync(id, version);
        }
    }

    private sealed class ReadProbe(IArtifactStore inner) : IArtifactStore
    {
        internal List<ArtifactDigest> Opened { get; } = [];

        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default)
        {
            Opened.Add(digest);

            return inner.OpenAsync(digest, token);
        }

        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default) => inner.ExistsAsync(digest, token);

        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken token = default) =>
            inner.PutAsync(kind, content, token);

        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) => inner.DeleteAsync(digest, token);

        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime before, int limit,
            CancellationToken token = default) => inner.PurgeUnreferencedAsync(references, before, limit, token);
    }
}
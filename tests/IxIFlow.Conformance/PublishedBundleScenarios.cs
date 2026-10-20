using IxIFlow.Tests.Infrastructure;
using System.Reflection;
using Dapper;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "F-01")]
[Trait("Spec", "F-02")]
[Trait("Spec", "F-04")]
[Trait("Spec", "F-13")]
public sealed class PublishedBundleScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PublishedVersionRunsAndRecoversOnAFreshHost(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.BundleConformance", Guid.NewGuid().ToString("N"));
        await PublishedBundleScenario.CrossHostAsync(() => store.NewRepository(), root, store.ConfigureHost, store.StartHostLinksAsync);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task BundlePinsAreCheckedAtStartAndCannotChangeOnCommit(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var original = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([1]));
        var different = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([2]));
        await store.RegisterWorkflowVersionAsync(new("Pinned", 1, "store-contract", ArtifactDigest: original));
        WorkflowInstance Instance(ArtifactDigest pin) => new()
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Pinned", WorkflowVersion = 1,
            BundleDigest = pin, Status = WorkflowStatus.Running, WorkflowDataJson = "{}",
            ExecutionStateJson = StoreTestSupport.CheckpointJson
        };
        var wrong = Instance(different);
        var grant = await StoreTestSupport.LeaseAsync(store, wrong.InstanceId);
        var refusedStart = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(wrong, grant.Epoch, 0)));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, refusedStart.Kind);
        Assert.Null(await store.GetWorkflowInstanceAsync(wrong.InstanceId));
        await StoreTestSupport.ReleaseAsync(store, wrong.InstanceId);

        var instance = Instance(original);
        await store.SeedAsync(instance);
        var saved = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        grant = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
        saved.BundleDigest = different;
        var refusedChange = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(saved, grant.Epoch, saved.Revision)));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, refusedChange.Kind);
        var unchanged = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        Assert.Equal(original, unchanged.BundleDigest);
        Assert.Equal(instance.Revision, unchanged.Revision);
        await StoreTestSupport.ReleaseAsync(store, instance.InstanceId);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ArtifactPurgeRetainsAnInstancePinAfterItsCatalogRecordIsLost(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.PinRetention", Guid.NewGuid().ToString("N"));
        var files = new FileSystemArtifactStore(folder);
        var manifest = new WorkflowBundleManifest(1, "PinRetention", 1, "store-contract", "fixture-data", "fixture-entry",
            typeof(WorkflowDefinition).Assembly.GetName().Version!.ToString(), WorkflowDefinitionValidator.CheckpointContractVersion,
            "", IxIFlow.Authoring.WorkflowCompilation.Settings, new Dictionary<string, string>(), new Dictionary<string, ArtifactDigest>());
        var archive = WorkflowBundleArchive.Create(manifest, new Dictionary<string, byte[]>
        {
            [WorkflowBundleArchive.AssemblyPath] = [1], [WorkflowBundleArchive.PdbPath] = [2],
            [WorkflowBundleArchive.YamlPath] = [3], [WorkflowBundleArchive.SourcePath] = [4]
        });
        var pinned = await files.PutAsync(ArtifactKind.Bundle, new MemoryStream(archive));
        var orphan = await files.PutAsync(ArtifactKind.Bundle, new MemoryStream([2]));
        await store.RegisterWorkflowVersionAsync(new("PinRetention", 1, "store-contract", ArtifactDigest: pinned), files);
        await store.SeedAsync(new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "PinRetention", WorkflowVersion = 1,
            BundleDigest = pinned, Status = WorkflowStatus.Completed, WorkflowDataJson = "{}",
            ExecutionStateJson = StoreTestSupport.CheckpointJson
        });
        // Deliberate catalog-loss fixture: the public catalog cannot delete immutable versions.
        // The retention guard must still protect every retained instance's saved pin.
        if (store is InMemoryWorkflowStateRepository memory)
        {
            var versions = (Dictionary<(string, int), WorkflowVersion>)typeof(InMemoryWorkflowStateRepository)
                .GetField("_versions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!;
            versions.Remove(("PinRetention", 1));
        }
        else
        {
            await using var connection = database.NewConnection();
            await connection.ExecuteAsync(profile.StartsWith("distributed-sqlserver", StringComparison.Ordinal)
                ? "DELETE FROM dbo.IxIFlowWorkflowVersions WHERE Name='PinRetention' AND Version=1"
                : "DELETE FROM ixiflow_workflow_versions WHERE name='PinRetention' AND version=1");
        }
        Assert.Empty(await store.GetWorkflowVersionsAsync());
        foreach (var digest in new[] { pinned, orphan })
            File.SetLastWriteTimeUtc(Path.Combine(folder, digest.Value[7..] + ".artifact"), DateTime.UtcNow.AddDays(-100));
        Assert.Equal(1, await store.PurgeArtifactsAsync(files, TimeSpan.FromDays(90), 100));
        Assert.True(await files.ExistsAsync(pinned));
        Assert.False(await files.ExistsAsync(orphan));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AHostWithoutBundleHostingDoesNotClaimAPublishedInstance(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.BundlePlacement", Guid.NewGuid().ToString("N"));
        string id;
        await using (var bundles = PublishedBundleScenario.Host(store, folder))
        {
            id = await bundles.GetRequiredService<IWorkflowClient>().StartAsync(version.Name, new FirstData());
            await PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Suspended);
        }
        Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(
            new(id, "bundle-signal", WorkflowTypeIdentity.SavedName(typeof(Signal)), "{}"))).Status);
        var before = (await store.GetWorkflowInstanceAsync(id))!;
        var services = new ServiceCollection();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowStateRepository>(store);
        await using (var code = services.BuildServiceProvider())
        {
            Assert.Empty(await store.GetRunnableInstancesAsync(await StoreTestSupport.ClaimAsync(code)));
            Assert.Equal(WorkflowExecutionStatus.Scheduled, (await code.GetRequiredService<IWorkflowClient>().RecoverAsync(id)).Status);
        }
        var after = (await store.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.ExecutionStateJson, after.ExecutionStateJson);
        await using var capable = PublishedBundleScenario.Host(store, folder);
        Assert.Equal(WorkflowExecutionStatus.Success, (await capable.GetRequiredService<IWorkflowClient>().RecoverAsync(id)).Status);
    }
}

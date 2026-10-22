using IxIFlow.Authoring;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "F-18")]
[Trait("Feature", "authoring")]
public sealed class WorkflowContractBundleTests
{
    [Theory]
    [InlineData("format")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("noncanonical")]
    [InlineData("hash")]
    public void FormatTwoRequiresACanonicalContractAndMatchingHash(string defect)
    {
        var good = Manifest();
        WorkflowBundleArchive.CheckManifest(good);
        var bad = defect switch
        {
            "format" => good with { FormatVersion = 1 },
            "missing" => good with { Contract = null! },
            "malformed" => good with { Contract = "{}" },
            "noncanonical" => good with { Contract = " " + good.Contract },
            _ => good with { ContractHash = new string('0', 64) }
        };
        var error = Assert.Throws<WorkflowEngineException>(() => WorkflowBundleArchive.CheckManifest(bad));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
    }

    [Fact]
    public async Task PublisherAndLoaderPropagateAndVerifyTheContract()
    {
        using var culture = new UICultureScope("de-DE");
        var store = new InMemoryWorkflowStateRepository();
        const string yaml = """
            workflow:
              name: ContractBundle
              version: 1
              dataType: Data
            definitions:
              - kind: activity
                activity: Echo
            """;
        var options = new WorkflowPublisherOptions();
        options.SourceOptions.Types["Data"] = "global::" + typeof(WorkflowContractData).FullName;
        options.SourceOptions.Types["Echo"] = "global::" + typeof(Echo).FullName;
        var draft = await store.SaveDefinitionAsync(new("ContractBundle", 1, "workflow", yaml), 0);
        var result = await new WorkflowPublisher(store, store, options).PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var row = result.Version!;
        var expected = WorkflowContract.OfType(typeof(WorkflowContractData));
        Assert.Equal(expected.ToJson(), row.ContractJson);
        Assert.Equal(expected.Hash, row.ContractHash);
        await using var archive = (await store.OpenAsync(row.ArtifactDigest!.Value))!;
        var bundle = await WorkflowBundleArchive.ReadAsync(archive, row.ArtifactDigest.Value);
        Assert.Equal(2, bundle.Manifest.FormatVersion);
        Assert.Equal(expected.ToJson(), bundle.Manifest.Contract);
        Assert.Equal(expected.Hash, bundle.Manifest.ContractHash);
        var cache = Path.Combine(Path.GetTempPath(), "workflow-contract-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(cache));
            using (var lease = await catalog.AcquireWorkflowDefinitionAsync(row.Name, 1, row.ArtifactDigest))
                Assert.Equal(expected.ToJson(), WorkflowDefinitionValidator.GetContract(lease.Definition).ToJson());
            var changed = new WorkflowContract([], [], true);
            var manifest = bundle.Manifest with { Contract = changed.ToJson(), ContractHash = changed.Hash };
            var bytes = WorkflowBundleArchive.Create(manifest, bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
            var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.AcquireWorkflowDefinitionAsync(row.Name, 1, digest));
            Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
            Assert.Contains("contract", error.Message);
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    [Theory]
    [InlineData("run")]
    [InlineData("start")]
    [InlineData("host")]
    public async Task DefinitionRootStartsValidateHostContextRequiredInputsForABundleDefinition(string path)
    {
        var store = new InMemoryWorkflowStateRepository();
        const string yaml = """
            workflow:
              name: ContractBundleRoot
              version: 1
              dataType: Data
            definitions:
              - kind: activity
                activity: Echo
            """;
        var publisherOptions = new WorkflowPublisherOptions();
        publisherOptions.SourceOptions.Types["Data"] = "global::" + typeof(WorkflowContractData).FullName;
        publisherOptions.SourceOptions.Types["Echo"] = "global::" + typeof(Echo).FullName;
        var draft = await store.SaveDefinitionAsync(new("ContractBundleRoot", 1, "workflow", yaml), 0);
        var published = await new WorkflowPublisher(store, store, publisherOptions).PublishAsync(draft.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
        var cache = Path.Combine(Path.GetTempPath(), "workflow-contract-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var provider = PublishedBundleScenario.Host(store, cache);
            var catalog = provider.GetRequiredService<IWorkflowCatalog>();
            using var lease = await catalog.AcquireWorkflowDefinitionAsync(published.Version!.Name, 1, published.Version.ArtifactDigest);
            var definition = lease.Definition;
            Assert.NotSame(typeof(WorkflowContractData).Assembly, definition.WorkflowDataType!.Assembly);
            Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(WorkflowContractData)), WorkflowTypeIdentity.SavedName(definition.WorkflowDataType));
            var client = provider.GetRequiredService<IWorkflowClient>();
            await using var renewer = new ExecutionLeaseRenewer(store);
            var operations = new WorkflowHost.InstanceOperations(provider, provider.GetRequiredService<IActivityExecutor>(), store,
                catalog, new InProcessInstanceGate(), renewer);
            var rejectedId = Guid.NewGuid().ToString("N");
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => Start(new WorkflowContractData(), rejectedId));
            Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
            Assert.Contains("WorkflowContractData.Value", error.Message);
            Assert.Null(await store.GetWorkflowInstanceAsync(rejectedId));
            var acceptedId = Guid.NewGuid().ToString("N");
            var result = await Start(new WorkflowContractData { Value = "host input" }, acceptedId);
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal("host input", result.WorkflowData!.GetType().GetProperty("Value")!.GetValue(result.WorkflowData));
            Assert.Equal(WorkflowStatus.Completed, (await store.GetWorkflowInstanceAsync(acceptedId))!.Status);

            async Task<WorkflowExecutionResult> Start(WorkflowContractData data, string id)
            {
                var options = new WorkflowOptions { InstanceId = id };
                if (path == "run") return await client.RunAsync(definition, data, options);
                if (path == "start") return await client.AwaitAsync(await client.StartAsync(definition, data, options));
                return await new WorkflowHost.SlotClient(operations).RunAsync(definition, data, options);
            }
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    private sealed class UICultureScope(string name) : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = Set(name);
        private static System.Globalization.CultureInfo Set(string name)
        {
            var previous = System.Globalization.CultureInfo.CurrentUICulture;
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(name);
            return previous;
        }
        public void Dispose() => System.Globalization.CultureInfo.CurrentUICulture = _previous;
    }

    private static WorkflowBundleManifest Manifest()
    {
        var contract = WorkflowContract.OfType(typeof(WorkflowContractData));
        return new(2, "Contract", 1, "hash", WorkflowTypeIdentity.SavedName(typeof(WorkflowContractData)), "Entry", "1.0", 4, "",
            contract.ToJson(), contract.Hash,
            new("5", "latest", "Release", true, "portable"), new Dictionary<string, string>(),
            new Dictionary<string, ArtifactDigest>
            {
                [WorkflowBundleArchive.AssemblyPath] = ArtifactDigest.Of([1]),
                [WorkflowBundleArchive.PdbPath] = ArtifactDigest.Of([2]),
                [WorkflowBundleArchive.YamlPath] = ArtifactDigest.Of([3]),
                [WorkflowBundleArchive.SourcePath] = ArtifactDigest.Of([4])
            });
    }
}

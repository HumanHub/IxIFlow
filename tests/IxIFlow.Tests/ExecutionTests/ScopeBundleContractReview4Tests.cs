using System.Reflection;
using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeBundleContractReview4Tests
{
    private static readonly Lazy<WorkflowBundle> ChildBundle = new(CreateChildBundle);

    [Theory]
    [InlineData(false, "row"), InlineData(true, "row")]
    [InlineData(false, "manifest"), InlineData(true, "manifest")]
    [InlineData(false, "manifest-and-row"), InlineData(true, "manifest-and-row")]
    public Task MisstatedRootRequirementsAreRefusedOnChildLoad(bool sqlite, string target) => WithStore(sqlite, async (store, root) =>
    {
        var artifacts = new InMemoryWorkflowStateRepository();
        var row = await StoreChild(artifacts, target);
        using var catalog = new WorkflowCatalog(store, artifacts: artifacts, bundles: new(Path.Combine(root, "cache")));
        await catalog.RegisterWorkflowVersionAsync(row);
        // The row and manifest agree in the adversarial case, including their hash.
        // Loading must compare the executable contract, not just these two descriptors.
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.ResolveWorkflowDefinitionAsync(row.Name, row.Version));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
        Assert.Contains("contract", error.Message);
        Assert.Empty((await store.QueryInstancesAsync(new())).Items);
    });

    [Theory]
    [InlineData(false, false), InlineData(false, true)]
    [InlineData(true, false), InlineData(true, true)]
    public Task HonestRequiringChildBundleIsRefusedAtRegistrationInBothOrders(bool sqlite, bool callerFirst) => WithStore(sqlite, async (store, root) =>
    {
        var artifacts = new InMemoryWorkflowStateRepository();
        var row = await StoreChild(artifacts, "honest");
        using var catalog = new WorkflowCatalog(store, artifacts: artifacts, bundles: new(Path.Combine(root, "cache")));
        var caller = new WorkflowBuilder<object, object>([], "ScopeContractCaller")
            .Invoke<object>(row.Name, row.Version, sourcePath: "caller.cs", sourceLine: 17).Named("invoke-child").Build();
        if (callerFirst) await catalog.RegisterWorkflowAsync(caller);
        else
        {
            await catalog.RegisterWorkflowVersionAsync(row);
            using var loaded = await catalog.ResolveWorkflowDefinitionAsync(row.Name, row.Version);
            Assert.Single(WorkflowDefinitionValidator.GetContract(loaded.Definition).RequiredScopes);
        }
        var revision = await store.GetCatalogRevisionAsync();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => callerFirst
            ? catalog.RegisterWorkflowVersionAsync(row) : catalog.RegisterWorkflowAsync(caller));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("caller.cs:17", error.Message);
        Assert.Contains("invoke-child", error.Message);
        Assert.Contains(row.Name, error.Message);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Null(await store.GetWorkflowVersionAsync(callerFirst ? row.Name : caller.Name, 1));
        Assert.Empty((await store.QueryInstancesAsync(new())).Items);
    });

    private static async Task<WorkflowVersion> StoreChild(IArtifactStore artifacts, string target)
    {
        var bundle = ChildBundle.Value;
        var manifest = bundle.Manifest;
        var honest = WorkflowContract.Parse(manifest.Contract);
        var scopeFree = new WorkflowContract(honest.Inputs, honest.Outputs, honest.Declared);
        if (target is "manifest" or "manifest-and-row")
            manifest = manifest with { Contract = scopeFree.ToJson(), ContractHash = scopeFree.Hash };
        var bytes = WorkflowBundleArchive.Create(manifest, bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
        var digest = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
        var verified = await WorkflowBundleArchive.ReadAsync(new MemoryStream(bytes), digest);
        var rowContract = target is "row" or "manifest-and-row" ? scopeFree : honest;
        return new(manifest.Name, manifest.Version, manifest.StructureHash, rowContract.ToJson(), rowContract.Hash,
            Descriptor: new WorkflowDescriptor(manifest.Name, "", [], null, null, IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(object)), IxIFlow.Core.Runtime.WorkflowTypeIdentity.CSharpName(typeof(object))), Invocations: [], ArtifactDigest: digest, Dependencies: [], ManifestJson: JsonSerializer.Serialize(verified.Manifest));
    }

    private static WorkflowBundle CreateChildBundle()
    {
        const string source = """
            using IxIFlow.Builders;
            using IxIFlow.Core;
            public static class Entry
            {
                public static WorkflowDefinition Build() => Workflow.Create<object>("ScopeContractChild")
                    .RequiresScope<object>().Build();
            }
            """;
        var compiled = WorkflowCompilation.Compile(source);
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
        var manifest = WorkflowCompilation.WithDefinition(compiled.Assembly, compiled.Pdb, definition =>
        {
            var contract = WorkflowDefinitionValidator.GetContract(definition);
            return new WorkflowBundleManifest(6, definition.Name, definition.Version, WorkflowDefinitionValidator.Inspect(definition),
                WorkflowTypeIdentity.SavedName(definition.WorkflowDataType!), "Entry", typeof(WorkflowDefinition).Assembly.GetName().Version!.ToString(),
                WorkflowDefinitionValidator.CheckpointContractVersion, WorkflowDefinitionValidator.GetCombinerSignature(definition),
                contract.ToJson(), contract.Hash, WorkflowCompilation.Settings,
                WorkflowCompilation.ReferencePaths().Select(AssemblyName.GetAssemblyName).Where(WorkflowSharedAssemblies.IsShared)
                    .DistinctBy(identity => identity.FullName).ToDictionary(identity => identity.FullName, identity => identity.Version!.ToString()),
                new Dictionary<string, ArtifactDigest>());
        });
        return new(manifest, new Dictionary<string, ReadOnlyMemory<byte>>
        {
            [WorkflowBundleArchive.AssemblyPath] = compiled.Assembly,
            [WorkflowBundleArchive.PdbPath] = compiled.Pdb,
            [WorkflowBundleArchive.SourcePath] = Encoding.UTF8.GetBytes(source),
            [WorkflowBundleArchive.YamlPath] = Encoding.UTF8.GetBytes("workflow: {name: ScopeContractChild}")
        });
    }

    private static async Task WithStore(bool sqlite, Func<IWorkflowStateRepository, string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.ScopeContractReview4", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (sqlite)
            {
                await using var store = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(root, "catalog.db"));
                await test(store, root);
            }
            else await test(new InMemoryWorkflowStateRepository(), root);
        }
        finally { await SqliteTestFiles.DeleteAsync(root); }
    }
}

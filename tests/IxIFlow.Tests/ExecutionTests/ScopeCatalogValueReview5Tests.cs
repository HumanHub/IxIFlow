using System.Text.Json;
using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeCatalogValueReview5Tests
{
    [Theory, InlineData(false), InlineData(true)]
    public void VersionEqualityAndHashingSurviveCollectionDeserialization(bool populated)
    {
        var contract = WorkflowContract.OfType(typeof(object));
        var row = new WorkflowVersion("CatalogValue", 1, "structure", contract.ToJson(), contract.Hash,
            Descriptor: new WorkflowDescriptor("CatalogValue", "", [], null, null, IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(object)), IxIFlow.Core.Runtime.WorkflowTypeIdentity.CSharpName(typeof(object))), Invocations: populated ? [new("Child", 1, "invoke", "caller.cs:1")] : [],
            Dependencies: populated ? [new("Child", 1)] : [],
            ArtifactReferences: populated ? [ArtifactDigest.Of([1, 2, 3])] : []);
        var restored = JsonSerializer.Deserialize<WorkflowVersion>(JsonSerializer.Serialize(row))!;
        Assert.NotSame(row.Invocations, restored.Invocations);
        Assert.Equal(row, restored);
        Assert.Equal(row.GetHashCode(), restored.GetHashCode());
        Assert.Contains(restored, new HashSet<WorkflowVersion> { row });
        Assert.NotEqual(row, restored with { State = WorkflowVersionState.Retired });
        if (populated)
        {
            Assert.NotEqual(row, restored with { Invocations = [restored.Invocations[0] with { Step = "different" }] });
            Assert.NotEqual(row, restored with { Dependencies = [new("Child", 2)] });
            Assert.NotEqual(row, restored with { ArtifactReferences = [ArtifactDigest.Of([4, 5, 6])] });
        }
    }

    [Fact]
    public async Task InventoryValidationRefusesNewIncompleteEdgesAndPreservesExistingMetadata()
    {
        var store = new InMemoryWorkflowStateRepository();
        var contract = WorkflowContract.OfType(typeof(object));
        var row = new WorkflowVersion("CatalogInventory", 1, "structure", contract.ToJson(), contract.Hash,
            Descriptor: new WorkflowDescriptor("CatalogInventory", "", [], null, null, IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(object)), IxIFlow.Core.Runtime.WorkflowTypeIdentity.CSharpName(typeof(object))), Invocations: [new("Child", 1, "invoke", "caller.cs:1")], Dependencies: [new("Child", 1)]);
        var incomplete = row with { Dependencies = [] };
        var invalid = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(incomplete));
        Assert.Contains("must refer to declared dependencies", invalid.Message);
        Assert.Equal(0, await store.GetCatalogRevisionAsync());
        await store.RegisterWorkflowVersionAsync(row);
        var revision = await store.GetCatalogRevisionAsync();
        var mutation = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(incomplete));
        Assert.Contains("immutable", mutation.Message);
        Assert.Equal(row, await store.GetWorkflowVersionAsync(row.Name, row.Version));
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
    }
}

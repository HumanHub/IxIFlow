using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring")]
[Trait("Spec", "F-09")]
[Trait("Spec", "F-14")]
public sealed class WorkflowReceiptConversionTests
{
    [Fact]
    public async Task SuccessfulWorkflowResultEncodesTheAuditedFullGenerationReceipt()
    {
        var store = new InMemoryWorkflowStateRepository();
        var draft = await store.SaveDefinitionAsync(new("Receipt", 1, "workflow", "source"), null, "author");
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([1]));
        var contract = WorkflowContract.OfType(typeof(object));
        var row = await store.PublishDefinitionAsync(draft.Id, new(draft.Name, draft.Version, "hash", contract.ToJson(), contract.Hash,
            Descriptor: new WorkflowDescriptor(draft.Name, "", [], null, null, IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(object)), IxIFlow.Core.Runtime.WorkflowTypeIdentity.CSharpName(typeof(object))), [], ArtifactDigest: digest, PublishedDraft: draft.Id, ManifestJson: "{}"), store, "publisher");
        var result = new WorkflowPublishResult(row, []);
        Assert.True(result.Success);
        Assert.Equal(draft.Id, result.Receipt!.Draft);
        Assert.Equal(JsonSerializer.Serialize(row), result.Receipt.ReceiptJson);
        var decoded = JsonSerializer.Deserialize<WorkflowVersion>(result.Receipt.ReceiptJson)!;
        Assert.Equal(draft.Generation, decoded.PublishedDraft!.Generation);
        Assert.Equal("publisher", decoded.PublishedBy);
        Assert.Equal(row.LifecycleRevision, decoded.LifecycleRevision);
        Assert.Null(new WorkflowPublishResult(null, []).Receipt);
    }
}

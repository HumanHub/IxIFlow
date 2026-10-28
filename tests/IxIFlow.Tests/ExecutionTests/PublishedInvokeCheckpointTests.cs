using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-17")]
[Trait("Spec", "F-18")]
public sealed class PublishedInvokeCheckpointTests
{
    [Theory]
    [InlineData(13, false)]
    [InlineData(1, false)]
    [InlineData(13, true)]
    [InlineData(1, true)]
    public async Task PublishRefusesCalleeCheckpointMismatchBeforeChangingDraftOrCatalog(int versionOffset, bool imported)
    {
        var options = new WorkflowPublisherOptions
        {
            SourceOptions = new() { Types = { ["Data"] = "global::" + typeof(FirstData).FullName } }
        };
        var source = new InMemoryWorkflowStateRepository();
        var calleeDraft = await source.SaveDefinitionAsync(new("Callee", 1, "workflow",
            "workflow: {name: Callee, version: 1, dataType: Data}\ndefinitions: []"), null);
        var published = await new WorkflowPublisher(source, source, options).PublishAsync(calleeDraft.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
        var original = published.Workflow!;
        await using var stream = (await source.OpenAsync(original.ArtifactDigest!.Value))!;
        var bundle = await WorkflowBundleArchive.ReadAsync(stream, original.ArtifactDigest.Value);
        var manifest = bundle.Manifest with
        {
            CheckpointContractVersion = WorkflowDefinitionValidator.CheckpointContractVersion + versionOffset
        };
        var bytes = WorkflowBundleArchive.Create(manifest,
            bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
        var store = new InMemoryWorkflowStateRepository();
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
        var verified = await WorkflowBundleArchive.ReadAsync(new MemoryStream(bytes), digest);
        var callee = original with { ArtifactDigest = digest, ManifestJson = JsonSerializer.Serialize(verified.Manifest) };
        await store.RegisterWorkflowVersionAsync(callee);
        callee = (await store.GetWorkflowVersionAsync(callee.Name, callee.Version))!;
        var yaml = imported
            ? "workflow: {name: Caller, version: 1, dataType: Data}\nimports:\n  workflows: [Callee@1]\ndefinitions: []"
            : "workflow: {name: Caller, version: 1, dataType: Data}\ndefinitions:\n  - kind: invokeWorkflow\n    workflow: {name: Callee, version: 1}";
        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", yaml), null);

        var result = await new WorkflowPublisher(store, store, options).PublishAsync(draft.Id);

        Assert.False(result.Success);
        Assert.Null(result.Workflow);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("PUB009", error.Code);
        Assert.Equal(WorkflowPublishStage.Validate, error.Stage);
        Assert.Equal(WorkflowDiagnosticSeverity.Error, error.Severity);
        Assert.Equal("workflow.yaml", error.Path);
        Assert.Equal(imported ? 3 : 4, error.Line);
        Assert.Equal(imported ? 3 : 15, error.Column);
        Assert.Contains("Callee@1", error.Message);
        Assert.Contains("republish the callee", error.Message);
        Assert.Equal(draft, await store.GetDefinitionAsync("workflow", "Caller", 1));
        Assert.Null(await store.GetWorkflowVersionAsync("Caller", 1));
        Assert.Equal(JsonSerializer.Serialize(callee), JsonSerializer.Serialize(Assert.Single(await store.GetWorkflowVersionsAsync())));
    }
}

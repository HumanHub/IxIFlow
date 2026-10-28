using System.Text.Json;
using System.Text.Json.Nodes;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "F-27")]
public sealed class ReleaseFormatTests
{
    public static TheoryData<int> PreReleaseVersions => new(Enumerable.Range(1, 14));

    // Generated from the final pre-release writer's manifest for Waiting(), changing only Format.
    // Reusing that complete shape with format 1 exercises the reset's possible namespace collision.
    public static TheoryData<int, string> PreReleaseHashes => new()
    {
        { 1, "13C4669096993870DCD77520C5593F03BB4F16B061952118212B5669AFF87D14" },
        { 2, "A73401022EAD5ECF3F32F479A8DED4D4857EC33348C1703436D38526B7C9089E" },
        { 3, "E729D9DA35470A0615976B8116A15FB1A7FA96923149DFF88252A94BF6D625C8" },
        { 4, "9A6A40BA312E8B7ECD91A2489F1A95C6D0D3AF26AC8AE2CFF7FB35BC89083A66" },
        { 5, "97E559A9A09D16A7520505556C95093EDCCFD637C81D68B3BC84BC88114ED57C" },
        { 6, "70CBE0DB8316B284C17F2A7D6264A267E30876D8C07030C3CE99EFA273D78CD4" },
        { 7, "C0EC63E04B2614DA2C692C8E05347765F68A3370A6C890A247DEF64EF6522650" },
        { 8, "9DBD95E5C5028617B2AF63F5551E471DC5A9B11CD0D491CD60043DFD36B62694" },
        { 9, "5A0D18A7978F0BDE06AC04A56C57169D4DA4BF12F50E417871C3E68D88CC2B9D" }
    };

    [Theory]
    [MemberData(nameof(PreReleaseVersions))]
    public void PreReleaseEnvelopesAreRefusedBeforeInterpretingState(int version)
    {
        var json = $$"""{"Runtime":"structured-v2","SchemaVersion":{{version}},"Continuations":"old shape"}""";
        Assert.False(ExecutionCheckpoint.IsStructured(json));
        foreach (var read in new Func<string, ExecutionCheckpoint>[] { ExecutionCheckpoint.Read, ExecutionCheckpoint.ReadTrunk })
        {
            var error = Assert.Throws<WorkflowEngineException>(() => read(json));
            Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
            Assert.Contains($"Unsupported execution checkpoint version '{version}'", error.Message);
            Assert.Contains("expected 1.", error.Message);
            Assert.Contains("no migration", error.Message);
        }
        var envelopeError = Assert.Throws<WorkflowEngineException>(() =>
            JsonSerializer.Deserialize<WorkflowInstance>("{\"ExecutionStateJson\":" + json + "}"));
        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, envelopeError.Kind);
        Assert.Contains("Unsupported execution checkpoint version", envelopeError.Message);
    }

    [Theory]
    [MemberData(nameof(PreReleaseHashes))]
    public async Task PreReleaseHashesAreRefusedWhenLoadingCatalogVersions(int format, string oldHash)
    {
        var definition = Waiting();
        var seed = new InMemoryWorkflowStateRepository();
        await new WorkflowCatalog(seed).RegisterWorkflowAsync(definition);
        var version = (await seed.GetWorkflowVersionAsync(definition.Name, definition.Version))!;
        Assert.True(version.StructureHash != oldHash, $"Pre-release format {format} collides with 1.0.0");
        var store = new InMemoryWorkflowStateRepository();
        await store.RegisterWorkflowVersionAsync(version with { StructureHash = oldHash });

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            new WorkflowCatalog(store).RegisterWorkflowAsync(definition));

        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("structure hash", error.Message);
        Assert.Contains(oldHash, error.Message);
        Assert.Contains(version.StructureHash, error.Message);
        Assert.Equal(oldHash, (await store.GetWorkflowVersionAsync(definition.Name, definition.Version))!.StructureHash);
    }

    [Theory]
    [MemberData(nameof(PreReleaseHashes))]
    public async Task PreReleaseHashesAreRefusedWhenLoadingInstances(int format, string oldHash)
    {
        var definition = Waiting();
        var seed = new InMemoryWorkflowStateRepository();
        using var source = Services(seed);
        var suspended = await source.GetRequiredService<IWorkflowClient>().RunAsync(definition, new object());
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        var instance = (await seed.GetWorkflowInstanceAsync(suspended.InstanceId))!;
        var checkpoint = JsonNode.Parse(instance.ExecutionStateJson)!;
        var currentHash = checkpoint["StructureHash"]!.GetValue<string>();
        Assert.True(currentHash != oldHash, $"Pre-release format {format} collides with 1.0.0");
        checkpoint["StructureHash"] = oldHash;
        instance.ExecutionStateJson = checkpoint.ToJsonString();
        instance.Revision = 0;
        var store = new InMemoryWorkflowStateRepository();
        await store.RegisterWorkflowVersionAsync((await seed.GetWorkflowVersionAsync(definition.Name, definition.Version))!);
        var owner = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
        var waits = await seed.GetOpenWaitsAsync(new WaitQuery(InstanceId: instance.InstanceId));
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, owner.Epoch, 0, openedWaits: waits.Select(wait =>
                new OpenedWait(wait.WaitId, wait.Kind, wait.Key, wait.EventType, null, wait.DefaultPayloadJson)).ToArray()))).Status);
        await StoreTestSupport.ReleaseAsync(store, instance.InstanceId);
        using var target = Services(store);
        await target.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            target.GetRequiredService<IWorkflowClient>().RunAsync(definition, new object(),
                new WorkflowOptions { InstanceId = instance.InstanceId }));

        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
        Assert.Contains("saved structure hash", error.Message);
        Assert.Contains(oldHash, error.Message);
        Assert.Contains(currentHash, error.Message);
        Assert.Equal(instance.ExecutionStateJson, (await store.GetWorkflowInstanceAsync(instance.InstanceId))!.ExecutionStateJson);
    }

    [Fact]
    public async Task ReleaseCheckpointAndStructureHashRoundTripAcrossProviders()
    {
        var definition = Waiting();
        var hash = WorkflowDefinitionValidator.Inspect(definition);
        var store = new InMemoryWorkflowStateRepository();
        string id;
        using (var first = Services(store))
        {
            var suspended = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new object());
            Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
            id = suspended.InstanceId;
        }
        var instance = (await store.GetWorkflowInstanceAsync(id))!;
        Assert.True(ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson));
        foreach (var read in new Func<string, ExecutionCheckpoint>[] { ExecutionCheckpoint.Read, ExecutionCheckpoint.ReadTrunk })
        {
            var checkpoint = read(instance.ExecutionStateJson);
            Assert.Equal("ixiflow-1", checkpoint.Runtime);
            Assert.Equal(1, checkpoint.SchemaVersion);
            Assert.Equal(hash, checkpoint.StructureHash);
        }
        Assert.Equal(hash, (await store.GetWorkflowVersionAsync(definition.Name, definition.Version))!.StructureHash);
        using var second = Services(store);
        await second.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(Waiting());
        var completed = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "release", "ready");
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(hash, ExecutionCheckpoint.Read((await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson).StructureHash);
    }

    private static WorkflowDefinition Waiting() =>
        new WorkflowBuilder<object, object>([], "Release formats").WaitFor<string>("release").Build();

    private static ServiceProvider Services(IWorkflowStateRepository store) =>
        new ServiceCollection().AddLogging().AddSingleton(store).AddIxIFlow().Services.BuildServiceProvider();
}

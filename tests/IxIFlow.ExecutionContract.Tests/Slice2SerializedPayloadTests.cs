using System.Text;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.ContractTests;

[Trait("Feature", "durability")]
public sealed class Slice2SerializedPayloadTests
{
    [Fact]
    public async Task HydrationStopTransportFailureStillReturnsReady()
    {
        using var harness = new MessageHarness();
        var definition = Workflow.Create<MessageHarness.Data>("slice2-hydration-stop-fault").Step<MessageHarness.Noop>().Build();
        var assignment = await harness.AssignAsync(definition);

        var reference = await harness.Host.Store.PayloadStore.PutAsync(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new MessageHarness.Data { Value = new string('x', 4096) })));

        assignment.Instance.WorkflowDataJson = StoredJson.FromReference(reference);
        var parts = assignment.Parts!.ToDictionary(pair => pair.Key, pair => pair.Value);
        parts[CheckpointParts.DataKey] = assignment.Instance.WorkflowDataJson;
        var limited = assignment with { Parts = parts, Limits = assignment.Limits with { MaxHydratedPayloadBytes = 1024 } };
        harness.Host.Assignment = limited;
        harness.ChangeCheckpoint = _ => throw new IOException("Stop transport failed");
        var ready = await harness.ExecuteAsync(limited);
        Assert.Equal(AssignmentOutcomeKind.Interrupted, ready.Outcome!.Kind);
        Assert.DoesNotContain(typeof(ReadPayload), harness.Crossed);
    }

    [Fact]
    public async Task HydrationStopAcknowledgesAMetadataOnlyCommitWithoutReadingTheBody()
    {
        using var harness = new MessageHarness();
        var definition = Workflow.Create<MessageHarness.Data>("slice2-hydration-stop").Step<MessageHarness.Noop>().Build();
        var assignment = await harness.AssignAsync(definition);

        var reference = await harness.Host.Store.PayloadStore.PutAsync(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new MessageHarness.Data { Value = new string('x', 4096) })));

        assignment.Instance.WorkflowDataJson = StoredJson.FromReference(reference);
        var parts = assignment.Parts!.ToDictionary(pair => pair.Key, pair => pair.Value);
        parts[CheckpointParts.DataKey] = assignment.Instance.WorkflowDataJson;
        var limited = assignment with { Parts = parts, Limits = assignment.Limits with { MaxHydratedPayloadBytes = 1024 } };
        harness.Host.Assignment = limited;
        var ready = await harness.ExecuteAsync(limited);

        Assert.True(ready.Outcome?.Kind == AssignmentOutcomeKind.Idle,
            $"{ready.Outcome?.Kind}: {ready.Outcome?.ErrorMessage}; {ready.Outcome?.ErrorType}; commits={harness.Host.Requests.Count}; {ready.Outcome?.ErrorStackTrace}");

        Assert.True(Assert.Single(harness.Host.Requests).Commit.PreservesPayloadPins);
        Assert.DoesNotContain(typeof(ReadPayload), harness.Crossed);
    }
}
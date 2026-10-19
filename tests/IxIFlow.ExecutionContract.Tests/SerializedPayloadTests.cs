using System.Text;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.ContractTests;

[Trait("Spec", "H-06")]
public sealed class SerializedPayloadTests
{
    [Fact]
    public async Task LargeCheckpointBytesCrossInBoundedChunksAndLostAcknowledgementReplaysSafely()
    {
        using var harness = new MessageHarness();
        var definition = Workflow.Create<MessageHarness.Data>("chunked-checkpoint")
            .Step<Grow>(step => step.Output(a => a.Value).To(c => c.WorkflowData.Value)).Step<MessageHarness.Noop>().Build();
        await harness.AssignAsync(definition);
        // The first checkpoint after the activity's start is the one that carries the large data.
        var lost = false;
        harness.Host.LoseReply = request => request.Commit.PayloadReferences.Count > 0 && !lost && (lost = true);
        var ready = await harness.ExecuteAsync();
        Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome!.Kind);
        var chunks = harness.Host.Requests.Where(r => r.UploadOnly).ToArray();
        Assert.True(chunks.Length > 4);
        Assert.All(chunks, request =>
        {
            Assert.InRange(request.Payload!.Body.Length, 1, PayloadChunk.MaxChunkBytes);
            var frame = JsonSerializer.SerializeToUtf8Bytes(request, ExecutionMessageContext.Default.Checkpoint);
            Assert.True(frame.Length < 110_000, $"Frame was {frame.Length} bytes");
        });
        var saved = (await harness.Host.Store.GetWorkflowInstanceAsync(harness.Assignment.Instance.InstanceId))!;
        Assert.NotNull(saved.WorkflowDataJson.Reference);
        Assert.Equal(JsonSerializer.Serialize(new MessageHarness.Data { Value = Grow.Text }),
            Encoding.UTF8.GetString((await harness.Host.Store.PayloadStore.GetAsync(saved.WorkflowDataJson.Reference))!.Value.Span));
        Assert.Null(await harness.Host.Store.PayloadStore.TryCollectAsync(saved.WorkflowDataJson.Reference, TimeSpan.Zero));
        Assert.Contains(WorkflowCommitStatus.AlreadyApplied, harness.Host.Receipts);
    }

    [Fact]
    public async Task AggregateHydrationLimitStopsThroughTheSerializedHostWithoutReadingTheBody()
    {
        using var harness = new MessageHarness();
        var definition = Workflow.Create<MessageHarness.Data>("limited-hydration").Step<MessageHarness.Noop>().Build();
        var assignment = await harness.AssignAsync(definition);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new MessageHarness.Data { Value = Grow.Text }));
        var reference = await harness.Host.Store.PayloadStore.PutAsync(body);
        var instance = assignment.Instance;
        instance.WorkflowDataJson = StoredJson.FromReference(reference);
        var parts = assignment.Parts!.ToDictionary(p => p.Key, p => p.Value);
        parts[CheckpointParts.DataKey] = instance.WorkflowDataJson;
        var limited = assignment with { Parts = parts, Limits = assignment.Limits with { MaxHydratedPayloadBytes = 1024 } };
        harness.Host.Assignment = limited;
        var ready = await harness.ExecuteAsync(limited);
        Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome!.Kind);
        Assert.Contains("hydrated and in-flight", ready.Outcome.ErrorMessage);
        var stop = Assert.Single(harness.Host.Requests);
        Assert.True(stop.Commit.PreservesPayloadPins);
        Assert.Empty(stop.Commit.Parts);
        Assert.DoesNotContain(typeof(ReadPayload), harness.Crossed);
    }

    [Fact]
    public async Task ReferencedRootHydratesThroughBoundedReadRequests()
    {
        using var harness = new MessageHarness();
        var definition = Workflow.Create<MessageHarness.Data>("read-payload").Step<MessageHarness.Noop>().Build();
        var assignment = await harness.AssignAsync(definition);
        var reference = await harness.Host.Store.PayloadStore.PutAsync(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new MessageHarness.Data { Value = Grow.Text })));
        assignment.Instance.WorkflowDataJson = StoredJson.FromReference(reference);
        var parts = assignment.Parts!.ToDictionary(p => p.Key, p => p.Value);
        parts[CheckpointParts.DataKey] = assignment.Instance.WorkflowDataJson;
        var ready = await harness.ExecuteAsync(assignment with { Parts = parts });
        Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome!.Kind);
        Assert.Contains(typeof(ReadPayload), harness.Crossed);
        Assert.Contains(typeof(ReadPayloadReply), harness.Crossed);
    }

    [Fact]
    public async Task SavedInputCopiesHydrateOnRecovery()
    {
        using var harness = new MessageHarness();
        var definition = Workflow.Create<MessageHarness.Data>("restore-input")
            .Step<Grow>(s => s.Output(a => a.Value).To(c => c.WorkflowData.Value))
            .Step<RecoverInput>(s => s.Input(a => a.Value).From(c => c.WorkflowData.Value)).Build();
        var assignment = await harness.AssignAsync(definition);
        var interrupted = false;
        harness.ChangeCheckpoint = request =>
        {
            if (!request.UploadOnly && !interrupted && request.Commit.Payload.Contains("\"StepId\":\"root/1\"", StringComparison.Ordinal))
            { interrupted = true; harness.Deliver(new Cancel(assignment.AssignmentId, CancelKind.Ownership)); }
            return request;
        };
        var first = await harness.ExecuteAsync();
        Assert.Equal(AssignmentOutcomeKind.OwnershipLost, first.Outcome!.Kind);
        var saved = (await harness.Host.Store.GetWorkflowInstanceAsync(assignment.Instance.InstanceId))!;
        using var state = JsonDocument.Parse(saved.ExecutionStateJson);
        var input = state.RootElement.GetProperty("Continuations")[0].GetProperty("PendingActivity")
            .GetProperty("Inputs").GetProperty("Properties").GetProperty("Value").GetProperty("Value").GetProperty("Json");
        Assert.True(input.TryGetProperty("reference", out _));
        var parts = assignment.Parts!;
        foreach (var request in harness.Host.Requests.Where(r => !r.UploadOnly)) parts = request.Commit.ApplyParts(parts);
        var restored = assignment with { AssignmentId = Guid.NewGuid(), Instance = saved, Parts = parts };
        harness.ChangeCheckpoint = null;
        harness.Host.Assignment = restored;
        harness.Host.Active = true;
        var recovered = await harness.ExecuteAsync(restored);
        Assert.Equal(AssignmentOutcomeKind.Idle, recovered.Outcome!.Kind);
        Assert.Equal(WorkflowStatus.Completed, harness.Host.Saved!.Status);
        Assert.Contains(typeof(ReadPayload), harness.Crossed);
    }

    public sealed class Grow : IAsyncActivity
    {
        public static string Text { get; } = new('é', 500_000);
        public string Value { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Value = Text; return Task.CompletedTask; }
    }
    public sealed class RecoverInput : IRecoverableActivity<bool>
    {
        public string Value { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Assert.Equal(Grow.Text, Value); return Task.CompletedTask; }
        public bool CaptureRecoveryState(IActivityContext context) => true;
        public Task<ActivityRecoveryResult<bool>> RecoverAsync(bool state, IActivityContext context, CancellationToken cancellationToken = default)
        { Assert.Equal(Grow.Text, Value); return Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Completed()); }
    }
}

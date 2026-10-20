using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.ContractTests;

public sealed class DiagnosticMessageTests
{
    private const string Secret = "serialized-diagnostic-secret-62aa";

    [Fact]
    public async Task ExecutorCommitsImmutableDiagnosticsAcrossThePublicMessageBoundary()
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<MessageHarness.Data>("serialized-diagnostic")
            .Step<MessageHarness.Noop>().Step<Fail>().Build());
        await harness.ExecuteAsync();
        var raw = Assert.Single(await harness.Host.Store.GetDiagnosticsAsync(assign.Instance.InstanceId));
        Assert.Equal(Secret, raw.Message);
        Assert.Equal(typeof(InvalidOperationException).FullName, raw.ErrorType);
        Assert.Contains(nameof(Fail), raw.StackTrace);
        var trace = await harness.Host.Store.GetTraceAsync(assign.Instance.InstanceId);
        var failed = Assert.Single(trace, row => row.Entry.EntryType == TraceEntryType.ActivityFailed).Entry;
        Assert.Equal(raw.DiagnosticId, failed.Error!.DiagnosticId);
        Assert.Equal(failed.Error.ToString(), failed.ErrorMessage);
        Assert.Null(failed.StackTrace);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(trace));
        var saved = (await harness.Host.Store.GetWorkflowInstanceAsync(assign.Instance.InstanceId))!;
        var commit = CheckpointCommit.Create(saved, assign.Epoch, saved.Revision,
            diagnostics: [raw], retainedDiagnosticIds: [raw.DiagnosticId]);
        var copy = harness.Cross(new Checkpoint(assign.AssignmentId, commit)).Commit;
        Assert.Equal(raw, Assert.Single(copy.Diagnostics));
        Assert.Equal(commit.RetainedDiagnosticIds, copy.RetainedDiagnosticIds);
        Assert.Equal(commit.ContentHash, copy.ContentHash);
        var wire = JsonSerializer.SerializeToNode(new Checkpoint(assign.AssignmentId, commit), ExecutionMessageContext.Default.Checkpoint)!;
        wire["Commit"]!["Diagnostics"]![0]!["Message"] = "tampered";
        Assert.Throws<JsonException>(() => wire.Deserialize(ExecutionMessageContext.Default.Checkpoint));
        wire["Commit"]!["Diagnostics"]![0]!["Message"] = Secret;
        wire["Commit"]!["RetainedDiagnosticIds"] = new System.Text.Json.Nodes.JsonArray("different-id");
        Assert.Throws<JsonException>(() => wire.Deserialize(ExecutionMessageContext.Default.Checkpoint));
    }

    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => throw new InvalidOperationException(Secret);
    }
}

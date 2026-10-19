using IxIFlow.Tests.Infrastructure;
using System.Text.Json;
using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "tracing")]
[Trait("Spec", "G-01")]
public sealed class TraceMetadataTests
{
    [Fact]
    public void NewEntriesHaveIndependentMutableMetadata()
    {
        var entry = new ExecutionTraceEntry();
        var other = new ExecutionTraceEntry();

        Assert.Empty(entry.Metadata);
        Assert.Equal("", entry.ActivityName);
        entry.Metadata.Add("Attempt", 1);
        Assert.Empty(other.Metadata);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Metadata\":{}}")]
    public void MissingOrEmptyMetadataReadsAsAnEmptyDictionary(string json)
    {
        var entry = JsonSerializer.Deserialize<ExecutionTraceEntry>(json)!;

        Assert.Empty(entry.Metadata);
        entry.Metadata.Add("Attempt", 1);
        Assert.Equal(1, entry.Metadata["Attempt"]);
    }

    [Fact]
    public async Task CommitOmitsEmptyMetadataAndReadsItBackAsEmpty()
    {
        var entry = new ExecutionTraceEntry
        {
            EntryType = TraceEntryType.Released,
            Payload = new ReleaseTrace("operator")
        };
        var store = new InMemoryWorkflowStateRepository();
        var commit = CheckpointCommit.Create(new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = "trace" }, (await StoreTestSupport.LeaseAsync(store, "trace")).Epoch, 0,
            traceEntries: [entry]);

        using var json = JsonDocument.Parse(Assert.Single(commit.TraceEntries));
        Assert.False(json.RootElement.TryGetProperty("Metadata", out _));
        Assert.False(json.RootElement.TryGetProperty("ActivityName", out _));
        var restored = Assert.Single(commit.ReadTraceEntries());
        Assert.Empty(restored.Metadata);
        Assert.Equal(entry.TraceId, restored.TraceId);
        Assert.Equal(entry.Payload, restored.Payload);
    }

    [Fact]
    public async Task CommitPreservesPopulatedMetadata()
    {
        var entry = new ExecutionTraceEntry { EntryType = TraceEntryType.ActivityStarted, ActivityName = "Activity" };
        entry.Metadata.Add("Attempt", 2);
        var store = new InMemoryWorkflowStateRepository();
        var commit = CheckpointCommit.Create(new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = "trace" }, (await StoreTestSupport.LeaseAsync(store, "trace")).Epoch, 0,
            traceEntries: [entry]);

        using var json = JsonDocument.Parse(Assert.Single(commit.TraceEntries));
        Assert.Equal(2, json.RootElement.GetProperty("Metadata").GetProperty("Attempt").GetInt32());
        Assert.Equal("Activity", json.RootElement.GetProperty("ActivityName").GetString());
        var restored = Assert.Single(commit.ReadTraceEntries());
        Assert.Equal("Activity", restored.ActivityName);
        Assert.Equal(2, Assert.IsType<JsonElement>(restored.Metadata["Attempt"]).GetInt32());
    }

    [Fact]
    public void RefusedEventStorageOmitsEmptyMetadata()
    {
        var entry = WorkflowStoreRules.RefusedEventTrace(new WorkflowEvent("trace", "refused", "go", "{}"),
            EventRejection.NoOpenWait, DateTime.UtcNow);

        using var json = JsonDocument.Parse(WorkflowStoreRules.SerializeTrace(entry));
        Assert.False(json.RootElement.TryGetProperty("Metadata", out _));
        var restored = JsonSerializer.Deserialize<ExecutionTraceEntry>(json.RootElement)!;
        Assert.Empty(restored.Metadata);
        Assert.Equal(entry.Payload, restored.Payload);
    }
}

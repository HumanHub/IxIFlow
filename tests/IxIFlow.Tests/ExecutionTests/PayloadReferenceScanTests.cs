using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Payload reference scans share cached serializer metadata; each scan still collects only its own references.
/// </summary>
[Trait("Feature", "durability")]
public sealed class PayloadReferenceScanTests
{
    [Fact]
    public async Task ConcurrentReferenceScansAndFailedReadsKeepTheirOwnReferences()
    {
        await Task.WhenAll(Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            var reference = PayloadReference.Of(Encoding.UTF8.GetBytes("payload-" + worker));
            var message = new ChildCompleted { DataJson = StoredJson.FromReference(reference) };
            var json = JsonSerializer.Serialize(message);
            // Fail after reading a reference, then scan another document on the same thread.
            var invalid = JsonNode.Parse(json)!;
            invalid["Error"] = new JsonArray();
            for (var i = 0; i < 100; i++)
            {
                Assert.Throws<JsonException>(() => CheckpointPayloads.References<ChildCompleted>(invalid.ToJsonString()));
                Assert.Equal(reference, Assert.Single(CheckpointPayloads.References<ChildCompleted>(json)));
                Assert.Empty(CheckpointPayloads.References<ChildCompleted>(JsonSerializer.Serialize(new ChildCompleted
                {
                    // A reference-looking value inside application JSON is opaque.
                    DataJson = StoredJson.Inline(json)
                })));
            }
        })));
    }
}

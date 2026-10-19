using System.Security.Cryptography;
using System.Text.Json.Nodes;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

// The digest describes immutable rows, not the order in which declarations arrived.
// Write raw UTF-16 code units: ordinal identity includes case and trailing spaces.
internal sealed record DemandSet(string Id, WorkflowDemand[] Rows, WorkflowDemand? Pin)
{
    internal static DemandSet Create(IEnumerable<WorkflowDemand> demands)
    {
        var source = demands.ToArray();
        var pin = source.SingleOrDefault(d => d.Key == "host" && d.Value != null);
        var rows = source.Where(d => !ReferenceEquals(d, pin))
            .OrderBy(d => d.Key, StringComparer.Ordinal).ToArray();
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        void Write(string? value)
        {
            writer.Write(value?.Length ?? -1);
            if (value != null) foreach (var codeUnit in value) writer.Write((ushort)codeUnit);
        }
        writer.Write(1); // canonical format version
        writer.Write(rows.Length);
        foreach (var row in rows)
        {
            Write(row.Key);
            Write(row.Value);
            writer.Write(row.ExpiresAtUtc?.Ticks ?? -1);
            writer.Write(row.Inherit);
        }
        return new(Convert.ToHexString(SHA256.HashData(bytes.ToArray())), rows, pin);
    }

    internal static string StripPayload(string payload)
    {
        var root = JsonNode.Parse(payload)!;
        if (root["ExecutionStateJson"] is JsonObject state) state.Remove("Demands");
        return root.ToJsonString();
    }

    internal static WorkflowInstance Hydrate(WorkflowInstance instance, IEnumerable<WorkflowDemand> demands)
    {
        if (!ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson)) return instance;
        var state = JsonNode.Parse(instance.ExecutionStateJson)!;
        state["Demands"] = System.Text.Json.JsonSerializer.SerializeToNode(demands);
        instance.ExecutionStateJson = state.ToJsonString();
        return instance;
    }
}

using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>A capability required by an instance. Only explicitly inherited demands pass to children.</summary>
public sealed record WorkflowDemand(string Key, string? Value = null, DateTime? ExpiresAtUtc = null, bool Inherit = false)
{
    /// <summary>Expiry of the value restriction when it ends before the key-presence constraint.</summary>
    public DateTime? ValueExpiresAtUtc { get; init; }
    [JsonIgnore] internal LambdaExpression? Expression { get; init; }
    [JsonIgnore] internal Func<object, string?>? Evaluate { get; init; }
}

/// <summary>How a start responds when no live host advertises all its demands.</summary>
public enum WhenUnschedulable { Wait, Fail }

internal sealed record DemandSettings(TimeSpan HostPinLifetime)
{
    internal static readonly DemandSettings Default = new(TimeSpan.FromMinutes(5));
}

internal static class WorkflowDemands
{
    internal static void Validate(WorkflowDemand demand)
    {
        if (string.IsNullOrWhiteSpace(demand.Key) || demand.Key.Length > 400)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                "Demand keys must contain 1 to 400 characters");
    }

    internal static List<WorkflowDemand> Resolve(WorkflowDefinition definition, object data, WorkflowOptions options,
        string structureHash, IEnumerable<WorkflowDemand>? registrationDemands = null)
    {
        if (!Enum.IsDefined(options.WhenUnschedulable))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Unknown unschedulable policy");
        var declarations = definition.Demands.Concat(registrationDemands ?? []).Concat(options.Demands).Append(
            new WorkflowDemand($"workflow:{definition.Name}@{definition.Version}", structureHash));
        if (options.RequiredHostId is { } host)
        {
            if (host == Guid.Empty)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "A required host ID cannot be empty");
            declarations = declarations.Append(new WorkflowDemand("host", host.ToString("D")));
        }
        var resolved = new Dictionary<string, WorkflowDemand>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            Validate(declaration);
            var value = declaration.Evaluate == null ? declaration.Value : declaration.Evaluate(data);
            var demand = new WorkflowDemand(declaration.Key, value, declaration.ExpiresAtUtc, declaration.Inherit)
                { ValueExpiresAtUtc = declaration.ValueExpiresAtUtc };
            if (resolved.TryGetValue(demand.Key, out var previous))
            {
                if (previous.Value != null && value != null && previous.Value != value)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                        $"Conflicting demands for '{demand.Key}': '{previous.Value}' and '{value}'");
                static DateTime? Later(DateTime? first, DateTime? second) =>
                    first == null || second == null ? null : first > second ? first : second;
                var valueExpiry = previous.Value == null ? demand.ValueExpiresAtUtc ?? demand.ExpiresAtUtc
                    : value == null ? previous.ValueExpiresAtUtc ?? previous.ExpiresAtUtc
                    : Later(previous.ValueExpiresAtUtc ?? previous.ExpiresAtUtc, demand.ValueExpiresAtUtc ?? demand.ExpiresAtUtc);
                var presenceExpiry = Later(previous.ExpiresAtUtc, demand.ExpiresAtUtc);
                demand = demand with { Value = previous.Value ?? value, Inherit = previous.Inherit || demand.Inherit,
                    ExpiresAtUtc = presenceExpiry,
                    ValueExpiresAtUtc = previous.Value == null && value == null || valueExpiry == presenceExpiry ? null : valueExpiry };
            }
            resolved[demand.Key] = demand;
        }
        return resolved.Values.OrderBy(demand => demand.Key, StringComparer.Ordinal).ToList();
    }

    internal static List<WorkflowDemand> Saved(WorkflowInstance instance) =>
        ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson)
            ? ExecutionCheckpoint.ReadTrunk(instance.ExecutionStateJson).Demands : [];

    // Only the creating transaction knows authoritative time. The immutable commit still hashes the request.
    internal static string StampStartPayload(string payload, DateTime now)
    {
        var instance = JsonSerializer.Deserialize<WorkflowInstance>(payload)!;
        if (!ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson)) return payload;
        var state = JsonNode.Parse(instance.ExecutionStateJson)!;
        if (state["HostPinLifetime"] == null) return payload;
        var lifetime = state["HostPinLifetime"]!.Deserialize<TimeSpan>();
        foreach (var demand in state["Demands"]!.AsArray())
            if (demand!["Key"]!.GetValue<string>() == "host")
                demand["ExpiresAtUtc"] = JsonValue.Create(now + lifetime);
        state.AsObject().Remove("HostPinLifetime");
        var root = JsonNode.Parse(payload)!;
        root["ExecutionStateJson"] = state;
        return root.ToJsonString();
    }

    internal static async Task RefuseUnschedulableAsync(IWorkflowStateRepository store,
        IReadOnlyList<WorkflowDemand> demands, CancellationToken token)
    {
        if (!await store.CanScheduleAsync(demands, token))
            throw new WorkflowEngineException(WorkflowErrorKind.Unschedulable,
                "No live host advertises all the workflow's demands");
    }
}

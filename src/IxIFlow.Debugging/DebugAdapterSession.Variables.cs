using System.Text.Json;
using System.Text.RegularExpressions;
using IxIFlow.Core;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;

namespace IxIFlow.Debugging;

public sealed partial class DebugAdapterSession
{
    private sealed record FrameHandle(string Thread, InspectionFrame Frame, long Revision, long Generation, long? Cursor);

    private sealed record VariableHandle(
        string Thread,
        string Scope,
        string[] Path,
        JsonElement Value,
        long Revision,
        long Generation,
        long? Cursor,
        string? DataFrameId);

    private readonly Dictionary<int, FrameHandle> _frames = [];
    private readonly Dictionary<int, VariableHandle> _variables = [];
    private long Revision => _replay?.Current.Revision ?? _inspection!.Revision;
    private long Generation => _inspection!.DebugState.PauseGeneration;
    private long? Cursor => _replay?.Current.Sequence;

    private void ClearHandles()
    {
        _frames.Clear();
        _variables.Clear();
    }

    private void Check(long revision, long generation, long? cursor)
    {
        if (revision != Revision || generation != Generation || cursor != Cursor)
            throw new ProtocolException("Stale variable or frame handle; refresh the stopped view");
    }

    private FrameHandle Frame(int id)
    {
        if (!_frames.TryGetValue(id, out var frame))
            throw new ProtocolException("Unknown or stale frame handle");

        Check(frame.Revision, frame.Generation, frame.Cursor);

        return frame;
    }

    private int Handle(string thread, string scope, string[] path, JsonElement value, string? dataFrameId = null)
    {
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return 0;

        if (_variables.Count >= 10000)
            throw new ProtocolException("Variable handle limit reached");

        var id = checked(++_nextId);
        _variables[id] = new(thread, scope, path, value.Clone(), Revision, Generation, Cursor, dataFrameId);

        return id;
    }

    private InspectionFrame? GraphFrame(string thread, InspectionFrame? frame = null)
    {
        var threads = CurrentThreads();
        InspectionThread? current = threads.Single(t => t.ContinuationId == thread);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (current != null && visited.Add(current.ContinuationId))
        {
            var frames = current.Frames;
            var index = frame == null ? frames.Count - 1 : frames.ToList().FindIndex(f => f.ActivationId == frame.ActivationId);
            var graph = frames.Take(index + 1).LastOrDefault(f => f.GraphId != null);

            if (graph != null)
                return graph;

            // Ordinary fork stacks omit enclosing frames; use parents from the same live/replay position.
            current = threads.SingleOrDefault(t => t.ContinuationId == current.ParentContinuationId);
            frame = null;
        }

        return null;
    }

    private string? DataFrame(string thread, InspectionFrame? selected = null)
    {
        var frames = CurrentThreads().Single(t => t.ContinuationId == thread).Frames;
        var count = selected == null ? frames.Count : frames.ToList().FindIndex(f => f.ActivationId == selected.ActivationId) + 1;

        return frames.Take(count).LastOrDefault(f => f.Kind == "InlineWorkflow")?.ActivationId;
    }

    private JsonElement DataValue(string thread, string? frameId)
    {
        var data = _replay == null ? _inspection!.Data : _replay.Current.Data;

        return data?.FirstOrDefault(d => frameId == null ? d.ContinuationId == thread && d.FrameId == null : d.FrameId == frameId)?.Value
            ?? throw new ProtocolException("Values are unavailable at this replay position");
    }

    private JsonElement ScopeValue(string thread, string scope, InspectionFrame? frame = null)
    {
        if (scope == "graph")
        {
            var graph = GraphFrame(thread, frame) ?? throw new ProtocolException("No graph in the selected frame");

            return JsonSerializer.SerializeToElement(new
            {
                id = graph.GraphId, node = graph.Node,
                phase = graph.Phase, visit = graph.Visit, armOccurrence = graph.ArmOccurrence,
                via = graph.Via, pending = graph.Pending
            });
        }

        if (scope == "data")
        {
            return DataValue(thread, DataFrame(thread, frame));
        }

        if (scope == "fault")
        {
            var faults = _replay == null ? _inspection!.CatchFaults : _replay.Current.CatchFaults;

            if (faults == null)
                throw new ProtocolException("Historical Catch values are unavailable");

            var current = CurrentThreads().Single(t => t.ContinuationId == thread);
            var selected = frame;

            while (true)
            {
                var count = selected == null
                    ? current.Frames.Count
                    : current.Frames.ToList().FindIndex(f => f.ActivationId == selected.ActivationId) + 1;

                var frames = current.Frames.Take(count).Select(f => f.ActivationId).ToHashSet(StringComparer.Ordinal);
                var fault = faults.LastOrDefault(f => f.ContinuationId == current.ContinuationId && frames.Contains(f.FrameId));

                if (fault != null)
                    return fault.Value;

                current = CurrentThreads().SingleOrDefault(t => t.ContinuationId == current.ParentContinuationId);

                if (current == null)
                    throw new ProtocolException("No Catch in the selected frame");

                selected = null;
            }
        }

        if (_replay != null)
            throw new ProtocolException("Historical scope values are unavailable");

        return scope switch
        {
            "scopes" => JsonSerializer.SerializeToElement(CurrentThreads().Single(t => t.ContinuationId == thread)
                .ScopesInForce.Select(id => _inspection!.Scopes.Single(s => s.ActivationId == id))
                .ToDictionary(s => s.ActivationId, s => new
                {
                    s.ActivationId, s.OwnerContinuationId, s.StepId, s.ValueType,
                    Threading = s.Threading.ToString(), Concurrency = s.Concurrency.ToString(),
                    s.Phase, s.RecordedOpens, s.Inputs, s.Outputs
                }, StringComparer.Ordinal)),
            "attempt" => JsonSerializer.SerializeToElement(_inspection!.PendingAttempts.Where(p => p.ContinuationId == thread)),
            "waits" => JsonSerializer.SerializeToElement(_inspection!.Waits.Where(w => w.ContinuationId == thread)),
            "item" => JsonSerializer.SerializeToElement(new
            {
                index = CurrentThreads().Single(t => t.ContinuationId == thread)
                    .Frames.LastOrDefault(f => f.ForEachItemIndex != null)?.ForEachItemIndex
            }),
            _ => throw new ProtocolException("This scope is unavailable until classified inspection is integrated")
        };
    }

    private async Task<object> ScopesAsync(JObject args, CancellationToken token)
    {
        await InspectAsync(token);
        var frame = Frame(args.Value<int>("frameId"));
        var scopes = new List<object>();

        foreach (var (name, scope) in new[]
                 {
                     ("Workflow data", "data"), ("Previous step (unavailable)", "previous"),
                     ("Catch fault", "fault"), ("Item", "item"), ("Attempt", "attempt"), ("Waits", "waits"), ("Scopes", "scopes"),
                     ("Graph", "graph")
                 })
        {
            if (scope == "graph" && GraphFrame(frame.Thread, frame.Frame) == null)
                continue;

            var reference = 0;

            try
            {
                reference = Handle(frame.Thread, scope, [], ScopeValue(frame.Thread, scope, frame.Frame),
                    DataFrame(frame.Thread, frame.Frame));
            }
            catch (ProtocolException)
            {
            }

            scopes.Add(new { name, variablesReference = reference, expensive = false });
        }

        return new { scopes };
    }

    private static IEnumerable<(string Name, JsonElement Value)> Children(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(p => (p.Name, p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select((v, i) => (i.ToString(System.Globalization.CultureInfo.InvariantCulture), v)),
        _ => []
    };

    private object Variable(string name, JsonElement value, string thread, string scope, string[] path, string? dataFrameId = null) => new
    {
        name, value = Display(value), type = value.ValueKind.ToString(),
        variablesReference = Handle(thread, scope, path, value, dataFrameId),
        evaluateName = scope + string.Concat(path.Select(p => int.TryParse(p, out _) ? $"[{p}]" : "." + p)),
        namedVariables = value.ValueKind == JsonValueKind.Object ? (int?)value.EnumerateObject().Count() : null,
        indexedVariables = value.ValueKind == JsonValueKind.Array ? (int?)value.GetArrayLength() : null,
        presentationHint = scope == "data" && _replay == null ? null : new { attributes = new[] { "readOnly" } }
    };

    private static string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{…}", JsonValueKind.Array => $"[{value.GetArrayLength()}]",
        _ => value.GetRawText()
    };

    private async Task<object> VariablesAsync(JObject args, CancellationToken token)
    {
        await InspectAsync(token);

        if (!_variables.TryGetValue(args.Value<int>("variablesReference"), out var handle))
            throw new ProtocolException("Unknown or stale variable handle");

        Check(handle.Revision, handle.Generation, handle.Cursor);
        var start = Math.Max(0, args.Value<int?>("start") ?? 0);
        var count = args.Value<int?>("count") ?? 0;

        if (count <= 0)
            count = 1000;

        var filter = args.Value<string>("filter");

        if (filter == "indexed" && handle.Value.ValueKind != JsonValueKind.Array ||
            filter == "named" && handle.Value.ValueKind == JsonValueKind.Array)
            return new { variables = Array.Empty<object>() };

        return new
        {
            variables = Children(handle.Value).Skip(start).Take(Math.Min(1000, count))
                .Select(c => Variable(c.Name, c.Value, handle.Thread, handle.Scope, [.. handle.Path, c.Name], handle.DataFrameId)).ToArray()
        };
    }

    private async Task<object> SetVariableAsync(JObject args, CancellationToken token)
    {
        await InspectAsync(token);

        if (!_variables.TryGetValue(args.Value<int>("variablesReference"), out var handle))
            throw new ProtocolException("Unknown or stale variable handle");

        Check(handle.Revision, handle.Generation, handle.Cursor);
        var name = Required(args, "name");

        if (!Children(handle.Value).Any(c => c.Name == name))
            throw new ProtocolException("Unknown variable");

        var value = JsonSerializer.Deserialize<JsonElement>(Required(args, "value"));
        await EditAsync(handle.Thread, handle.Scope, [.. handle.Path, name], value, token, handle.DataFrameId);
        await InspectAsync(token);
        var safeValue = handle.Scope == "data" ? DataValue(handle.Thread, handle.DataFrameId) : ScopeValue(handle.Thread, handle.Scope);

        foreach (var part in handle.Path.Append(name))
            safeValue = Child(safeValue, part);

        return new { value = Display(safeValue), variablesReference = 0 };
    }

    private async Task EditAsync(string thread, string scope, string[] path, JsonElement value, CancellationToken token,
        string? dataFrameId = null)
    {
        if (_replay != null || scope != "data" || _inspection!.Summary.Status != WorkflowStatus.Paused)
            throw new ProtocolException("Only paused workflow data is editable");

        await AuthorizeAsync(DebugOperation.Edit, token);

        await _operations.EditDataAsync(_instanceId!, Guid.NewGuid().ToString("N"), _inspection.Revision,
            new(_inspection.DebugState.PauseGeneration, thread, dataFrameId), [new(path, value)], _endpoint.Actor, _incarnation, token);

        ClearHandles();
        Emit<InvalidatedEvent>(new { areas = new[] { "variables", "stacks" } });
    }

    private static readonly Regex PathExpression =
        new(@"^(data|WorkflowData|previous|fault|item|attempt|waits|graph)(\.[A-Za-z_][A-Za-z0-9_]*|\[(0|[1-9][0-9]*)\])*$",
            RegexOptions.CultureInvariant);

    private static (string Scope, string[] Path) ParsePath(string expression)
    {
        if (expression.Length > 4096 || !PathExpression.IsMatch(expression))
            throw new ProtocolException("Evaluate accepts scope paths only");

        var parts = expression.Replace("[", ".").Replace("]", "").Split('.');

        return (parts[0] == "WorkflowData" ? "data" : parts[0], parts[1..]);
    }

    private async Task<object> EvaluateAsync(JObject args, bool edit, CancellationToken token)
    {
        await InspectAsync(token);
        var frame = args.Value<int?>("frameId") is { } frameId ? Frame(frameId) : null;

        var thread = frame?.Thread ?? _inspection!.DebugState.Stop?.BranchId ?? CurrentThreads().FirstOrDefault()?.ContinuationId
            ?? throw new ProtocolException("No selected thread");

        var (scope, path) = ParsePath(Required(args, "expression"));

        if (edit)
        {
            var replacement = JsonSerializer.Deserialize<JsonElement>(Required(args, "value"));
            await EditAsync(thread, scope, path, replacement, token, DataFrame(thread, frame?.Frame));
            await InspectAsync(token);
            var safeValue = ScopeValue(thread, scope, frame?.Frame);

            foreach (var part in path)
                safeValue = Child(safeValue, part);

            return new { value = Display(safeValue), variablesReference = 0 };
        }

        var value = ScopeValue(thread, scope, frame?.Frame);

        foreach (var part in path)
            value = Child(value, part);

        return new
        {
            result = Display(value), type = value.ValueKind.ToString(),
            variablesReference = Handle(thread, scope, path, value, DataFrame(thread, frame?.Frame))
        };
    }

    private static JsonElement Child(JsonElement value, string part) =>
        value.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index) && index >= 0 && index < value.GetArrayLength()
            ? value[index]
            : value.ValueKind == JsonValueKind.Object && value.TryGetProperty(part, out var member)
                ? member
                : throw new ProtocolException("Path is unavailable");
}
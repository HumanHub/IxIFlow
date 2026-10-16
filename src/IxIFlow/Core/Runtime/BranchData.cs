using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// One change a parallel branch made to its copy of the workflow data, against the data it was forked
/// from (D-14, spec E-01): a JSON property path and the new value as JSON, or the property's removal.
/// </summary>
internal sealed class DataChange
{
    public List<string> Path { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Removed { get; set; }
}

/// <summary>
/// Branch copies of the workflow data and their merge at a join (D-14, spec E-01 to E-05). Values are
/// compared as JSON: objects and dictionaries per property, arrays and scalars whole, and a property
/// with a combiner whole. At the join, a path changed by one branch takes its value; a path changed by
/// several, or a path and one inside it, conflicts unless a combiner merges them.
/// </summary>
internal static class BranchData
{
    /// <summary>A branch's own copy of <paramref name="data"/>.</summary>
    public static object Copy(object data) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(data, data.GetType()), data.GetType())!;

    /// <summary>The changes that turn <paramref name="baseline"/> into <paramref name="branch"/>.</summary>
    public static List<DataChange> Changes(object baseline, object branch)
    {
        var changes = new List<DataChange>();
        Compare(Node(baseline), Node(branch), baseline.GetType(), [], changes);
        return changes;
    }

    /// <summary>A branch's copy rebuilt from the data it was forked from and its saved changes.</summary>
    public static object Rebuild(object baseline, IReadOnlyList<DataChange> changes)
    {
        var node = Node(baseline);
        foreach (var change in changes)
            node = Apply(node, change);
        return node.Deserialize(baseline.GetType())!;
    }

    /// <summary>
    /// <paramref name="baseline"/> with every branch's changes, given in branch declaration order, or a
    /// <see cref="JoinConflictException"/> when two branches changed the same data without a combiner.
    /// </summary>
    public static object Merge(object baseline, IReadOnlyList<IReadOnlyList<DataChange>> branches)
    {
        var type = baseline.GetType();
        var node = Node(baseline);
        var byPath = new Dictionary<string, List<(int Branch, DataChange Change)>>(StringComparer.Ordinal);
        for (var index = 0; index < branches.Count; index++)
            foreach (var change in branches[index])
            {
                var key = Key(change.Path);
                if (!byPath.TryGetValue(key, out var list))
                    byPath[key] = list = [];
                list.Add((index, change));
            }

        var paths = byPath.Values.Select(list => list[0].Change.Path).ToList();
        foreach (var path in paths)
            foreach (var other in paths)
                if (!ReferenceEquals(path, other) && Inside(other, path) &&
                    byPath[Key(path)].Concat(byPath[Key(other)]).Select(item => item.Branch).Distinct().Count() > 1)
                    throw Conflict(path, $"Parallel branches changed '{Display(path)}' and '{Display(other)}', " +
                                         "which is inside it");

        foreach (var (key, list) in byPath)
        {
            var path = list[0].Change.Path;
            var combiner = CombinerAt(type, path);
            if (list.Count == 1 && combiner == null)
            {
                node = Apply(node, list[0].Change);
                continue;
            }
            if (combiner == null)
                throw Conflict(path, $"Parallel branches changed '{Display(path)}' and it has no combiner");
            if (list.Any(item => item.Change.Removed))
                throw Conflict(path, $"A parallel branch removed '{Display(path)}', which has a combiner");
            node = Apply(node, new DataChange
            {
                Path = path,
                Value = Combine(combiner, ValueAt(node, path), list.Select(item => item.Change.Value!).ToList())
            });
        }
        return node.Deserialize(type)!;
    }

    /// <summary>
    /// <paramref name="root"/> with the committed changes of every live branch laid over it, in fork
    /// order: a view of what the branches of the open joins have done so far, for operators and tests.
    /// It is not a merge; branches that changed the same data show the later one.
    /// </summary>
    public static object Overlay(object root, ExecutionCheckpoint checkpoint)
    {
        var node = Node(root);
        foreach (var branch in checkpoint.Continuations.Where(item => item.DataChanges != null))
            foreach (var change in branch.DataChanges!)
                node = Apply(node, change);
        return node.Deserialize(root.GetType())!;
    }

    private static JsonNode Node(object data) =>
        JsonSerializer.SerializeToNode(data, data.GetType()) ?? new JsonObject();

    private static void Compare(JsonNode? before, JsonNode? after, Type? type, List<string> path,
        List<DataChange> changes)
    {
        if (before is JsonObject beforeObject && after is JsonObject afterObject && type != null &&
            (path.Count == 0 || CombinerAt(type, path) == null))
        {
            foreach (var (name, value) in afterObject)
            {
                var child = new List<string>(path) { name };
                if (!beforeObject.TryGetPropertyValue(name, out var old))
                    changes.Add(new DataChange { Path = child, Value = value?.ToJsonString() ?? "null" });
                else
                    Compare(old, value, type, child, changes);
            }
            foreach (var (name, _) in beforeObject)
                if (!afterObject.ContainsKey(name))
                    changes.Add(new DataChange { Path = new List<string>(path) { name }, Removed = true });
            return;
        }
        if (!JsonNode.DeepEquals(before, after))
            changes.Add(new DataChange { Path = path, Value = after?.ToJsonString() ?? "null" });
    }

    private static JsonNode Apply(JsonNode root, DataChange change)
    {
        if (change.Path.Count == 0)
            return change.Removed ? new JsonObject() : JsonNode.Parse(change.Value!) ?? new JsonObject();
        var parent = root;
        foreach (var segment in change.Path.Take(change.Path.Count - 1))
        {
            var objectNode = parent.AsObject();
            if (objectNode[segment] is not JsonObject next)
                objectNode[segment] = next = new JsonObject();
            parent = next;
        }
        var target = parent.AsObject();
        var name = change.Path[^1];
        if (change.Removed)
            target.Remove(name);
        else
            target[name] = change.Value == null ? null : JsonNode.Parse(change.Value);
        return root;
    }

    private static JsonNode? ValueAt(JsonNode root, List<string> path)
    {
        JsonNode? node = root;
        foreach (var segment in path)
            node = node is JsonObject objectNode && objectNode.TryGetPropertyValue(segment, out var next) ? next : null;
        return node;
    }

    private static string Combine(Type combinerType, JsonNode? original, IReadOnlyList<string> values)
    {
        var valueType = combinerType.GetInterfaces()
            .Single(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(ICombiner<>))
            .GetGenericArguments()[0];
        var listType = typeof(List<>).MakeGenericType(valueType);
        var list = (IList)Activator.CreateInstance(listType)!;
        foreach (var value in values)
            list.Add(JsonSerializer.Deserialize(value, valueType));
        var originalValue = original == null ? null : original.Deserialize(valueType);
        var combiner = Activator.CreateInstance(combinerType)!;
        var result = combinerType.GetMethod(nameof(ICombiner<object>.Combine))!.Invoke(combiner, [originalValue, list]);
        return JsonSerializer.Serialize(result, valueType);
    }

    /// <summary>The combiner declared on the property at <paramref name="path"/> of <paramref name="type"/>, if any.</summary>
    public static Type? CombinerAt(Type type, IReadOnlyList<string> path)
    {
        var current = type;
        PropertyInfo? property = null;
        foreach (var segment in path)
        {
            if (DictionaryValueType(current) is { } valueType)
            {
                current = valueType;
                property = null;
                continue;
            }
            property = PropertyNamed(current, segment);
            if (property == null)
                return null;
            current = property.PropertyType;
        }
        return property?.GetCustomAttribute<CombineAttribute>()?.CombinerType;
    }

    private static PropertyInfo? PropertyNamed(Type type, string jsonName) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(property =>
            (property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name) == jsonName);

    private static Type? DictionaryValueType(Type type) =>
        type.GetInterfaces().Append(type)
            .FirstOrDefault(item => item.IsGenericType &&
                                    item.GetGenericTypeDefinition() == typeof(IDictionary<,>) &&
                                    item.GetGenericArguments()[0] == typeof(string))
            ?.GetGenericArguments()[1];

    private static bool Inside(List<string> path, List<string> owner) =>
        path.Count > owner.Count && owner.SequenceEqual(path.Take(owner.Count));

    private static string Key(List<string> path) => string.Join('\u001f', path);

    private static string Display(List<string> path) => path.Count == 0 ? "(the whole data)" : string.Join('.', path);

    private static JoinConflictException Conflict(List<string> path, string message) => new(Display(path), message);
}

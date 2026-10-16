using System.Collections;
using System.ComponentModel;
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
/// several, or a path and one inside it, conflicts unless a combiner merges them. Changes are applied to
/// the data object itself, property by property, so what no branch changed keeps its value and the object
/// keeps its identity; a branch's copy is a JSON copy taken at the fork.
/// </summary>
internal static class BranchData
{
    /// <summary>A branch's own copy of <paramref name="data"/>.</summary>
    public static object Copy(object data) => Copy(JsonSerializer.Serialize(data, data.GetType()), data.GetType());

    /// <summary>A copy from a serialized form, so a fork serializes its parent's data once.</summary>
    public static object Copy(string json, Type type) => JsonSerializer.Deserialize(json, type)!;

    /// <summary>
    /// The changes that turn <paramref name="baseline"/> into <paramref name="branch"/>. A property with a
    /// combiner, on the data type or in <paramref name="overrides"/>, is compared whole.
    /// </summary>
    public static List<DataChange> Changes(object baseline, object branch,
        IReadOnlyDictionary<string, Type>? overrides = null) =>
        Changes(Node(baseline), branch, baseline.GetType(), overrides);

    /// <inheritdoc cref="Changes(object, object, IReadOnlyDictionary{string, Type}?)"/>
    public static List<DataChange> Changes(JsonNode baseline, object branch, Type type,
        IReadOnlyDictionary<string, Type>? overrides = null)
    {
        var changes = new List<DataChange>();
        Compare(baseline, Node(branch), type, overrides, [], changes);
        return changes;
    }

    public static JsonNode Node(object data) =>
        JsonSerializer.SerializeToNode(data, data.GetType()) ?? new JsonObject();

    /// <summary>A branch's copy rebuilt from the data it was forked from and its saved changes.</summary>
    public static object Rebuild(object baseline, IReadOnlyList<DataChange> changes)
    {
        var copy = Copy(baseline);
        foreach (var change in changes)
            ApplyTo(copy, change);
        return copy;
    }

    /// <summary>
    /// The changes the join applies to the parent's data: every branch's changes, given in branch
    /// declaration order, with combiners applied, or a <see cref="JoinConflictException"/> when branches
    /// changed the same data, or a property and one inside it, without a combiner that merges them.
    /// </summary>
    public static List<DataChange> Merge(object baseline, IReadOnlyList<IReadOnlyList<DataChange>> branches,
        IReadOnlyDictionary<string, Type>? overrides = null)
    {
        var type = baseline.GetType();
        var byPath = new Dictionary<string, List<(int Branch, DataChange Change)>>(StringComparer.Ordinal);
        var order = new List<string>();
        for (var index = 0; index < branches.Count; index++)
            foreach (var change in branches[index])
            {
                var key = Key(change.Path);
                if (!byPath.TryGetValue(key, out var list))
                {
                    byPath[key] = list = [];
                    order.Add(key);
                }
                list.Add((index, change));
            }

        // A change and one inside it from different branches conflict: each path checks its prefixes.
        foreach (var key in order)
        {
            var path = byPath[key][0].Change.Path;
            for (var length = 0; length < path.Count; length++)
                if (byPath.TryGetValue(Key(path.Take(length).ToList()), out var outer) &&
                    outer.Concat(byPath[key]).Select(item => item.Branch).Distinct().Count() > 1)
                    throw Conflict(outer[0].Change.Path, $"Parallel branches changed " +
                        $"'{Display(outer[0].Change.Path)}' and '{Display(path)}', which is inside it");
        }

        var baselineNode = Node(baseline);
        var merged = new List<DataChange>();
        foreach (var key in order)
        {
            var list = byPath[key];
            var path = list[0].Change.Path;
            var combiner = CombinerAt(type, path, overrides);
            if (list.Count == 1 && combiner == null)
            {
                merged.Add(list[0].Change);
                continue;
            }
            if (combiner == null)
                throw Conflict(path, $"Parallel branches changed '{Display(path)}' and it has no combiner");
            if (list.Any(item => item.Change.Removed))
                throw Conflict(path, $"A parallel branch removed '{Display(path)}', which has a combiner");
            merged.Add(new DataChange
            {
                Path = path,
                Value = Combine(combiner, path, ValueAt(baselineNode, path),
                    list.Select(item => item.Change.Value!).ToList())
            });
        }
        return merged;
    }

    /// <summary>Applies <paramref name="change"/> to <paramref name="data"/> in place.</summary>
    public static void ApplyTo(object data, DataChange change)
    {
        if (change.Path.Count == 0)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                "A branch replaced the whole workflow data, which a join cannot apply in place");
        Set(data, data.GetType(), change, 0);
    }

    /// <summary>
    /// <paramref name="root"/> with the committed changes of every live branch laid over it, in fork
    /// order: a view of what the branches of the open joins have done so far, for operators and tests.
    /// It is not a merge; branches that changed the same data show the later one.
    /// </summary>
    public static object Overlay(object root, ExecutionCheckpoint checkpoint)
    {
        var view = Copy(root);
        foreach (var branch in checkpoint.Continuations.Where(item => item.DataChanges != null))
            foreach (var change in branch.DataChanges!)
                ApplyTo(view, change);
        return view;
    }

    private static void Compare(JsonNode? before, JsonNode? after, Type? type,
        IReadOnlyDictionary<string, Type>? overrides, List<string> path, List<DataChange> changes)
    {
        if (before is JsonObject beforeObject && after is JsonObject afterObject && type != null &&
            (path.Count == 0 || CombinerAt(type, path, overrides) == null))
        {
            foreach (var (name, value) in afterObject)
            {
                var child = new List<string>(path) { name };
                if (!beforeObject.TryGetPropertyValue(name, out var old))
                    changes.Add(new DataChange { Path = child, Value = value?.ToJsonString() ?? "null" });
                else
                    Compare(old, value, type, overrides, child, changes);
            }
            foreach (var (name, _) in beforeObject)
                if (!afterObject.ContainsKey(name))
                    changes.Add(new DataChange { Path = new List<string>(path) { name }, Removed = true });
            return;
        }
        if (!JsonNode.DeepEquals(before, after))
            changes.Add(new DataChange { Path = path, Value = after?.ToJsonString() ?? "null" });
    }

    // Sets the value at change.Path[depth..] inside target and returns target, so a struct on the way is
    // written back to its owner.
    private static object Set(object target, Type type, DataChange change, int depth)
    {
        var segment = change.Path[depth];
        var last = depth == change.Path.Count - 1;
        if (target is IDictionary dictionary && DictionaryTypes(type) is { } types)
        {
            var (keyType, valueType) = types;
            var key = DictionaryKey(segment, keyType);
            if (last)
            {
                if (change.Removed)
                    dictionary.Remove(key);
                else
                    dictionary[key] = Read(change.Value, valueType);
                return target;
            }
            var inner = dictionary[key] ?? throw Missing(change);
            dictionary[key] = Set(inner, inner.GetType(), change, depth + 1);
            return target;
        }

        var property = PropertyNamed(type, segment) ?? throw Missing(change);
        if (last)
        {
            var value = change.Removed ? Default(property.PropertyType) : Read(change.Value, property.PropertyType);
            Assign(target, property, value, change);
            return target;
        }
        var current = property.GetValue(target) ?? throw Missing(change);
        var updated = Set(current, current.GetType(), change, depth + 1);
        if (!ReferenceEquals(updated, current) || property.PropertyType.IsValueType)
            Assign(target, property, updated, change);
        return target;
    }

    private static void Assign(object target, PropertyInfo property, object? value, DataChange change)
    {
        if (property.SetMethod == null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"A join cannot set '{Display(change.Path)}', which has no setter");
        property.SetValue(target, value);
    }

    private static object? Read(string? json, Type type) =>
        json == null ? Default(type) : JsonSerializer.Deserialize(json, type);

    private static object? Default(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

    private static object DictionaryKey(string segment, Type keyType) =>
        keyType == typeof(string) ? segment
        : TypeDescriptor.GetConverter(keyType).ConvertFromInvariantString(segment)
          ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, $"'{segment}' is not a {keyType.Name} key");

    private static (Type Key, Type Value)? DictionaryTypes(Type type) =>
        type.GetInterfaces().Append(type)
            .FirstOrDefault(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            is { } dictionary
            ? (dictionary.GetGenericArguments()[0], dictionary.GetGenericArguments()[1])
            : null;

    private static WorkflowEngineException Missing(DataChange change) =>
        new(WorkflowErrorKind.InvalidState, $"A join found nothing at '{Display(change.Path)}' to apply a change to");

    private static JsonNode? ValueAt(JsonNode root, List<string> path)
    {
        JsonNode? node = root;
        foreach (var segment in path)
            node = node is JsonObject objectNode && objectNode.TryGetPropertyValue(segment, out var next) ? next : null;
        return node;
    }

    // Runs a combiner through ICombiner<T>, so an explicit implementation works too; anything it throws,
    // or a value it cannot read, fails the join for its property.
    private static string Combine(Type combinerType, List<string> path, JsonNode? original, IReadOnlyList<string> values)
    {
        try
        {
            var contract = CombinerContract(combinerType);
            var valueType = contract.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(valueType))!;
            foreach (var value in values)
                list.Add(JsonSerializer.Deserialize(value, valueType));
            var originalValue = original?.Deserialize(valueType);
            var combiner = Activator.CreateInstance(combinerType)!;
            var result = contract.GetMethod(nameof(ICombiner<object>.Combine))!.Invoke(combiner, [originalValue, list]);
            return JsonSerializer.Serialize(result, valueType);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var reason = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            throw Conflict(path, $"The combiner of '{Display(path)}' could not merge the branches: {reason.Message}");
        }
    }

    /// <summary>The <see cref="ICombiner{T}"/> a combiner type implements, or an invalid definition.</summary>
    public static Type CombinerContract(Type combinerType)
    {
        var contracts = combinerType.GetInterfaces()
            .Where(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(ICombiner<>))
            .ToArray();
        return contracts.Length == 1 && combinerType.GetConstructor(Type.EmptyTypes) != null
            ? contracts[0]
            : throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"'{combinerType.Name}' must implement ICombiner<T> once and have a parameterless constructor");
    }

    /// <summary>
    /// The combiner for the property at <paramref name="path"/> of <paramref name="type"/>: the parallel
    /// step's override for that path, or the one declared on the property, if any.
    /// </summary>
    public static Type? CombinerAt(Type type, IReadOnlyList<string> path,
        IReadOnlyDictionary<string, Type>? overrides = null)
    {
        if (overrides != null && overrides.TryGetValue(string.Join('.', path), out var chosen))
            return chosen;
        var current = type;
        PropertyInfo? property = null;
        foreach (var segment in path)
        {
            if (DictionaryTypes(current) is { } types)
            {
                current = types.Value;
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

    /// <summary>The JSON path of a property chain given by CLR names, such as <c>Order.Total</c>.</summary>
    public static string JsonPath(Type type, string clrPath)
    {
        var names = new List<string>();
        var current = type;
        foreach (var name in clrPath.Split('.'))
        {
            var property = current.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"'{type.Name}' has no property path '{clrPath}'");
            names.Add(JsonName(property));
            current = property.PropertyType;
        }
        return string.Join('.', names);
    }

    public static string JsonName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;

    private static PropertyInfo? PropertyNamed(Type type, string jsonName) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(property => JsonName(property) == jsonName);

    // An unambiguous key for a path, whatever its segments contain.
    private static string Key(List<string> path) => JsonSerializer.Serialize(path);

    private static string Display(List<string> path) => path.Count == 0 ? "(the whole data)" : string.Join('.', path);

    private static JoinConflictException Conflict(List<string> path, string message) => new(Display(path), message);
}

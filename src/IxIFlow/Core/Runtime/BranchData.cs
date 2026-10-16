using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// One change a parallel branch made to its copy of the workflow data, against the data it was forked
/// from (D-14, spec E-01): a JSON property path and the new value as JSON, or a dictionary entry's removal.
/// </summary>
internal sealed class DataChange
{
    public List<string> Path { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Removed { get; set; }
}

/// <summary>A branch taking part in a merge: its changes, and its copy of the data for its combiners.</summary>
internal sealed record MergingBranch(IReadOnlyList<DataChange> Changes, Func<object> Data);

/// <summary>
/// The outcome of a merge: the parent's data as the join leaves it, read back whole, and the paths the
/// join changes in it.
/// </summary>
internal sealed record MergedData(object Data, IReadOnlyList<List<string>> Paths);

/// <summary>
/// Branch copies of the workflow data and their merge at a join (D-14, spec E-01 to E-07). The data is
/// seen as System.Text.Json sees it, with every property written, null and default ones included, so a
/// copy reads back what it was given. A class object is compared per property and a mutable dictionary
/// class per key; anything else is one value: lists, structs, polymorphic values, objects with extension
/// data or with a converter of their own. A property with no setter is not branch data when it is one
/// value, such as a computed one; when it is compared per property, its object is merged in place.
/// At the join, a change takes its branch's value; changes from several branches to one property, or to a
/// property and one inside it, conflict unless a combiner on that property or one above it merges them,
/// from the branches' own values. The merge is laid over the parent's data as JSON and read back whole
/// before anything changes, so a merge the data type cannot take fails the join and leaves the data as it
/// was; only then are the changed paths set on the parent's object, which keeps its identity and whatever
/// no branch changed.
/// </summary>
internal static class BranchData
{
    private static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { WriteEveryProperty } }
    };

    // A property left out when null or default would read back as its initial value, not as it was.
    private static void WriteEveryProperty(JsonTypeInfo info)
    {
        foreach (var property in info.Properties)
            property.ShouldSerialize = null;
    }

    /// <summary>The data as JSON, the form a fork copies from.</summary>
    public static string Snapshot(object data) => JsonSerializer.Serialize(data, data.GetType(), Options);

    /// <summary>A branch's own copy of <paramref name="data"/>.</summary>
    public static object Copy(object data) => Copy(Snapshot(data), data.GetType());

    /// <summary>A copy from a snapshot, so a fork serializes its parent's data once.</summary>
    public static object Copy(string snapshot, Type type) => JsonSerializer.Deserialize(snapshot, type, Options)!;

    public static JsonNode Node(object data) =>
        JsonSerializer.SerializeToNode(data, data.GetType(), Options) ?? new JsonObject();

    /// <summary>The changes that turn <paramref name="baseline"/> into <paramref name="branch"/>.</summary>
    public static List<DataChange> Changes(object baseline, object branch) =>
        Changes(Node(baseline), branch, baseline.GetType());

    /// <inheritdoc cref="Changes(object, object)"/>
    public static List<DataChange> Changes(JsonNode baseline, object branch, Type type)
    {
        var changes = new List<DataChange>();
        var info = Info(type);
        Compare(baseline, Node(branch), info, PerMember(info), [], changes);
        return changes;
    }

    /// <summary>A branch's copy rebuilt from the data it was forked from and its saved changes.</summary>
    public static object Rebuild(object baseline, IReadOnlyList<DataChange> changes)
    {
        var node = Node(baseline);
        foreach (var change in changes)
            Lay(node, change);
        return node.Deserialize(baseline.GetType(), Options)!;
    }

    /// <summary>
    /// <paramref name="root"/> with the committed changes of every live branch laid over it, in fork
    /// order: a view of what the branches of the open joins have done so far, for operators and tests.
    /// It is not a merge; branches that changed the same data show the later one.
    /// </summary>
    public static object Overlay(object root, ExecutionCheckpoint checkpoint) =>
        Rebuild(root, checkpoint.Continuations.SelectMany(item => item.DataChanges ?? []).ToList());

    /// <summary>
    /// Merges the branches, given in declaration order, into a copy of <paramref name="baseline"/>, or
    /// throws <see cref="JoinConflictException"/> when branches changed the same data, or a property and
    /// one inside it, without a combiner that merges them, or when the data type cannot hold the merge.
    /// </summary>
    public static MergedData Merge(object baseline, IReadOnlyList<MergingBranch> branches,
        IReadOnlyDictionary<string, Type>? overrides = null)
    {
        var type = baseline.GetType();
        // Each change belongs to a unit: the outermost property on its path with a combiner, or itself.
        var units = new Dictionary<string, (List<string> Path, Type? Combiner, List<int> Branches, DataChange Change)>(
            StringComparer.Ordinal);
        var order = new List<string>();
        var combinerAt = new Dictionary<string, Type?>(StringComparer.Ordinal);
        for (var index = 0; index < branches.Count; index++)
            foreach (var change in branches[index].Changes)
            {
                var (path, combiner) = Unit(type, change.Path, overrides, combinerAt);
                var key = Key(path);
                if (!units.TryGetValue(key, out var unit))
                {
                    units[key] = unit = (path, combiner, [], change);
                    order.Add(key);
                }
                if (!unit.Branches.Contains(index))
                    unit.Branches.Add(index);
            }

        // A unit and one inside it from different branches conflict: each unit checks its prefixes.
        foreach (var key in order)
        {
            var (path, _, changedBy, _) = units[key];
            for (var length = 0; length < path.Count; length++)
                if (units.TryGetValue(Key(path.Take(length).ToList()), out var outer) &&
                    outer.Branches.Concat(changedBy).Distinct().Count() > 1)
                    throw Conflict(outer.Path, $"Parallel branches changed " +
                        $"'{Display(outer.Path)}' and '{Display(path)}', which is inside it");
        }

        var node = Node(baseline);
        var combined = new List<(List<string> Path, Type Combiner, List<int> Branches)>();
        foreach (var key in order)
        {
            var (path, combiner, changedBy, change) = units[key];
            if (combiner != null)
                combined.Add((path, combiner, changedBy));
            else if (changedBy.Count > 1)
                throw Conflict(path, $"Parallel branches changed '{Display(path)}' and it has no combiner");
            else
                Lay(node, change);
        }

        object merged;
        try
        {
            merged = node.Deserialize(type, Options)!;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or WorkflowEngineException))
        {
            // The data type refused the merge, through its converters, required members or setters.
            var at = (error as JsonException)?.Path?.TrimStart('$').TrimStart('.') ?? "";
            throw new JoinConflictException(at, $"The merged data could not be read back as {type.Name}: {error.Message}");
        }
        foreach (var (path, combiner, changedBy) in combined)
        {
            var values = changedBy.Select(index => ValueAt(branches[index].Data(), path)).ToList();
            var value = Combine(combiner, path, ValueAt(baseline, path), values);
            try
            {
                SetAt(merged, path, value);
            }
            catch (Exception error) when (error is not WorkflowEngineException)
            {
                throw Conflict(path, $"The combined value of '{Display(path)}' could not be set: " +
                    $"{(error is TargetInvocationException { InnerException: { } inner } ? inner : error).Message}");
            }
        }
        return new MergedData(merged, order.Select(key => units[key].Path).ToList());
    }

    /// <summary>
    /// Sets every path the merge changed on <paramref name="data"/>, the parent's own object, from the
    /// merged copy: what no branch changed keeps its value and identity.
    /// </summary>
    public static void Apply(object data, MergedData merge)
    {
        foreach (var path in merge.Paths)
        {
            if (path.Count == 0)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    "A branch replaced the whole workflow data, which a join cannot apply in place");
            var target = data;
            var source = merge.Data;
            var info = Info(data.GetType());
            for (var depth = 0; ; depth++)
            {
                var segment = path[depth];
                var last = depth == path.Count - 1;
                if (info.Kind == JsonTypeInfoKind.Dictionary)
                {
                    var key = DictionaryKey(info, segment);
                    var dictionary = (IDictionary)target;
                    var merged = (IDictionary)source;
                    if (!merged.Contains(key))
                    {
                        dictionary.Remove(key);
                        break;
                    }
                    var value = merged[key];
                    if (last || value == null || dictionary[key] is not { } inner)
                    {
                        dictionary[key] = value;
                        break;
                    }
                    (target, source, info) = (inner, value, Info(info.ElementType!));
                }
                else
                {
                    var property = PropertyNamed(info, segment)
                        ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                            $"A join found no property at '{Display(path)}'");
                    var value = property.Get!(source);
                    if (last || value == null || property.Get(target) is not { } inner)
                    {
                        if (property.Set == null)
                            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                                $"A join cannot set '{Display(path)}', which has no setter");
                        property.Set(target, value);
                        break;
                    }
                    (target, source, info) = (inner, value, Info(property.PropertyType));
                }
            }
        }
    }

    // Records the changes between two JSON values of one contract: per member when the contract is merged
    // per member and both are objects, otherwise whole.
    private static void Compare(JsonNode? before, JsonNode? after, JsonTypeInfo info, bool perMember,
        List<string> path, List<DataChange> changes)
    {
        if (perMember && before is JsonObject beforeObject && after is JsonObject afterObject)
        {
            if (info.Kind == JsonTypeInfoKind.Dictionary)
            {
                var element = Info(info.ElementType!);
                foreach (var (key, value) in afterObject)
                {
                    var child = new List<string>(path) { key };
                    if (beforeObject.TryGetPropertyValue(key, out var old))
                        Compare(old, value, element, PerMember(element), child, changes);
                    else
                        changes.Add(new DataChange { Path = child, Value = value?.ToJsonString() ?? "null" });
                }
                foreach (var (key, _) in beforeObject)
                    if (!afterObject.ContainsKey(key))
                        changes.Add(new DataChange { Path = new List<string>(path) { key }, Removed = true });
                return;
            }
            foreach (var property in info.Properties.Where(IsBranchData))
            {
                var member = Info(property.PropertyType);
                Compare(beforeObject[property.Name], afterObject[property.Name], member,
                    property.CustomConverter == null && PerMember(member),
                    new List<string>(path) { property.Name }, changes);
            }
            return;
        }
        if (!JsonNode.DeepEquals(before, after))
            changes.Add(new DataChange { Path = path, Value = after?.ToJsonString() ?? "null" });
    }

    // Lays a change over the JSON of the data it was made against.
    private static void Lay(JsonNode root, DataChange change)
    {
        if (change.Path.Count == 0)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                "A branch replaced the whole workflow data, which a join cannot apply in place");
        var owner = root;
        foreach (var segment in change.Path.Take(change.Path.Count - 1))
            owner = (owner as JsonObject)?[segment] ?? throw Missing(change.Path);
        if (owner is not JsonObject target)
            throw Missing(change.Path);
        if (change.Removed)
            target.Remove(change.Path[^1]);
        else
            target[change.Path[^1]] = JsonNode.Parse(change.Value ?? "null");
    }

    private static JsonTypeInfo Info(Type type) => Options.GetTypeInfo(type);

    /// <summary>
    /// True when a value of this contract is compared and merged per member: a class whose properties are
    /// its whole JSON, or a mutable dictionary class. Anything else is one value.
    /// </summary>
    private static bool PerMember(JsonTypeInfo info) =>
        !info.Type.IsValueType && !info.Type.IsInterface && !info.Type.IsAbstract && info.PolymorphismOptions == null &&
        info.Kind switch
        {
            JsonTypeInfoKind.Object => !info.Properties.Any(property => property.IsExtensionData),
            JsonTypeInfoKind.Dictionary => typeof(IDictionary).IsAssignableFrom(info.Type),
            _ => false
        };

    // A property is branch data when it can be read and either set or merged in place.
    private static bool IsBranchData(JsonPropertyInfo property) =>
        property.Get != null && (property.Set != null || MergedInPlace(property));

    private static bool MergedInPlace(JsonPropertyInfo property) =>
        property.CustomConverter == null && Declared(property) == null && PerMember(Info(property.PropertyType));

    private static Type? Declared(JsonPropertyInfo property) =>
        property.AttributeProvider?.GetCustomAttributes(typeof(CombineAttribute), true)
            .OfType<CombineAttribute>().FirstOrDefault()?.CombinerType;

    private static JsonPropertyInfo? PropertyNamed(JsonTypeInfo info, string jsonName) =>
        info.Properties.FirstOrDefault(property => property.Name == jsonName);

    private static string? ClrName(JsonPropertyInfo property) => (property.AttributeProvider as MemberInfo)?.Name;

    // The unit a change merges in, and its combiner: the outermost property on the path with one, chosen by
    // the step or declared by the data type, or the change's own path.
    private static (List<string> Path, Type? Combiner) Unit(Type type, List<string> path,
        IReadOnlyDictionary<string, Type>? overrides, Dictionary<string, Type?> cache)
    {
        for (var length = 1; length <= path.Count; length++)
        {
            var prefix = path.Take(length).ToList();
            var key = Key(prefix);
            if (!cache.TryGetValue(key, out var combiner))
                cache[key] = combiner = CombinerAt(type, prefix, overrides);
            if (combiner != null)
                return (prefix, combiner);
        }
        return (path, null);
    }

    /// <summary>
    /// The combiner for the property at the JSON <paramref name="path"/> of <paramref name="type"/>: the
    /// parallel step's choice for that path, or the one the property declares, if any.
    /// </summary>
    public static Type? CombinerAt(Type type, IReadOnlyList<string> path,
        IReadOnlyDictionary<string, Type>? overrides = null)
    {
        if (overrides != null && overrides.TryGetValue(Key(path), out var chosen))
            return chosen;
        var info = Info(type);
        JsonPropertyInfo? property = null;
        foreach (var segment in path)
        {
            if (!PerMember(info))
                return null;
            if (info.Kind == JsonTypeInfoKind.Dictionary)
            {
                info = Info(info.ElementType!);
                property = null;
                continue;
            }
            property = PropertyNamed(info, segment);
            if (property == null)
                return null;
            info = Info(property.PropertyType);
        }
        return property == null ? null : Declared(property);
    }

    /// <summary>
    /// The JSON path a join merges a write to the CLR member path <paramref name="clrPath"/> (such as
    /// <c>WorkflowData.Order.Lines[].Total</c>) as: the path to the write, or to the value it is written
    /// into when that value is merged whole. Null when it writes into a dictionary entry, which the join
    /// merges per key, or outside the workflow data. A write to a member the serializer does not see is an
    /// invalid definition: the join would drop it.
    /// </summary>
    public static List<string>? MergePath(Type dataType, string clrPath, Func<string> where)
    {
        const string root = "WorkflowData";
        if (!clrPath.StartsWith(root + ".", StringComparison.Ordinal))
            return null;
        var info = Info(dataType);
        var path = new List<string>();
        foreach (var segment in clrPath[(root.Length + 1)..].Split('.'))
        {
            var indexed = segment.EndsWith("[]", StringComparison.Ordinal);
            var name = segment.Replace("[]", "", StringComparison.Ordinal);
            var property = info.Properties.FirstOrDefault(item => ClrName(item) == name && item.Get != null)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"{where()} writes '{clrPath[(root.Length + 1)..]}', but '{name}' is not serialized with the " +
                    "workflow data, so a join would drop the write");
            path.Add(property.Name);
            var value = Info(property.PropertyType);
            if (property.CustomConverter != null || !PerMember(value))
                return path;
            if (indexed)
                return null;
            info = value;
        }
        return path;
    }

    /// <summary>
    /// Records a parallel step's choice of <paramref name="combiner"/> for the property
    /// <paramref name="property"/> selects, instead of the one the data type declares, if any (spec E-05).
    /// </summary>
    public static void Choose(WorkflowStep step, System.Linq.Expressions.LambdaExpression property, Type dataType,
        Type valueType, Type combiner)
    {
        ArgumentNullException.ThrowIfNull(property);
        var path = MemberPath.Of(property);
        if (path == null || path.Contains("[]", StringComparison.Ordinal))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"A combiner is chosen for a property of the workflow data, reached through members only: {property}");
        step.ParallelCombiners[CombinerKey(dataType, path, valueType, combiner)] = combiner;
    }

    /// <summary>
    /// The key a step's choice of combiner is kept under, for the property at the CLR member path
    /// <paramref name="clrPath"/>, checked to be one the join can merge with <paramref name="combiner"/>.
    /// </summary>
    private static string CombinerKey(Type dataType, string clrPath, Type valueType, Type combiner)
    {
        CombinerContract(combiner);
        var info = Info(dataType);
        var path = new List<string>();
        JsonPropertyInfo? property = null;
        foreach (var name in clrPath.Split('.'))
        {
            if (property != null && (property.CustomConverter != null || !PerMember(info)))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"A combiner cannot be chosen for '{clrPath}': '{string.Join('.', path)}' is merged as one value");
            if (property != null && Declared(property) != null)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"A combiner cannot be chosen for '{clrPath}': '{string.Join('.', path)}' has a combiner, " +
                    "which merges everything inside it");
            property = info.Properties.FirstOrDefault(item => ClrName(item) == name && item.Get != null)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"A combiner cannot be chosen for '{clrPath}': '{name}' is not serialized with the workflow data");
            path.Add(property.Name);
            info = Info(property.PropertyType);
        }
        if (property!.PropertyType != valueType)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"The combiner '{combiner.Name}' merges {valueType.Name} values, but '{clrPath}' is " +
                $"{property.PropertyType.Name}");
        if (property.Set == null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"A combiner cannot be chosen for '{clrPath}', which has no setter for the combined value");
        return Key(path);
    }

    /// <summary>
    /// Checks that every combiner a data type declares fits its property, which can be set (spec E-05),
    /// and, when <paramref name="branches"/>, that parallel branches' copies of it can be merged (spec
    /// E-01): it is merged per member, and a property with no setter is either one value, which is not
    /// branch data, or merged in place and read back into its existing object, by
    /// <c>JsonObjectCreationHandling.Populate</c> or a constructor parameter. A list or another value merged
    /// whole needs a setter, or a branch's copy of it could not be merged.
    /// </summary>
    public static void Validate(Type dataType, bool branches)
    {
        if (branches && !PerMember(Info(dataType)))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"'{dataType.Name}' is serialized as one value, so parallel branches' changes to it cannot be " +
                "merged; use a class whose properties are its JSON");
        Walk(Info(dataType), [], new HashSet<Type>(), (path, owner, property) =>
        {
            var at = string.Join('.', path);
            if (Declared(property) is { } combiner)
            {
                var valueType = CombinerContract(combiner).GetGenericArguments()[0];
                if (valueType != property.PropertyType)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"The combiner '{combiner.Name}' of '{at}' merges {valueType.Name} values, but the property " +
                        $"is {property.PropertyType.Name}");
                if (property.Set == null)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"'{at}' has a combiner but no setter for the combined value");
            }
            if (!branches || property.Set != null || property.Get == null)
                return;
            var value = Info(property.PropertyType);
            if (MergedInPlace(property))
            {
                var populated = (property.ObjectCreationHandling ?? owner.PreferredPropertyObjectCreationHandling) ==
                                JsonObjectCreationHandling.Populate;
                if (!populated && property.AssociatedParameter == null)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"'{at}' has no setter and is not read back into its object, so a parallel branch's copy " +
                        "would lose it; give it a setter, a constructor parameter or " +
                        "[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]");
            }
            else if (!value.Type.IsValueType && value.Kind != JsonTypeInfoKind.None)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"'{at}' has no setter, so a join could not set what a parallel branch made of it; give it a setter");
        });
    }

    /// <summary>The combiners a data type declares where a join uses them, for the fingerprint (spec E-05).</summary>
    public static string CombinerSignature(Type? dataType)
    {
        if (dataType == null)
            return "";
        var found = new List<string>();
        Walk(Info(dataType), [], new HashSet<Type>(), (path, _, property) =>
        {
            if (Declared(property) is { } combiner)
                found.Add(string.Join('.', path) + "=" + WorkflowTypeIdentity.StableName(combiner));
        });
        return string.Join(';', found.Order(StringComparer.Ordinal));
    }

    // Visits every serialized property the join compares, through objects and dictionaries merged per member.
    private static void Walk(JsonTypeInfo info, List<string> path, HashSet<Type> visiting,
        Action<List<string>, JsonTypeInfo, JsonPropertyInfo> visit)
    {
        if (!PerMember(info) || !visiting.Add(info.Type))
            return;
        try
        {
            if (info.Kind == JsonTypeInfoKind.Dictionary)
            {
                Walk(Info(info.ElementType!), path.Count == 0 ? ["[]"] : [.. path[..^1], path[^1] + "[]"], visiting, visit);
                return;
            }
            foreach (var property in info.Properties)
            {
                List<string> at = [.. path, ClrName(property) ?? property.Name];
                visit(at, info, property);
                if (property.CustomConverter == null && Declared(property) == null)
                    Walk(Info(property.PropertyType), at, visiting, visit);
            }
        }
        finally
        {
            visiting.Remove(info.Type);
        }
    }

    // The value at a JSON path of a data object, read through its contract.
    private static object? ValueAt(object data, List<string> path)
    {
        object? current = data;
        var info = Info(data.GetType());
        foreach (var segment in path)
        {
            if (current == null)
                return null;
            if (info.Kind == JsonTypeInfoKind.Dictionary)
            {
                current = ((IDictionary)current)[DictionaryKey(info, segment)];
                info = Info(info.ElementType!);
                continue;
            }
            var property = PropertyNamed(info, segment) ?? throw Missing(path);
            current = property.Get!(current);
            info = Info(property.PropertyType);
        }
        return current;
    }

    // Sets the value at a JSON path of a data object whose owners along the path all exist.
    private static void SetAt(object data, List<string> path, object? value)
    {
        var owner = data;
        var info = Info(data.GetType());
        for (var depth = 0; depth < path.Count; depth++)
        {
            var last = depth == path.Count - 1;
            if (info.Kind == JsonTypeInfoKind.Dictionary)
            {
                var dictionary = (IDictionary)owner;
                var key = DictionaryKey(info, path[depth]);
                if (last)
                    dictionary[key] = value;
                else
                    owner = dictionary[key] ?? throw Missing(path);
                info = Info(info.ElementType!);
                continue;
            }
            var property = PropertyNamed(info, path[depth]) ?? throw Missing(path);
            if (last)
                (property.Set ?? throw Missing(path))(owner, value);
            else
                owner = property.Get!(owner) ?? throw Missing(path);
            info = Info(property.PropertyType);
        }
    }

    // A dictionary key read from its JSON name the way the serializer reads it.
    private static object DictionaryKey(JsonTypeInfo dictionary, string name)
    {
        if (dictionary.KeyType == typeof(string))
            return name;
        var probe = typeof(Dictionary<,>).MakeGenericType(dictionary.KeyType!, typeof(int));
        var read = (IDictionary)JsonSerializer.Deserialize($"{{{JsonSerializer.Serialize(name)}:0}}", probe, Options)!;
        return read.Keys.Cast<object>().Single();
    }

    private static WorkflowEngineException Missing(List<string> path) =>
        new(WorkflowErrorKind.InvalidState, $"A join found nothing at '{Display(path)}' to apply a change to");

    // Runs a combiner through ICombiner<T>, so an explicit implementation works too; anything it throws
    // fails the join for its property.
    private static object? Combine(Type combinerType, List<string> path, object? original, IReadOnlyList<object?> values)
    {
        try
        {
            var contract = CombinerContract(combinerType);
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(contract.GetGenericArguments()[0]))!;
            foreach (var value in values)
                list.Add(value);
            var combiner = Activator.CreateInstance(combinerType)!;
            return contract.GetMethod(nameof(ICombiner<object>.Combine))!.Invoke(combiner, [original, list]);
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

    /// <summary>An unambiguous key for a JSON path, whatever its segments contain.</summary>
    public static string Key(IReadOnlyList<string> path) => JsonSerializer.Serialize(path);

    private static string Display(IReadOnlyList<string> path) =>
        path.Count == 0 ? "(the whole data)" : string.Join('.', path);

    private static JoinConflictException Conflict(List<string> path, string message) => new(Display(path), message);
}

using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// One change a parallel branch made to its copy of the workflow data, against the data it was forked
/// from: a JSON property path and the new value as JSON, or a dictionary entry's removal.
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
/// Branch copies of the workflow data and their merge at a join. The data is
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
    /// <summary>Collects classification using the same contract walk as branch validation.</summary>
    internal static RedactingJson.ClassifiedPath[] ClassifiedPaths(Type type)
    {
        var paths = new List<RedactingJson.ClassifiedPath>();
        Walk(Info(type), [], merged: true, new HashSet<(Type, bool)>(), (path, _, property, _) =>
        {
            if (RedactingJson.Classification(property.AttributeProvider) is { } classification)
                paths.Add(new(string.Join('.', path), classification));
        });
        return paths.ToArray();
    }

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
                // Branches come in order, so a branch already listed is the last one.
                if (unit.Branches.Count == 0 || unit.Branches[^1] != index)
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
        // A combined value is written as its property's contract writes it, with the property's converter
        // and number handling, and laid over the merge, so the read-back below validates it with the rest
        // and no setter sees it beside values it will not be merged with.
        foreach (var (path, combiner, changedBy) in combined)
        {
            var values = changedBy.Select(index => ValueAt(branches[index].Data(), path)).ToList();
            var value = Combine(combiner, path, ValueAt(baseline, path), values);
            string json;
            try
            {
                json = PropertyJson(type, path, value)?.ToJsonString() ?? "null";
            }
            catch (Exception error) when (error is not (OutOfMemoryException or WorkflowEngineException))
            {
                throw Conflict(path, $"The combined value of '{Display(path)}' could not be written: {error.Message}");
            }
            Lay(node, new DataChange { Path = path, Value = json });
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
        return new MergedData(merged, order.Select(key => units[key].Path).ToList());
    }

    /// <summary>
    /// Sets every path the merge changed on <paramref name="data"/>, the parent's own object, from the
    /// merged copy: what no branch changed keeps its value and identity. The object may refuse a value the
    /// read-back took, through a setter that reads state the copy does not have; then every value set,
    /// the refused one included, is put back and the join fails with <see cref="JoinConflictException"/>.
    /// Putting a value back assigns it again, so a setter on branch data must be a plain assignment that
    /// can be undone; one that refuses even that leaves the data changed, which is an engine failure.
    /// </summary>
    public static void Apply(object data, MergedData merge)
    {
        var undo = new List<Action>();
        List<string> path = [];
        try
        {
            // In the order the read-back set them: each object's properties as its contract declares them.
            foreach (var changed in merge.Paths.OrderBy(item => DeclarationOrder(data.GetType(), item), PathOrder.Instance))
            {
                path = changed;
                if (path.Count == 0)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                        "A branch replaced the whole workflow data, which a join cannot apply in place");
                SetFrom(data, merge.Data, path, undo);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var refused = new List<Exception>();
            for (var index = undo.Count - 1; index >= 0; index--)
                try
                {
                    undo[index]();
                }
                catch (Exception restore) when (restore is not OutOfMemoryException)
                {
                    refused.Add(restore is TargetInvocationException { InnerException: { } inner } ? inner : restore);
                }
            if (refused.Count > 0)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    $"A join could not put the workflow data back after '{Display(path)}' was refused: " +
                    string.Join("; ", refused.Select(item => item.Message)), new AggregateException(refused));
            if (error is WorkflowEngineException)
                throw;
            var reason = error is TargetInvocationException { InnerException: { } cause } ? cause : error;
            throw Conflict(path, $"The merge could not be applied to '{Display(path)}': {reason.Message}");
        }
    }

    // The position of each segment of a path among its owner's properties; a dictionary key keeps its place.
    private static List<int> DeclarationOrder(Type type, List<string> path)
    {
        var order = new List<int>();
        var info = Info(type);
        foreach (var segment in path)
        {
            if (info.Kind == JsonTypeInfoKind.Dictionary)
            {
                order.Add(0);
                info = Info(info.ElementType!);
                continue;
            }
            var index = info.Properties.Select(property => property.Name).ToList().IndexOf(segment);
            order.Add(index);
            if (index < 0)
                break;
            info = Info(info.Properties[index].PropertyType);
        }
        return order;
    }

    private sealed class PathOrder : IComparer<List<int>>
    {
        public static readonly PathOrder Instance = new();

        public int Compare(List<int>? one, List<int>? other)
        {
            for (var index = 0; index < Math.Min(one!.Count, other!.Count); index++)
                if (one[index] != other[index])
                    return one[index].CompareTo(other[index]);
            return one.Count.CompareTo(other.Count);
        }
    }

    // Sets one changed path on the parent's object from the merged copy, at the first point where the
    // merged value is new or replaces a whole value. How to put the old value back is recorded first, so
    // a setter that changes its object and then refuses is undone too; a value that did not change is
    // left alone.
    private static void SetFrom(object data, object merged, List<string> path, List<Action> undo)
    {
        var target = data;
        var source = merged;
        var info = Info(data.GetType());
        for (var depth = 0; ; depth++)
        {
            var segment = path[depth];
            var last = depth == path.Count - 1;
            if (info.Kind == JsonTypeInfoKind.Dictionary)
            {
                var key = DictionaryKey(info, segment);
                var dictionary = (IDictionary)target;
                var mergedDictionary = (IDictionary)source;
                var existed = dictionary.Contains(key);
                var old = existed ? dictionary[key] : null;
                var value = mergedDictionary.Contains(key) ? mergedDictionary[key] : null;
                if (!mergedDictionary.Contains(key) || last || value == null || old == null)
                {
                    undo.Add(() =>
                    {
                        if (dictionary.Contains(key) != existed || existed && !Same(dictionary[key], old))
                        {
                            if (existed)
                                dictionary[key] = old;
                            else
                                dictionary.Remove(key);
                        }
                    });
                    if (mergedDictionary.Contains(key))
                        dictionary[key] = value;
                    else
                        dictionary.Remove(key);
                    return;
                }
                (target, source, info) = (old, value, Info(info.ElementType!));
            }
            else
            {
                var property = PropertyNamed(info, segment)
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                        $"A join found no property at '{Display(path)}'");
                var value = property.Get!(source);
                var old = property.Get(target);
                if (last || value == null || old == null)
                {
                    var set = property.Set ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                        $"A join cannot set '{Display(path)}', which has no setter");
                    var owner = target;
                    var get = property.Get;
                    undo.Add(() =>
                    {
                        if (!Same(get(owner), old))
                            set(owner, old);
                    });
                    set(owner, value);
                    return;
                }
                (target, source, info) = (old, value, Info(property.PropertyType));
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

    // True when a value is still the one a restore would put back: the same object, or for a value type or
    // a string, the same data as the serializer writes it. A type's own equality may call different data
    // equal, so it is not asked.
    private static bool Same(object? current, object? old) =>
        ReferenceEquals(current, old) ||
        current is ValueType or string && old != null && current.GetType() == old.GetType() &&
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(current, current.GetType(), Options),
            JsonSerializer.SerializeToNode(old, old.GetType(), Options));

    // A property's value as JSON, as the property's own contract writes it: a contract of its own holding
    // just that property, with the property's converter and number handling and its owner's number
    // handling, so the serializer applies them with its usual precedence and nothing else changes.
    private static JsonNode? PropertyJson(Type type, List<string> path, object? value)
    {
        var owner = Info(type);
        JsonPropertyInfo? property = null;
        foreach (var segment in path)
        {
            if (property != null)
                owner = Info(property.PropertyType);
            if (owner.Kind == JsonTypeInfoKind.Dictionary)
            {
                // A key: the next segment is a property of the dictionary's values.
                owner = Info(owner.ElementType!);
                property = null;
                continue;
            }
            property = PropertyNamed(owner, segment) ?? throw Missing(path);
        }
        if (property == null)
            throw Missing(path);
        var box = JsonTypeInfo.CreateJsonTypeInfo(typeof(PropertyBox), Options);
        box.NumberHandling = owner.NumberHandling;
        var single = box.CreateJsonPropertyInfo(property.PropertyType, property.Name);
        single.Get = _ => value;
        single.CustomConverter = property.CustomConverter;
        single.NumberHandling = property.NumberHandling;
        box.Properties.Add(single);
        return JsonSerializer.SerializeToNode(new PropertyBox(), box)?[property.Name]?.DeepClone();
    }

    // What PropertyJson serializes: an object whose only property it supplies.
    private sealed class PropertyBox;

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
    /// its whole JSON and can each be set or merged in place, or a dictionary class the serializer creates
    /// and fills. Anything else is one value: an object holding state only its constructor sets, an
    /// immutable dictionary, a list, a struct, a polymorphic value.
    /// </summary>
    private static bool PerMember(JsonTypeInfo info) =>
        !info.Type.IsValueType && !info.Type.IsInterface && !info.Type.IsAbstract && info.PolymorphismOptions == null &&
        info.Kind switch
        {
            JsonTypeInfoKind.Object => !info.Properties.Any(property => property.IsExtensionData ||
                                                                         property.Get != null && property.Set == null &&
                                                                         property.AssociatedParameter != null),
            JsonTypeInfoKind.Dictionary => info.CreateObject != null && typeof(IDictionary).IsAssignableFrom(info.Type),
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
    /// merges per key, or outside the workflow data. A write the join would drop is an invalid definition:
    /// one to a member the serializer does not see, anywhere on the path, or into a value merged whole
    /// that has no setter for the join to set.
    /// </summary>
    public static List<string>? MergePath(Type dataType, string clrPath, Func<string> where)
    {
        const string root = "WorkflowData";
        if (!clrPath.StartsWith(root + ".", StringComparison.Ordinal))
            return null;
        var target = clrPath[(root.Length + 1)..];
        var contracts = Contracts(Info(dataType));
        var path = new List<string>();
        List<string>? unit = null;
        var decided = false;
        var segments = target.Split('.');
        for (var position = 0; position < segments.Length; position++)
        {
            var segment = segments[position];
            var indexes = 0;
            while (segment.EndsWith("[]", StringComparison.Ordinal))
            {
                segment = segment[..^2];
                indexes++;
            }
            // A cast in the path is not kept, so every derived type that has the member must serialize it.
            var matches = contracts.SelectMany(contract => contract.Properties
                    .Where(item => ClrName(item) == segment).Select(item => (Contract: contract, Property: item)))
                .ToList();
            if (matches.Count == 0 || matches.Any(match => match.Property.Get == null))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"{where()} writes '{target}', but '{segment}' is not serialized with the workflow data" +
                    (matches.Count > 1 ? " in every type it may be" : "") + ", so a join would drop the write");
            var property = matches[0].Property;
            var inside = position < segments.Length - 1 || indexes > 0;
            // A value the join sets whole, written inside, must have a setter, wherever it is on the path.
            foreach (var (_, match) in matches)
                if (inside && match.Set == null && (match.CustomConverter != null || !PerMember(Info(match.PropertyType))))
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"{where()} writes '{target}', inside '{segment}', which a join sets whole but which has " +
                        "no setter, so a join would drop the write");
            var value = Info(property.PropertyType);
            if (!decided)
            {
                path.Add(property.Name);
                if (property.CustomConverter != null || !PerMember(value))
                    (unit, decided) = (path, true);
                else if (indexes > 0)
                    (unit, decided) = (null, true);
            }
            // The rest of the path is checked too, through list items and polymorphic values; behind a
            // converter of its own the serializer decides, so nothing more can be checked.
            contracts = [];
            foreach (var (_, match) in matches)
            {
                if (match.CustomConverter != null)
                    continue;
                var next = Info(match.PropertyType);
                for (var level = 0; level < indexes && next.ElementType != null; level++)
                    next = Info(next.ElementType);
                if (next.Kind != JsonTypeInfoKind.None)
                    contracts.AddRange(Contracts(next));
            }
            if (contracts.Count == 0)
                break;
        }
        return decided ? unit : path;
    }

    // A contract and, for a polymorphic value, those of its derived types.
    private static List<JsonTypeInfo> Contracts(JsonTypeInfo info) =>
        [info, .. info.PolymorphismOptions?.DerivedTypes.Select(item => Info(item.DerivedType)) ?? []];

    /// <summary>
    /// Records a parallel step's choice of <paramref name="combiner"/> for the property
    /// <paramref name="property"/> selects, instead of the one the data type declares, if any.
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
    /// Checks that every combiner a data type declares fits its property, which can be set,
    /// and, when <paramref name="branches"/>, that parallel branches' copies of the data read back what
    /// they were given and can be merged: the data is merged per member; every combiner sits
    /// where a join uses it, not inside a value merged whole or one with a combiner; and anywhere in the
    /// data, inside lists and polymorphic values too, a property with no setter is either a scalar the
    /// type computes, set by a constructor parameter, or an object read back into itself by
    /// <c>JsonObjectCreationHandling.Populate</c>. A list, dictionary or other object with no setter is
    /// refused: a copy would lose it, or keep what its initializer put in it.
    /// </summary>
    public static void Validate(Type dataType, bool branches)
    {
        _ = RedactingJson.ClassifiedPaths(dataType);
        if (branches && !PerMember(Info(dataType)))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"'{dataType.Name}' is serialized as one value, so parallel branches' changes to it cannot be " +
                "merged; use a class whose properties are its JSON");
        Walk(Info(dataType), [], merged: true, new HashSet<(Type, bool)>(), (path, owner, property, merged) =>
        {
            var at = string.Join('.', path);
            if (Declared(property) is { } combiner)
            {
                var valueType = CombinerContract(combiner).GetGenericArguments()[0];
                if (valueType != property.PropertyType)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"The combiner '{combiner.Name}' of '{at}' merges {valueType.Name} values, but the property " +
                        $"is {property.PropertyType.Name}");
                if (branches && property.Set == null)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"'{at}' has a combiner but no setter for the combined value");
                if (branches && !merged)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                        $"'{at}' has a combiner, but it is inside a value a join merges as one, so the combiner " +
                        "never applies; put it on that value's property");
            }
            if (!branches)
                return;
            var value = Info(property.PropertyType);
            var populated = (property.ObjectCreationHandling ?? owner.PreferredPropertyObjectCreationHandling) ==
                            JsonObjectCreationHandling.Populate;
            // The serializer populates an existing mutable collection; an array, an immutable or frozen one, or
            // a read-only view it replaces, whatever the preference says.
            if (populated && value.Kind is JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary &&
                Populatable(value.Type))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"'{at}' is read back by adding to what its initializer put in it, so a parallel branch's copy " +
                    "would keep items the data no longer has; read it back whole");
            if (property.Set != null || property.AssociatedParameter != null ||
                value.Kind == JsonTypeInfoKind.Object && populated && MergedInPlace(property))
                return;
            // A get-only auto-property holds state, which a copy reads back only through a constructor
            // parameter; a scalar the type computes is not state.
            var stored = property.AttributeProvider is PropertyInfo { DeclaringType: { } declaring } clr &&
                         declaring.GetField($"<{clr.Name}>k__BackingField",
                             BindingFlags.Instance | BindingFlags.NonPublic) != null;
            if (!stored && (value.Type.IsValueType || value.Kind == JsonTypeInfoKind.None))
                return;
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"'{at}' has no setter and no constructor parameter the serializer uses, so a parallel branch's " +
                "copy would lose it or keep what its initializer put in it; give it a setter, or a constructor " +
                "parameter, marking that constructor [JsonConstructor] if the type has others");
        });
    }

    private static bool Populatable(Type type) =>
        !type.IsArray && type.Namespace is not ("System.Collections.Immutable" or "System.Collections.Frozen") &&
        !(type.IsInterface && type.IsGenericType && type.GetGenericTypeDefinition() is var definition &&
          (definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyCollection<>) ||
           definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyDictionary<,>) ||
           definition == typeof(IReadOnlySet<>)));

    /// <summary>The combiners a data type declares, for the structure hash.</summary>
    public static string CombinerSignature(Type? dataType)
    {
        if (dataType == null)
            return "";
        var found = new List<string>();
        Walk(Info(dataType), [], merged: true, new HashSet<(Type, bool)>(), (path, _, property, _) =>
        {
            if (Declared(property) is { } combiner)
                found.Add(string.Join('.', path) + "=" + WorkflowTypeIdentity.StableName(combiner));
        });
        return string.Join(';', found.Distinct().Order(StringComparer.Ordinal));
    }

    // Visits every serialized property a branch copy reads back: through objects and dictionaries, list
    // elements and polymorphic values. merged says whether a join merges at the property itself, or it is
    // inside a value merged whole.
    private static void Walk(JsonTypeInfo info, List<string> path, bool merged, HashSet<(Type, bool)> visiting,
        Action<List<string>, JsonTypeInfo, JsonPropertyInfo, bool> visit)
    {
        if (!visiting.Add((info.Type, merged)))
            return;
        try
        {
            var inside = merged && PerMember(info);
            if (info.PolymorphismOptions is { } polymorphism)
                foreach (var derived in polymorphism.DerivedTypes.Where(item => item.DerivedType != info.Type))
                    Walk(Info(derived.DerivedType), path, merged: false, visiting, visit);
            switch (info.Kind)
            {
                case JsonTypeInfoKind.Dictionary or JsonTypeInfoKind.Enumerable when info.ElementType != null:
                    Walk(Info(info.ElementType), path.Count == 0 ? ["[]"] : [.. path[..^1], path[^1] + "[]"],
                        inside && info.Kind == JsonTypeInfoKind.Dictionary, visiting, visit);
                    break;
                case JsonTypeInfoKind.Object:
                    foreach (var property in info.Properties.Where(item => item.Get != null))
                    {
                        List<string> at = [.. path, ClrName(property) ?? property.Name];
                        visit(at, info, property, inside);
                        if (property.CustomConverter == null)
                            Walk(Info(property.PropertyType), at, inside && Declared(property) == null, visiting, visit);
                    }
                    break;
            }
        }
        finally
        {
            visiting.Remove((info.Type, merged));
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

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IxIFlow.Core;

/// <summary>
/// Merges the values parallel branches gave one property of the workflow data (D-14, spec E-05). It
/// receives the value at the fork and the value of every branch that changed it, in branch declaration
/// order, and must be pure: it may run again after a recovery. A combiner that cannot merge the values
/// throws, and the join fails with <see cref="JoinConflictException"/> for the property.
/// </summary>
public interface ICombiner<T>
{
    T Combine(T original, IReadOnlyList<T> branchValues);
}

/// <summary>Declares how parallel branches' changes to this property are merged (spec E-05).</summary>
[AttributeUsage(AttributeTargets.Property)]
public abstract class CombineAttribute : Attribute
{
    private protected CombineAttribute()
    {
    }

    /// <summary>The combiner type, an <see cref="ICombiner{T}"/> of the property's type.</summary>
    public abstract Type CombinerType { get; }
}

/// <inheritdoc cref="CombineAttribute"/>
public sealed class CombineAttribute<TCombiner> : CombineAttribute
    where TCombiner : new()
{
    public override Type CombinerType => typeof(TCombiner);
}

/// <summary>Takes the value of the first branch, in declaration order, that changed it (spec E-06).</summary>
public sealed class FirstWins<T> : ICombiner<T>
{
    public T Combine(T original, IReadOnlyList<T> branchValues) => branchValues[0];
}

/// <summary>Takes the value of the last branch, in declaration order, that changed it (spec E-06).</summary>
public sealed class LastWins<T> : ICombiner<T>
{
    public T Combine(T original, IReadOnlyList<T> branchValues) => branchValues[^1];
}

/// <summary>Adds every branch's change to the value at the fork (spec E-06).</summary>
public sealed class Sum<T> : ICombiner<T> where T : INumber<T>
{
    public T Combine(T original, IReadOnlyList<T> branchValues) =>
        branchValues.Aggregate(original, (total, value) => total + (value - original));
}

/// <summary>Takes the largest branch value (spec E-06).</summary>
public sealed class Max<T> : ICombiner<T> where T : IComparable<T>
{
    public T Combine(T original, IReadOnlyList<T> branchValues) =>
        branchValues.Aggregate((best, value) => value.CompareTo(best) > 0 ? value : best);
}

/// <summary>Takes the smallest branch value (spec E-06).</summary>
public sealed class Min<T> : ICombiner<T> where T : IComparable<T>
{
    public T Combine(T original, IReadOnlyList<T> branchValues) =>
        branchValues.Aggregate((best, value) => value.CompareTo(best) < 0 ? value : best);
}

/// <summary>
/// Keeps the list at the fork and adds each branch's new items after it, in declaration order (spec
/// E-06). Every branch must have kept the list at the fork as its beginning; items are compared as JSON.
/// </summary>
public sealed class Append<T> : ICombiner<List<T>>
{
    public List<T> Combine(List<T> original, IReadOnlyList<List<T>> branchValues)
    {
        original ??= [];
        var result = new List<T>(original);
        foreach (var branch in branchValues)
        {
            if (branch.Count < original.Count ||
                original.Where((item, index) => !CombinerJson.Same(item, branch[index])).Any())
                throw new InvalidOperationException(
                    "A branch changed or removed items the list had at the fork, so its additions cannot be appended");
            result.AddRange(branch.Skip(original.Count));
        }
        return result;
    }
}

/// <summary>
/// Merges dictionaries per key (spec E-06): a key one branch added, changed or removed takes that
/// branch's change; two branches changing one key differently conflict. Values are compared as JSON.
/// </summary>
public sealed class Union<TKey, TValue> : ICombiner<Dictionary<TKey, TValue>> where TKey : notnull
{
    public Dictionary<TKey, TValue> Combine(Dictionary<TKey, TValue> original,
        IReadOnlyList<Dictionary<TKey, TValue>> branchValues)
    {
        original ??= [];
        var result = new Dictionary<TKey, TValue>(original);
        var decided = new Dictionary<TKey, (bool Removed, JsonNode? Json)>();
        foreach (var branch in branchValues)
        {
            var changes = branch.Where(pair => !original.TryGetValue(pair.Key, out var before) ||
                                               !CombinerJson.Same(before, pair.Value))
                .Select(pair => (pair.Key, Removed: false, Value: pair.Value))
                .Concat(original.Keys.Where(key => !branch.ContainsKey(key))
                    .Select(key => (Key: key, Removed: true, Value: default(TValue)!)));
            foreach (var (key, removed, value) in changes)
            {
                var json = removed ? null : CombinerJson.Of(value);
                if (decided.TryGetValue(key, out var earlier) &&
                    (earlier.Removed != removed || !JsonNode.DeepEquals(earlier.Json, json)))
                    throw new InvalidOperationException($"Two branches changed the key '{key}' differently");
                decided[key] = (removed, json);
                if (removed)
                    result.Remove(key);
                else
                    result[key] = value;
            }
        }
        return result;
    }
}

/// <summary>
/// Merges sets (spec E-06): the set at the fork without what any branch removed, with what any branch
/// added. Items are compared as JSON, because each branch's set is its own copy: an item a branch kept is
/// equal to the one at the fork, not the same object. The result uses the set's own comparer.
/// </summary>
public sealed class Union<T> : ICombiner<HashSet<T>>
{
    public HashSet<T> Combine(HashSet<T> original, IReadOnlyList<HashSet<T>> branchValues)
    {
        original ??= [];
        var at = original.Select(item => (Item: item, Json: CombinerJson.Of(item))).ToList();
        var kept = at.Where(item => branchValues.All(branch => branch.Any(other => CombinerJson.Same(item.Item, other))))
            .Select(item => item.Item);
        var result = new HashSet<T>(kept, original.Comparer);
        var added = new List<JsonNode?>();
        foreach (var branch in branchValues)
            foreach (var item in branch)
            {
                var json = CombinerJson.Of(item);
                if (at.Any(old => JsonNode.DeepEquals(old.Json, json)) ||
                    added.Any(other => JsonNode.DeepEquals(other, json)))
                    continue;
                added.Add(json);
                result.Add(item);
            }
        return result;
    }
}

internal static class CombinerJson
{
    public static JsonNode? Of<T>(T value) => JsonSerializer.SerializeToNode(value);

    /// <summary>True when two values have the same JSON, whatever the order of an object's properties.</summary>
    public static bool Same<T>(T one, T other) => JsonNode.DeepEquals(Of(one), Of(other));
}

/// <summary>
/// Parallel branches changed the same workflow data, or a property and one inside it, without a
/// combiner that merges them (D-14, spec E-04). Raised at the parallel step; the data is left as it was
/// before the step.
/// </summary>
public sealed class JoinConflictException(string path, string message) : Exception(message)
{
    /// <summary>The conflicting property path, such as <c>Order.Total</c>.</summary>
    public string Path { get; } = path;
}

namespace IxIFlow.Core;

/// <summary>
/// A <c>ForEach</c> body's handle on its current item. Mappings, conditions and predicates in the
/// body read the item with <c>ctx.Item(item)</c> and its position with <c>ctx.Index(item)</c>. A handle
/// belongs to its body: using it anywhere else is a build error.
/// </summary>
public abstract class ItemScope
{
    private protected ItemScope(string label, Type itemType)
    {
        Label = label;
        ItemType = itemType;
    }

    /// <summary>A name for diagnostics; never used to find the item.</summary>
    public string Label { get; internal set; }

    public Type ItemType { get; }

    public override string ToString() => $"ForEach item '{Label}' ({ItemType.Name})";
}

/// <inheritdoc cref="ItemScope"/>
public sealed class ItemScope<TItem> : ItemScope
{
    internal ItemScope(string label) : base(label, typeof(TItem))
    {
    }
}

/// <summary>What became of one item of a <c>ForEach</c>.</summary>
public enum ItemStatus
{
    /// <summary>The body ran to its end for this item.</summary>
    Completed,

    /// <summary>The item started and was cancelled when the join decided without it.</summary>
    Cancelled,

    /// <summary>The item never started.</summary>
    NotStarted
}

/// <summary>
/// The result of one item of a <c>ForEach</c>, in item order: its position in the list, what became of
/// it, and the value the body's <c>.Result(...)</c> gave for it.
/// </summary>
public sealed record ItemResult<TResult>(int Index, ItemStatus Status, TResult? Value);

/// <summary>A context that carries the items in force where it was made.</summary>
internal interface IItemContext
{
    ItemValues Items { get; set; }
}

/// <summary>
/// The items in force where a context is made: the index and saved JSON of the item of every
/// <c>ForEach</c> body that encloses it. Items are read-only: every context reads its own copy, made
/// when it first reads the item, so a change to one is seen by no other context and never saved.
/// </summary>
internal sealed class ItemValues
{
    private readonly IReadOnlyDictionary<ItemScope, (int Index, StoredJson Json)> _values;
    private readonly Dictionary<ItemScope, object?> _read = new(ReferenceEqualityComparer.Instance);

    public static ItemValues None { get; } = new(new Dictionary<ItemScope, (int, StoredJson)>());

    public ItemValues(IReadOnlyDictionary<ItemScope, (int Index, StoredJson Json)> values) => _values = values;

    public bool IsEmpty => _values.Count == 0;

    /// <summary>Gives <paramref name="context"/> its own copy of these items, when it is a workflow context, and returns it.</summary>
    public object On(object context)
    {
        if (context is IItemContext items)
            items.Items = IsEmpty ? this : new ItemValues(_values);
        return context;
    }

    public (int Index, object? Value) Of(ItemScope scope)
    {
        if (!_values.TryGetValue(scope, out var saved))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"{scope} is read outside its ForEach body");
        if (!_read.TryGetValue(scope, out var value))
            _read[scope] = value = Runtime.ForEachTransitions.Deserialize(saved.Json, scope.ItemType, "item");
        return (saved.Index, value);
    }
}

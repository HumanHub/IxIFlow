using System.Collections;

namespace IxIFlow.Core;

/// <summary>A collection read, including whether its instance has been removed.</summary>
public sealed class WorkflowReadResult<T>(IReadOnlyList<T> items, bool isRemoved = false) : IReadOnlyList<T>
{
    public bool IsRemoved { get; } = isRemoved;
    public int Count => items.Count;
    public T this[int index] => items[index];
    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

using System.Numerics;

namespace IxIFlow.Core;

/// <summary>
/// Merges the values parallel branches gave one property of the workflow data (D-14, spec E-05). It
/// receives the value at the fork and the value of every branch that changed it, in branch declaration
/// order, and must be pure: it may run again after a recovery.
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

/// <summary>Adds every branch's change to the value at the fork (spec E-06).</summary>
public sealed class Sum<T> : ICombiner<T> where T : INumber<T>
{
    public T Combine(T original, IReadOnlyList<T> branchValues) =>
        branchValues.Aggregate(original, (total, value) => total + (value - original));
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

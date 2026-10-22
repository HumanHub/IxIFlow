using System.Linq.Expressions;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>One typed write to a serialized location in workflow data.</summary>
public sealed record Assignment(LambdaExpression Target, LambdaExpression Value, Type ValueType)
{
    /// <summary>The structural CLR path; computed indices are represented by [].</summary>
    public string? TargetPath => MemberPath.Of(Target);
    internal Action<object>? Apply { get; set; }
    internal Delegate? TargetBehavior { get; set; }
    internal Delegate? ValueBehavior { get; set; }
}

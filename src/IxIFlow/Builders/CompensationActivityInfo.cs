using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Information about compensation activities
/// </summary>
public class CompensationActivityInfo
{
    internal Core.Runtime.WorkflowStepDelegates Runtime { get; set; } = null!;

    public Type ActivityType { get; set; } = null!;
    public Type? PreviousChainActivityType { get; set; }

    /// <summary>The answer type a request's compensation reads, checked against the request's (D-36).</summary>
    public Type? ReplyType { get; set; }
    public List<PropertyMapping> InputMappings { get; set; } = [];
    public List<PropertyMapping> OutputMappings { get; set; } = [];
}
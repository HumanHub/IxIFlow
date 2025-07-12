using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Information about compensation activities
/// </summary>
public class CompensationActivityInfo
{
    public Type ActivityType { get; set; } = null!;
    public Type? PreviousChainActivityType { get; set; }
    public List<PropertyMapping> InputMappings { get; set; } = [];
    public List<PropertyMapping> OutputMappings { get; set; } = [];
}
using Microsoft.Extensions.Compliance.Classification;

namespace IxIFlow.Core;

/// <summary>Marks a member for erasure in inspection, or for the application's configured redactor.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class SensitiveAttribute() : DataClassificationAttribute(Classification)
{
    /// <summary>The classification to register with an application's redactor provider.</summary>
    public new static DataClassification Classification { get; } = new("IxIFlow", "Sensitive");
}

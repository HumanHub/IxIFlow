namespace IxIFlow.ActivitySdk;

/// <summary>Declares the stable catalog identity and optional presentation metadata of an activity.</summary>
/// <param name="key">The activity's unique catalog key, used by authored workflow documents.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WorkflowActivityAttribute(string key) : Attribute
{
    /// <summary>The stable activity catalog key.</summary>
    public string Key { get; } = key;

    /// <summary>The activity contract version; defaults to 1.0.</summary>
    public string Version { get; set; } = "1.0";

    /// <summary>An optional display name for authoring tools.</summary>
    public string? Name { get; set; }

    /// <summary>An optional category for grouping activities in authoring tools.</summary>
    public string? Category { get; set; }

    /// <summary>An optional icon identifier for authoring tools.</summary>
    public string? Icon { get; set; }

    /// <summary>An optional designer identifier for authoring tools.</summary>
    public string? Designer { get; set; }
}
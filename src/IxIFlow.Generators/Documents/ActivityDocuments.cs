using System.ComponentModel;

namespace IxIFlow.Dsl.Documents;

[Description("A standalone C# activity document.")]
public sealed record ActivityDocument : DocumentNode
{
    [Description("The document contract version.")]
    [DocumentValues("1.0")]
    public string SchemaVersion { get; init; } = "1.0";
    [Description("The activity identity and display metadata.")]
    [DocumentRequired]
    public ActivityDefinitionDocument Activity { get; init; } = new();
    [Description("Package imports in id@version form.")]
    public List<string> Packages { get; init; } = [];
    [Description("C# namespaces imported by the activity.")]
    public List<string> Usings { get; init; } = [];
    [Description("Input properties keyed by C# property name.")]
    public Dictionary<string, ActivityMemberDocument> Input { get; init; } = new();
    [Description("Output properties keyed by C# property name.")]
    public Dictionary<string, ActivityMemberDocument> Output { get; init; } = new();
    [Description("Properties used as both inputs and outputs.")]
    public Dictionary<string, ActivityMemberDocument> InOut { get; init; } = new();
    [Description("C# ExecuteAsync statements, or OpenAsync statements returning the scope value, authored as a literal block.")]
    [DocumentRequired]
    public string Body { get; init; } = string.Empty;
    [Description("Optional C# class declarations, authored as a literal block.")]
    public string? Declarations { get; init; }
}

[Description("The identity and descriptive metadata of an activity.")]
public sealed record ActivityDefinitionDocument : DocumentNode
{
    [Description("The lowercase activity key and package identifier.")]
    [DocumentRequired]
    public string Key { get; init; } = string.Empty;
    [Description("The integer activity version, from 1 through 65534.")]
    [DocumentRequired]
    public int Version { get; init; }
    [Description("The activity display name.")]
    public string? Name { get; init; }
    [Description("The activity category.")]
    public string? Category { get; init; }
    [Description("The lowercase icon name.")]
    public string? Icon { get; init; }
    [Description("The lowercase designer name.")]
    public string? Designer { get; init; }
    [Description("The activity description.")]
    public string? Description { get; init; }
    [Description("The C# scope value type; when supplied, body implements OpenAsync and declarations must implement CloseAsync.")]
    public string? Scope { get; init; }
    [Description("Scope value types required by this activity, including enclosing requirements of a scope activity.")]
    public List<string> RequiresScopes { get; init; } = [];
    [Description("Scope concurrency; omission uses serialized. Only scope activities can declare this member.")]
    [DocumentValues("serialized", "shared", DiagnosticCode = "ACTD017")]
    public string? Concurrency { get; init; }
    [Description("Minimum scope threading; omission uses anyThread. Only scope activities can declare this member.")]
    [DocumentValues("anyThread", "threadAffine", DiagnosticCode = "ACTD017")]
    public string? Threading { get; init; }
}

[Description("A typed activity property and its editor metadata.")]
public sealed record ActivityMemberDocument : DocumentNode
{
    private object? _default;
    [Description("The C# property type.")]
    [DocumentRequired]
    public string Type { get; init; } = string.Empty;
    [Description("Whether this input requires a value.")]
    public bool Required { get; init; }
    [Description("The scalar input default, including an explicitly supplied null.")]
    public object? Default
    {
        get => _default;
        init
        {
            _default = value;
            var present = Members.ToDictionary(pair => pair.Key, pair => pair.Value);
            present["default"] = Location;
            Members = present;
        }
    }
    [Description("The property display label.")]
    public string? Label { get; init; }
    [Description("The property help text.")]
    public string? Help { get; init; }
    [Description("The editor control used for this property.")]
    [DocumentValues("text", "code", "binding", "connection", DiagnosticCode = "ACTD007")]
    public string? Control { get; init; }

    /// <summary>Whether a default was supplied, including an explicit null.</summary>
    public bool HasDefault() => Members.ContainsKey("default");
}

using System.Text.Json;
using System.Text.RegularExpressions;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>Portable workflow presentation. Execution and member contracts are separate identities.</summary>
public sealed record WorkflowDescriptor(string DisplayName, string? Description, IReadOnlyList<string> Tags,
    string? Icon, string? Category, string DataType, string DataTypeName)
{
    public static WorkflowDescriptor Of(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkflowCatalogRules.ValidateName(definition.Name);
        var type = WorkflowDefinitionValidator.RequireDataType(definition);
        var descriptor = new WorkflowDescriptor(definition.DisplayName ?? definition.Name, definition.Description,
            Array.AsReadOnly((definition.Tags ?? throw Invalid("Workflow tags are required")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            definition.Icon, definition.Category, WorkflowTypeIdentity.SavedName(type), WorkflowTypeIdentity.CSharpName(type));
        Validate(descriptor);
        return descriptor;
    }

    public static void Validate(WorkflowDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.DisplayName) || descriptor.DisplayName.Length > 200 || descriptor.Description?.Length > 4000 ||
            descriptor.Tags == null || descriptor.Category?.Length > 100)
            throw Invalid("Workflow presentation exceeds its bounds");
        if (descriptor.Icon != null && (descriptor.Icon.Length > 100 || !Regex.IsMatch(descriptor.Icon, @"\A[a-z][a-z0-9-]*\z", RegexOptions.CultureInvariant)))
            throw Invalid("WF007: Workflow icon must be a lowercase name token");
        if (descriptor.Category != null && string.IsNullOrWhiteSpace(descriptor.Category))
            throw Invalid("WF008: Workflow category cannot be blank");
        foreach (var tag in descriptor.Tags)
            if (tag == null || !Regex.IsMatch(tag, @"\A[\w.-]{1,50}\z", RegexOptions.CultureInvariant))
                throw Invalid("Workflow tags must contain 1 to 50 word, dot or hyphen characters");
        if (!descriptor.Tags.SequenceEqual(descriptor.Tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) || TagProjection(descriptor).Length > 400)
            throw Invalid("Workflow tags must be unique, sorted and fit within 400 characters");
        foreach (var value in new[] { descriptor.DisplayName, descriptor.Description, descriptor.Icon, descriptor.Category }.OfType<string>())
            WorkflowCatalogRules.ValidateUtf16(value);
        if (!WorkflowTypeIdentity.IsSavedName(descriptor.DataType) || WorkflowTypeIdentity.CSharpName(descriptor.DataType) != descriptor.DataTypeName)
            throw Invalid("Workflow descriptor requires matching saved and C# data type names");
    }

    public string ToJson() { Validate(this); return JsonSerializer.Serialize(this); }
    public static WorkflowDescriptor Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var names = new[] { "DisplayName", "Description", "Tags", "Icon", "Category", "DataType", "DataTypeName" };
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)))
            throw Invalid("Invalid workflow descriptor JSON shape");
        var value = JsonSerializer.Deserialize<WorkflowDescriptor>(json) ?? throw Invalid("Workflow descriptor is required");
        Validate(value);
        return value with { Tags = Array.AsReadOnly(value.Tags.ToArray()) };
    }

    internal static string TagProjection(WorkflowDescriptor descriptor) => " " + string.Join(" ", descriptor.Tags) + " ";
    public bool Equals(WorkflowDescriptor? other) => ReferenceEquals(this, other) || other != null &&
        DisplayName == other.DisplayName && Description == other.Description && Icon == other.Icon && Category == other.Category &&
        DataType == other.DataType && DataTypeName == other.DataTypeName && Tags.SequenceEqual(other.Tags);
    public override int GetHashCode() => HashCode.Combine(DisplayName, Description, Icon, Category, DataType, DataTypeName, string.Join(" ", Tags));
    private static WorkflowEngineException Invalid(string message) => new(WorkflowErrorKind.InvalidDefinition, message);
}

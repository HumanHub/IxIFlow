using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>An immutable workflow input and output contract.</summary>
public sealed class WorkflowContract
{
    /// <summary>Creates a contract with members ordered by ordinal name.</summary>
    public WorkflowContract(IReadOnlyList<ContractMember> Inputs,
        IReadOnlyList<ContractMember> Outputs, bool Declared)
    {
        this.Inputs = Members(Inputs);
        this.Outputs = Members(Outputs);
        this.Declared = Declared;
        if (this.Outputs.Any(output => output.Required && !this.Inputs.Any(input => input.Name == output.Name)))
            throw new ArgumentException("A required contract member cannot be output-only");
        foreach (var member in this.Inputs.Join(this.Outputs, member => member.Name, member => member.Name, (input, output) => (input, output)))
            if (member.input != member.output) throw new ArgumentException("Input and output descriptors for a member must agree");
    }

    public IReadOnlyList<ContractMember> Inputs { get; }
    public IReadOnlyList<ContractMember> Outputs { get; }
    public bool Declared { get; }

    /// <summary>Canonical, compact JSON; its UTF-8 bytes define the contract hash.</summary>
    public string ToJson() => JsonSerializer.Serialize(new { Inputs, Outputs, Declared });

    /// <summary>The uppercase SHA-256 digest of the canonical JSON, as used for structure hashes.</summary>
    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));

    /// <summary>Reads a portable descriptor without resolving types in the current process.</summary>
    public static WorkflowContract Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Shape(root, ["Inputs", "Outputs", "Declared"]);
        return new(Read(root.GetProperty("Inputs")), Read(root.GetProperty("Outputs")), root.GetProperty("Declared").GetBoolean());

        static ContractMember[] Read(JsonElement array) => array.EnumerateArray().Select(item =>
        {
            Shape(item, ["Name", "Type", "Required", "HasDefault", "DefaultJson", "DisplayName", "Description", "UIHint"]);
            return new ContractMember(item.GetProperty("Name").GetString()!, item.GetProperty("Type").GetString()!,
                item.GetProperty("Required").GetBoolean(), item.GetProperty("HasDefault").GetBoolean(),
                item.GetProperty("DefaultJson").GetString(), item.GetProperty("DisplayName").GetString(),
                item.GetProperty("Description").GetString(), item.GetProperty("UIHint").GetString());
        }).ToArray();
    }

    private static void Shape(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(names.Order(StringComparer.Ordinal)))
            throw new JsonException("Invalid workflow contract members");
    }

    private static IReadOnlyList<ContractMember> Members(IReadOnlyList<ContractMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (member == null || string.IsNullOrWhiteSpace(member.Name) || !names.Add(member.Name) ||
                !WorkflowTypeIdentity.IsSavedName(member.Type) || member.HasDefault != (member.DefaultJson != null))
                throw new ArgumentException("Invalid workflow contract member");
            if (member.HasDefault) using (JsonDocument.Parse(member.DefaultJson!)) { }
        }
        return Array.AsReadOnly(members.OrderBy(member => member.Name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Derives the type's markers, or public setter/getter convention when there are no markers.</summary>
    public static WorkflowContract OfType(Type type) => Derive(type, null, null);

    internal static bool Marked(PropertyInfo property) => property.IsDefined(typeof(WorkflowInputAttribute)) ||
        property.IsDefined(typeof(WorkflowOutputAttribute)) || property.IsDefined(typeof(WorkflowInputOutputAttribute));
    private static bool Input(PropertyInfo property) => property.IsDefined(typeof(WorkflowInputAttribute)) || property.IsDefined(typeof(WorkflowInputOutputAttribute));
    private static bool Output(PropertyInfo property) => property.IsDefined(typeof(WorkflowOutputAttribute)) || property.IsDefined(typeof(WorkflowInputOutputAttribute));

    internal static WorkflowContract Derive(Type type, IReadOnlyList<string>? inputs, IReadOnlyList<string>? outputs)
    {
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            return DeriveInvariant(type, inputs, outputs);
        }
        finally { CultureInfo.CurrentUICulture = uiCulture; }
    }

    private static WorkflowContract DeriveInvariant(Type type, IReadOnlyList<string>? inputs, IReadOnlyList<string>? outputs)
    {
        ArgumentNullException.ThrowIfNull(type);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var marked = properties.Any(Marked);
        var declared = marked || inputs != null || outputs != null;
        properties = properties.GroupBy(property => property.Name, StringComparer.Ordinal).Select(group =>
        {
            var candidates = group.OrderBy(property => DeclarationDistance(type, property.DeclaringType)).ToArray();
            if (candidates.Count(property => property.GetMethod is { IsPublic: true } || property.SetMethod is { IsPublic: true }) > 1 &&
                candidates.Any(property => IsInput(property) || IsOutput(property)))
                throw Refused(type, group.Key, "hides a base property and cannot be declared");
            return candidates[0];
        }).ToArray();
        CheckList(inputs, true);
        CheckList(outputs, false);
        var ins = new List<ContractMember>();
        var outs = new List<ContractMember>();
        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length != 0)
            {
                if (Marked(property) || inputs?.Contains(property.Name) == true || outputs?.Contains(property.Name) == true)
                    throw Refused(type, property.Name, "is an indexer and cannot be mapped");
                continue;
            }
            var input = IsInput(property);
            var output = IsOutput(property);
            if (Input(property) && property.SetMethod is not { IsPublic: true })
                throw Refused(type, property.Name, "is marked as an input but has no public setter");
            if (Output(property) && property.GetMethod is not { IsPublic: true })
                throw Refused(type, property.Name, "is marked as an output but has no public getter");
            var required = property.IsDefined(typeof(RequiredAttribute));
            if (required && output && !input) throw Refused(type, property.Name, "has [Required] but is output-only");
            var hasDefault = property.GetCustomAttribute<DefaultValueAttribute>() != null;
            string? defaultJson = null;
            if (hasDefault)
            {
                if (property.SetMethod is not { IsPublic: true }) throw Refused(type, property.Name, "has [DefaultValue] but has no public setter");
                defaultJson = WorkflowJson.Serialize(ActivityContract.Default(property, type, "Workflow"), property.PropertyType);
            }
            var display = property.GetCustomAttribute<DisplayAttribute>();
            var member = new ContractMember(property.Name, WorkflowTypeIdentity.SavedName(property.PropertyType),
                required, hasDefault, defaultJson, display?.GetName(), display?.GetDescription(), property.GetCustomAttribute<UIHintAttribute>()?.UIHint);
            if (input) ins.Add(member);
            if (output) outs.Add(member);
        }
        return new(ins, outs, declared);

        bool IsInput(PropertyInfo property) => inputs is { Count: 0 } ? false : declared
            ? Input(property) || inputs?.Contains(property.Name) == true
            : property.GetIndexParameters().Length == 0 && property.SetMethod is { IsPublic: true };
        bool IsOutput(PropertyInfo property) => outputs is { Count: 0 } ? false : declared
            ? Output(property) || outputs?.Contains(property.Name) == true
            : property.GetIndexParameters().Length == 0 && property.GetMethod is { IsPublic: true };
        void CheckList(IReadOnlyList<string>? list, bool input)
        {
            foreach (var name in list ?? [])
            {
                var property = properties.FirstOrDefault(property => property.Name == name)
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, $"Workflow '{type.Name}' has no public property '{name}' to declare");
                if (input ? property.SetMethod is not { IsPublic: true } : property.GetMethod is not { IsPublic: true })
                    throw Refused(type, name, input ? "is declared as an input but has no public setter" : "is declared as an output but has no public getter");
                if (Marked(property) && !(input ? Input(property) : Output(property)))
                    throw Refused(type, name, input ? "is declared as an input but is marked output-only" : "is declared as an output but is marked input-only");
            }
        }
    }

    // Hidden public properties can share a name with a different type. Use the declaration nearest the data type.
    internal static PropertyInfo? FindProperty(Type type, string name) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name == name)
            .OrderBy(property => DeclarationDistance(type, property.DeclaringType)).FirstOrDefault();

    private static int DeclarationDistance(Type type, Type? declaringType)
    {
        var distance = 0;
        for (var current = type; current != null && current != declaringType; current = current.BaseType) distance++;
        return distance;
    }

    internal void ValidateType(Type type)
    {
        foreach (var (members, input) in new[] { (Inputs, true), (Outputs, false) })
            foreach (var member in members)
            {
                var property = FindProperty(type, member.Name)
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, $"Child workflow '{type.Name}' has no public property '{member.Name}' to map");
                if (property.GetIndexParameters().Length != 0) throw Refused(type, member.Name, "is an indexer and cannot be mapped");
                if (input ? property.SetMethod is not { IsPublic: true } : property.GetMethod is not { IsPublic: true })
                    throw Refused(type, member.Name, input ? "is not an input: it has no public setter" : "is not an output: it has no public getter");
                if (member.Type != WorkflowTypeIdentity.SavedName(property.PropertyType)) throw Refused(type, member.Name, "has a different contract type identity");
                if (member.HasDefault)
                    try { _ = WorkflowJson.Deserialize(member.DefaultJson!, property.PropertyType); }
                    catch (Exception error) when (error is JsonException or NotSupportedException)
                    { throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, $"Workflow property '{type.Name}.{member.Name}' has a default that cannot be converted to {property.PropertyType.Name}", error); }
            }
    }

    internal void ValidateRequired(object? data, Type type, IReadOnlyDictionary<string, object?>? assigned = null)
    {
        if (data != null && !type.IsInstanceOfType(data) &&
            WorkflowTypeIdentity.SavedName(data.GetType()) == WorkflowTypeIdentity.SavedName(type))
            type = data.GetType();
        var missing = Inputs.Where(member => member.Required).Where(member =>
        {
            var property = FindProperty(type, member.Name) ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Child workflow input '{type.Name}.{member.Name}' is not a property of its data");
            var value = data == null ? null : property.GetMethod != null ? property.GetValue(data) : assigned?.GetValueOrDefault(member.Name);
            return ActivityContract.IsEmpty(value);
        }).Select(member => $"{type.Name}.{member.Name}").ToArray();
        if (missing.Length != 0) throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
            $"InvalidInput: workflow input(s) '{string.Join("', '", missing)}' are required and cannot be null or an empty string");
    }

    private static WorkflowEngineException Refused(Type type, string member, string reason) =>
        new(WorkflowErrorKind.InvalidDefinition, $"Workflow property '{type.Name}.{member}' {reason}");
}

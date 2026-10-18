using System.Collections.Concurrent;
using System.Reflection;

namespace IxIFlow.Dsl.Documents;

/// <summary>Declares the discriminator and optional inference member of a document variant.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DocumentKindAttribute(string kind) : Attribute
{
    public string Kind { get; } = kind;
    public string? ImpliedBy { get; set; }
}

/// <summary>Marks a member that must be supplied in a document.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DocumentRequiredAttribute : Attribute;

/// <summary>Declares the closed set of accepted string values of a document member.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DocumentValuesAttribute(params string[] values) : Attribute
{
    public IReadOnlyList<string> Values { get; } = values;
    public string DiagnosticCode { get; set; } = "CHOICE001";
}

/// <summary>A key a document node accepts, with the member it reads into when there is one.</summary>
/// <param name="IsDiscriminator">The key is the variant's <c>kind</c>, written as the declared kind and checked against it.</param>
public readonly record struct DocumentKey(string Name, PropertyInfo? Member, bool IsDiscriminator);

/// <summary>Provides the shared member ordering, names and variant resolution of the document contract.</summary>
public static class DocumentModel
{
    /// <summary>The root key that binds a document to its editor schema; readers accept and ignore it.</summary>
    public const string SchemaKey = "$schema";

    private static readonly ConcurrentDictionary<Type, IReadOnlyList<PropertyInfo>> Members = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<Type>> Variants = new();
    private static readonly ConcurrentDictionary<Type, DocumentKindAttribute?> Kinds = new();
    private static readonly ConcurrentDictionary<(Type, string), IReadOnlyList<string>> Values = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<DocumentKey>> Keys = new();

    /// <summary>The serialized members of a document type, base members first, then in declaration order.</summary>
    public static IReadOnlyList<PropertyInfo> GetMembers(Type type) => Members.GetOrAdd(type, static type =>
        type == typeof(DocumentNode) || type == typeof(object) ? [] :
        [.. type.BaseType is { } parent ? GetMembers(parent) : [],
         .. type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(member => member.MetadataToken)]);

    /// <summary>The keys a node of this type accepts, in document order: a variant's kind, the root's schema binding, then the members.</summary>
    public static IReadOnlyList<DocumentKey> GetKeys(Type type) => Keys.GetOrAdd(type, static type =>
    {
        var members = GetMembers(type);
        var discriminator = GetKind(type) == null ? null : (DocumentKey?)new DocumentKey("kind", members.FirstOrDefault(member => GetName(member) == "kind"), true);
        return
        [
            .. discriminator is { } kind ? [kind] : Array.Empty<DocumentKey>(),
            .. type == typeof(WorkflowDocument) ? [new DocumentKey(SchemaKey, null, false)] : Array.Empty<DocumentKey>(),
            .. members.Where(member => member != discriminator?.Member).Select(member => new DocumentKey(GetName(member), member, false))
        ];
    });

    public static string GetName(MemberInfo member) => CamelCase(member.Name);
    public static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name.Substring(1);
    public static DocumentKindAttribute? GetKind(Type type) => Kinds.GetOrAdd(type, static type => type.GetCustomAttribute<DocumentKindAttribute>());

    /// <summary>The closed set of values a member accepts, or an empty list when the member is open.</summary>
    public static IReadOnlyList<string> GetValues(Type type, string property) => Values.GetOrAdd((type, property), static key =>
        GetMembers(key.Item1).First(member => member.Name == key.Item2).GetCustomAttribute<DocumentValuesAttribute>()?.Values ?? []);

    /// <summary>The concrete kinds of an abstract document type, in declaration order.</summary>
    public static IReadOnlyList<Type> GetVariants(Type type) => Variants.GetOrAdd(type, static type => type.Assembly.GetTypes()
        .Where(candidate => !candidate.IsAbstract && type.IsAssignableFrom(candidate) && GetKind(candidate) != null)
        .OrderBy(candidate => candidate.MetadataToken).ToArray());

    /// <summary>The variant named by <paramref name="kind"/>, or the one implied by a present member when the kind is absent.</summary>
    public static Type? ResolveVariant(Type type, string? kind, Func<string, bool> hasMember) =>
        GetVariants(type).FirstOrDefault(candidate =>
        {
            var contract = GetKind(candidate)!;
            return hasMember("kind") ? contract.Kind == kind : contract.ImpliedBy is { } member && hasMember(member);
        });
    public static DocumentLocation GetLocation(WorkflowDocument document, string? path)
    {
        object? current = document;
        var location = document.Location;
        foreach (System.Text.RegularExpressions.Match segment in System.Text.RegularExpressions.Regex.Matches(path ?? "", @"([^\.\[\]]+)|\[(\d+)\]"))
        {
            if (current is DocumentNode node)
            {
                var member = segment.Value;
                if (document.Members.Count > 0 && !node.Members.ContainsKey(member)) return node.Location;
                location = node.Members.TryGetValue(member, out var found) ? found : node.Location;
                current = node.GetType().GetProperties().FirstOrDefault(property =>
                    string.Equals(property.Name, member, StringComparison.OrdinalIgnoreCase))?.GetValue(node);
            }
            else if (current is System.Collections.IDictionary dictionary) current = dictionary[segment.Value];
            else if (current is System.Collections.IList list && int.TryParse(segment.Groups[2].Value, out var index) && index < list.Count)
                current = list[index];
            else break;
            if (current is DocumentNode child) location = child.Location;
        }
        return location;
    }

}

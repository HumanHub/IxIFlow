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
}

/// <summary>Provides the shared member ordering, names and variant resolution of the document contract.</summary>
public static class DocumentModel
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<PropertyInfo>> Members = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<Type>> Variants = new();
    private static readonly ConcurrentDictionary<Type, DocumentKindAttribute?> Kinds = new();

    /// <summary>The serialized members of a document type, base members first, then in declaration order.</summary>
    public static IReadOnlyList<PropertyInfo> GetMembers(Type type) => Members.GetOrAdd(type, static type =>
        type == typeof(DocumentNode) || type == typeof(object) ? [] :
        [.. type.BaseType is { } parent ? GetMembers(parent) : [],
         .. type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(member => member.MetadataToken)]);

    public static string GetName(MemberInfo member) => CamelCase(member.Name);
    public static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name.Substring(1);
    public static DocumentKindAttribute? GetKind(Type type) => Kinds.GetOrAdd(type, static type => type.GetCustomAttribute<DocumentKindAttribute>());

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
}

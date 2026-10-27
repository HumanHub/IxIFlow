using Microsoft.CodeAnalysis;

namespace IxIFlow.Dsl.Compilation;

public sealed partial record ActivityDocumentContract
{
    /// <summary>Reads the ordinary or scope activity contract without loading its assembly.</summary>
    public static ActivityDocumentContract? FromSymbol(INamedTypeSymbol type, string key, string version)
    {
        var ordinary = type.AllInterfaces.Any(item => item.ToDisplayString() == "IxIFlow.Core.IAsyncActivity");
        var scopes = type.AllInterfaces.Where(item => item.OriginalDefinition.ToDisplayString() == "IxIFlow.Core.IScopeActivity<TValue>").ToArray();
        if (ordinary == (scopes.Length > 0) || scopes.Length > 1) return null;
        var attributes = new List<AttributeData>();
        for (var current = type; current != null; current = current.BaseType) attributes.AddRange(current.GetAttributes());
        string Mode(string attribute, string first, string second)
        {
            var value = attributes.FirstOrDefault(item => item.AttributeClass?.ToDisplayString() == "IxIFlow.Core." + attribute)
                ?.ConstructorArguments.FirstOrDefault().Value;
            return value == null || value is 0 ? first : value is 1 ? second : "invalid";
        }
        return new(key, version)
        {
            Scope = scopes.Length == 0 ? null : Name(scopes[0].TypeArguments[0]),
            RequiresScopes = attributes.Where(item => item.AttributeClass?.OriginalDefinition.ToDisplayString() == "IxIFlow.Core.RequiresScopeAttribute<TValue>")
                .Select(item => Name(item.AttributeClass!.TypeArguments[0])).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            Concurrency = scopes.Length == 0 ? null : Mode("ScopeConcurrencyAttribute", "serialized", "shared"),
            Threading = scopes.Length == 0 ? null : Mode("ScopeThreadingAttribute", "anyThread", "threadAffine")
        };
    }

    private static string Name(ITypeSymbol type) => type.ToDisplayString(new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers));

    // Value snapshots preserve incremental-generator caching without retaining compilation symbols.
    internal string Snapshot() => string.Join("\u001f", Version, Scope ?? "", string.Join("\u001e", RequiresScopes), Concurrency ?? "", Threading ?? "");
    internal static ActivityDocumentContract ReadSnapshot(string key, string snapshot)
    {
        var fields = snapshot.Split('\u001f');
        return new(key, fields[0])
        {
            Scope = fields[1].Length == 0 ? null : fields[1],
            RequiresScopes = fields[2].Split(new[] { '\u001e' }, StringSplitOptions.RemoveEmptyEntries),
            Concurrency = fields[3].Length == 0 ? null : fields[3],
            Threading = fields[4].Length == 0 ? null : fields[4]
        };
    }
}

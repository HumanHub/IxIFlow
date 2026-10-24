using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.Core;
using Microsoft.CodeAnalysis;

namespace IxIFlow.Authoring;

internal sealed record PackageScopeClaim(string Assembly, string MetadataName, string PackageId, string PackageVersion,
    string Key, string Version, string? ValueType, ScopeConcurrency Concurrency, ScopeThreading Threading, string[] Requirements);

/// <summary>Checks package reference declarations against the implementations used by the build host.</summary>
internal static class ScopePackageContracts
{
    private static string SourceName(ITypeSymbol type) => PackageDescriptorAgreement.SourceName(type);

    public static PackageScopeClaim Describe(INamedTypeSymbol type, string packageId, string packageVersion, string key, string version)
    {
        var ordinary = type.AllInterfaces.Any(item => item.ToDisplayString() == "IxIFlow.Core.IAsyncActivity");
        var scopes = type.AllInterfaces.Where(item => item.OriginalDefinition.ToDisplayString() == "IxIFlow.Core.IScopeActivity<TValue>").ToArray();
        if (type.IsAbstract || type.DeclaredAccessibility != Accessibility.Public || type.IsGenericType ||
            (!ordinary && scopes.Length != 1) || ordinary && scopes.Length != 0)
            throw new InvalidOperationException($"Package activity '{key}' requires exactly one ordinary or scope implementation");
        var declarations = new List<AttributeData>();
        for (var current = type; current != null; current = current.BaseType) declarations.AddRange(current.GetAttributes());
        int Mode(string name)
        {
            var attribute = declarations.FirstOrDefault(item => item.AttributeClass?.ToDisplayString() == "IxIFlow.Core." + name);
            if (attribute == null) return 0;
            if (scopes.Length == 0 || attribute.ConstructorArguments.FirstOrDefault().Value is not int mode || mode is < 0 or > 1)
                throw new InvalidOperationException($"Package activity '{key}' has an invalid scope mode");
            return mode;
        }
        var names = new Stack<string>();
        for (var current = type; current != null; current = current.ContainingType) names.Push(current.MetadataName);
        var metadata = (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") + string.Join("+", names);
        return new(type.ContainingAssembly.Identity.Name, metadata, packageId, packageVersion, key, version,
            scopes.Length == 0 ? null : SourceName(scopes[0].TypeArguments[0]),
            (ScopeConcurrency)Mode("ScopeConcurrencyAttribute"), (ScopeThreading)Mode("ScopeThreadingAttribute"),
            declarations.Where(item => item.AttributeClass?.OriginalDefinition.ToDisplayString() == "IxIFlow.Core.RequiresScopeAttribute<TValue>")
                .Select(item => SourceName(item.AttributeClass!.TypeArguments[0])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    public static void ValidateRuntime(AssemblyLoadContext context, IReadOnlyList<PackageScopeClaim> claims)
    {
        foreach (var claim in claims)
        {
            var assembly = context.LoadFromAssemblyName(new AssemblyName(claim.Assembly));
            var type = assembly.GetType(claim.MetadataName) ?? throw new InvalidOperationException($"Package activity '{claim.Key}' has no runtime implementation");
            var descriptor = ActivityDescriptor.OfType(type, claim.PackageId, claim.PackageVersion);
            if (descriptor.Key != claim.Key || descriptor.Version != claim.Version || descriptor.Scope?.ValueTypeName != claim.ValueType ||
                descriptor.Scope is { } scope && (scope.Concurrency != claim.Concurrency || scope.Threading != claim.Threading) ||
                !descriptor.RequiresScopes.Select(item => item.TypeName).Order(StringComparer.Ordinal).SequenceEqual(claim.Requirements))
                throw new InvalidOperationException($"Package activity '{claim.Key}' scope contract disagrees between reference and runtime assemblies");
        }
    }
}

using IxIFlow.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Authoring;

internal static class PackageDescriptorAgreement
{
    internal static string SourceName(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Dynamic)
            return "global::System.Object";

        if (type is IArrayTypeSymbol array)
            return SourceName(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]";

        if (type is not INamedTypeSymbol named)
            throw new WorkflowPublishException("PUB014", "A portable source type must be closed", new(1, 1));

        named = named.TupleUnderlyingType ?? named;
        var chain = new Stack<INamedTypeSymbol>();

        for (var current = named; current != null; current = current.ContainingType)
            chain.Push(current);

        var names = new List<string>();

        foreach (var part in chain)
            names.Add(Escape(part.Name) + (part.Arity == 0 ? "" : "<" + string.Join(", ", part.TypeArguments.Select(SourceName)) + ">"));

        var spaces = new Stack<string>();

        for (var space = named.ContainingNamespace; !space.IsGlobalNamespace; space = space.ContainingNamespace)
            spaces.Push(Escape(space.Name));

        return "global::" + (spaces.Count == 0 ? "" : string.Join(".", spaces) + ".") + string.Join(".", names);

        static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
    }

    internal static void Validate(ResolvedWorkflowPackages packages, WorkflowCompilation.ReferenceSet references)
    {
        // Bind implementation metadata separately so Roslyn resolves type forwarders on
        // both sides before comparing assembly-aware structural member identities.
        var runtimeReferences = references.Metadata.Where(reference =>
                !packages.RuntimeAssemblies.ContainsKey(System.Reflection.AssemblyName.GetAssemblyName(reference.FilePath!).Name!))
            .Concat(packages.RuntimeAssemblies.Values.Select(bytes => MetadataReference.CreateFromImage(bytes)));

        var runtimeCompilation = CSharpCompilation.Create("PackageRuntimeAgreement", references: runtimeReferences);

        var runtimeAssemblies = runtimeCompilation.References.Select(reference => runtimeCompilation.GetAssemblyOrModuleSymbol(reference))
            .OfType<IAssemblySymbol>().ToDictionary(assembly => assembly.Identity.Name, StringComparer.OrdinalIgnoreCase);

        var matched = new HashSet<(string PackageId, string MetadataName)>();

        foreach (var assembly in references.Assemblies)
        {
            if (!packages.Owners.ContainsKey(assembly.Identity.Name))
                continue;

            if (!packages.RuntimeAssemblies.TryGetValue(assembly.Identity.Name, out var image))
                continue;

            var identity = ResolvedWorkflowPackages.AssemblyIdentity(image);

            if (identity.Version != assembly.Identity.Version ||
                !(identity.GetPublicKeyToken() ?? []).SequenceEqual(assembly.Identity.PublicKeyToken))
                Refused("Package reference and runtime assembly identities disagree");

            var attributed = Types(assembly.GlobalNamespace).Where(type => Has(type, "WorkflowActivityAttribute")).ToArray();

            var descriptors = packages.Activities.Values.SelectMany(items => items)
                .Where(item => packages.ActivityAssemblies[item.Key] == assembly.Identity.Name).ToArray();

            foreach (var type in attributed)
            {
                var descriptor = descriptors.SingleOrDefault(item => item.MetadataTypeName == MetadataName(type));

                if (descriptor == null)
                {
                    Refused("Package activity reference has no runtime descriptor");

                    continue;
                }

                matched.Add((descriptor.PackageId, descriptor.MetadataTypeName));

                var attribute = type.GetAttributes().Single(item =>
                    item.AttributeClass?.ToDisplayString() == "IxIFlow.ActivitySdk.WorkflowActivityAttribute");

                var key = attribute.ConstructorArguments[0].Value as string;
                var version = attribute.NamedArguments.FirstOrDefault(item => item.Key == "Version").Value.Value as string ?? "1.0";

                if (key != descriptor.Key || version != descriptor.Version || SourceName(type) != descriptor.CSharpTypeName)
                    Refused("Package activity reference identity disagrees with its runtime descriptor");

                var properties = Properties(type).ToArray();
                var runtimeType = runtimeAssemblies[assembly.Identity.Name].GetTypeByMetadataName(MetadataName(type));

                if (runtimeType == null)
                {
                    Refused("Package activity reference has no runtime type");

                    continue;
                }

                var runtimeProperties = Properties(runtimeType).ToDictionary(property => property.Name, StringComparer.Ordinal);

                var marked = properties.Any(property =>
                    Has(property, "ActivityInputAttribute") || Has(property, "ActivityOutputAttribute") ||
                    Has(property, "ActivityInputOutputAttribute"));

                foreach (var (members, input) in new[] { (descriptor.Inputs, true), (descriptor.Outputs, false) })
                {
                    var selected = properties.Where(property => marked
                        ? Has(property, input ? "ActivityInputAttribute" : "ActivityOutputAttribute") ||
                        Has(property, "ActivityInputOutputAttribute")
                        : (input ? property.SetMethod : property.GetMethod)?.DeclaredAccessibility == Accessibility.Public).ToArray();

                    if (selected.Length != members.Count)
                        Refused("Package reference and runtime activity member directions disagree");

                    foreach (var property in selected)
                    {
                        var member = members.SingleOrDefault(item => item.Name == property.Name);

                        if (member == null || member.TypeName != SourceName(property.Type) ||
                            member.Required != Has(property, "RequiredAttribute") ||
                            !runtimeProperties.TryGetValue(property.Name, out var runtimeProperty) ||
                            TypeIdentity(property.Type, runtimeAssemblies) != TypeIdentity(runtimeProperty.Type, runtimeAssemblies) ||
                            member.HasDefault != Has(property, "DefaultValueAttribute") ||
                            (input ? property.SetMethod : property.GetMethod)?.DeclaredAccessibility != Accessibility.Public)
                            Refused("Package reference and runtime activity members disagree");
                    }
                }
            }
        }

        foreach (var descriptor in packages.Activities.Values.SelectMany(items => items))
            if (!matched.Contains((descriptor.PackageId, descriptor.MetadataTypeName)))
                Refused("Runtime activity is absent from its reference declarations");
    }

    private static string TypeIdentity(ITypeSymbol type, IReadOnlyDictionary<string, IAssemblySymbol> runtimeAssemblies)
    {
        if (type.TypeKind == TypeKind.Dynamic || type.SpecialType == SpecialType.System_Object)
            return "object";

        if (type is IArrayTypeSymbol array)
            return (array.IsSZArray ? "V" : "A" + array.Rank + ":") + TypeIdentity(array.ElementType, runtimeAssemblies);

        if (type is not INamedTypeSymbol { TypeKind: not TypeKind.Error } named)
            throw new WorkflowPublishException("PUB014", "Package activity member type cannot be resolved", new(1, 1));

        named = named.TupleUnderlyingType ?? named;
        var chain = new Stack<INamedTypeSymbol>();

        for (var current = named; current != null; current = current.ContainingType)
            chain.Push(current);

        var arguments = chain.SelectMany(part => part.TypeArguments).Select(argument => TypeIdentity(argument, runtimeAssemblies))
            .ToArray();

        var definition = runtimeAssemblies.GetValueOrDefault(named.ContainingAssembly.Identity.Name)
            ?.ResolveForwardedType(MetadataName(named)) ?? named;

        var assembly = definition.ContainingAssembly.Identity.ToString();
        var name = MetadataName(definition);

        return "N" + assembly.Length + ":" + assembly + name.Length + ":" + name + arguments.Length + ":" + string.Concat(arguments);
    }

    internal static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol scope)
    {
        foreach (var member in scope.GetMembers())
            if (member is INamedTypeSymbol type)
            {
                yield return type;

                foreach (var child in Types(type))
                    yield return child;
            }
            else if (member is INamespaceSymbol space)
                foreach (var child in Types(space))
                    yield return child;
    }

    private static IEnumerable<IPropertySymbol> Properties(INamedTypeSymbol type)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var current = type; current != null; current = current.BaseType)
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>().Where(item =>
                         !item.IsStatic && !item.IsIndexer && item.DeclaredAccessibility == Accessibility.Public))
                if (names.Add(property.Name))
                    yield return property;
    }

    private static bool Has(ISymbol symbol, string name)
    {
        for (ISymbol? current = symbol; current != null; current = (current as IPropertySymbol)?.OverriddenProperty)
            foreach (var attribute in current.GetAttributes())
                for (var type = attribute.AttributeClass; type != null; type = type.BaseType)
                    if (type.Name == name && type.ContainingNamespace.ToDisplayString() is
                            "IxIFlow.ActivitySdk" or "System.ComponentModel" or "System.ComponentModel.DataAnnotations")
                        return true;

        return false;
    }

    private static string MetadataName(INamedTypeSymbol type)
    {
        var names = new Stack<string>();

        for (var current = type; current != null; current = current.ContainingType)
            names.Push(current.MetadataName);

        return (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") +
            string.Join("+", names);
    }

    private static void Refused(string message) => throw new WorkflowPublishException("PUB014", message, new(1, 1));
}
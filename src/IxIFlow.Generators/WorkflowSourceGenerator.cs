using System.Reflection.Metadata;
using System.Collections.Immutable;
using System.Text;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using YamlDotNet.Core;

namespace IxIFlow.Generators;

/// <summary>Generates fluent definitions from workflow additional files.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class WorkflowSourceGenerator : IIncrementalGenerator
{
    private const string ActivityAttribute = "IxIFlow.ActivitySdk.WorkflowActivityAttribute";
    private static readonly DiagnosticDescriptor InvalidDocument = new(
        "IXIYAML001", "Invalid workflow YAML", "{0}", "Authoring", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Where(input => input.Right.GetOptions(input.Left).TryGetValue(
                "build_metadata.AdditionalFiles.IxIFlowWorkflow", out var enabled) &&
                string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
            .Select((input, token) => new Input(input.Left.Path, input.Left.GetText(token)?.ToString() ?? ""))
            .WithTrackingName("YamlInputs");
        var requests = files.Collect().Select((inputs, _) => new TypeRequests(Lines(inputs.SelectMany(Names))));
        var sourceAliases = context.SyntaxProvider.ForAttributeWithMetadataName(ActivityAttribute,
            static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax,
            static (input, _) => Lines(input.Attributes.Select(attribute =>
                attribute.ConstructorArguments[0].Value + "\t" + MetadataName((INamedTypeSymbol)input.TargetSymbol))))
            .Collect().Select((aliases, _) => Lines(aliases));
        var sourceNames = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax,
            static (input, token) => input.SemanticModel.GetDeclaredSymbol(input.Node, token) is INamedTypeSymbol type
                ? type.Name + "\t" + MetadataName(type) : "")
            .Collect().Select((names, _) => Lines(names));
        var referenceAliases = context.MetadataReferencesProvider
            .Select((reference, token) => ReferencedAliases(reference, token))
            .Collect().Select((aliases, _) => Lines(aliases));
        var aliases = sourceAliases.Combine(sourceNames).Combine(referenceAliases)
            .Select((input, _) => Lines([input.Left.Left, input.Left.Right, input.Right]));
        var lookup = requests.Combine(aliases)
            .Select((input, _) => new Lookup(input.Left.Names, input.Right));
        // Only metadata-name lookups depend on the compilation. Binding snapshots contain values,
        // so alias resolution and member maps are cached across unrelated C# edits.
        var bound = context.CompilationProvider.Combine(lookup)
            .Select((input, token) => Bind(input.Left, input.Right, token))
            .WithTrackingName("YamlTypeLookup");
        var types = bound.Select((input, _) => Resolve(input)).WithTrackingName("YamlTypes");
        var assemblyName = context.CompilationProvider.Select((compilation, _) => compilation.AssemblyName ?? "Workflows");
        var generatedNamespace = context.AnalyzerConfigOptionsProvider.Combine(assemblyName)
            .Select((input, _) => (input.Left.GlobalOptions.TryGetValue("build_property.RootNamespace", out var name) &&
                !string.IsNullOrWhiteSpace(name) ? name : input.Right) + ".Generated");
        context.RegisterSourceOutput(files.Collect().Combine(types).Combine(generatedNamespace).WithTrackingName("YamlGeneration"),
            (production, input) => Generate(production, input.Left.Left, input.Left.Right, input.Right));
    }

    private static string Lines(IEnumerable<string> values) => string.Join("\n", values
        .SelectMany(value => value.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));

    private static IEnumerable<string> Names(Input input)
    {
        try { return WorkflowSourceTranslator.TypeNames(WorkflowYamlSerializer.Read(input.Text))
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray(); }
        catch (Exception error) when (error is YamlException or InvalidOperationException or ArgumentException)
        { return []; }
    }

    private static BoundTypes Bind(Compilation compilation, Lookup lookup, CancellationToken token)
    {
        var aliases = lookup.Aliases.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t')).ToLookup(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        var rows = new List<string>();
        foreach (var name in lookup.Names.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            token.ThrowIfCancellationRequested();
            foreach (var type in new[] { name }.Concat(aliases[name]).Distinct(StringComparer.Ordinal)
                         .SelectMany(metadataName => compilation.GetTypesByMetadataName(metadataName)))
            {
                var properties = new List<string>();
                for (var current = type; current != null; current = current.BaseType)
                    properties.AddRange(current.GetMembers().OfType<IPropertySymbol>()
                        .Where(property => property.DeclaredAccessibility == Accessibility.Public).Select(property => property.Name));
                var attribute = type.GetAttributes().FirstOrDefault(item => item.AttributeClass?.ToDisplayString() == ActivityAttribute);
                var version = attribute?.NamedArguments.FirstOrDefault(pair => pair.Key == "Version").Value.Value as string ??
                    (attribute == null ? "" : "1.0");
                var dataTypes = type.AllInterfaces.Where(contract => contract.OriginalDefinition.ToDisplayString() == "IxIFlow.Builders.Interfaces.IWorkflow<TWorkflowData>")
                    .Select(contract => contract.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Distinct().ToArray();
                rows.Add(string.Join("\t", name, type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), version,
                    string.Join(";", properties.Distinct(StringComparer.Ordinal)), dataTypes.Length == 1 ? dataTypes[0] : ""));
            }
        }
        return new BoundTypes(Lines(rows));
    }

    private static TypeModel Resolve(BoundTypes bound)
    {
        var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var members = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var workflowDataTypes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var versions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in bound.Rows.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(line => line.Split('\t')).GroupBy(row => row[0], StringComparer.Ordinal))
        {
            var matches = group.ToArray();
            if (matches.Length != 1) continue;
            var row = matches[0];
            names.Add(row[0], row[1]);
            if (row[4].Length > 0) workflowDataTypes.Add(row[0], row[4]);
            if (row[2].Length != 0) versions.Add(row[0], row[2]);
            foreach (var property in row[3].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var key = row[0] + "." + char.ToLowerInvariant(property[0]) + property.Substring(1);
                if (!members.ContainsKey(key)) members.Add(key, property);
            }
        }
        return new TypeModel(Lines(names.Select(pair => pair.Key + "\t" + pair.Value)),
            Lines(members.Select(pair => pair.Key + "\t" + pair.Value)),
            Lines(versions.Select(pair => pair.Key + "\t" + pair.Value)),
            Lines(workflowDataTypes.Select(pair => pair.Key + "\t" + pair.Value)));
    }

    private static string ReferencedAliases(MetadataReference reference, CancellationToken token)
    {
        if (reference is not PortableExecutableReference portable) return "";
        var modules = portable.GetMetadata() switch
        {
            AssemblyMetadata metadata => metadata.GetModules(),
            ModuleMetadata metadata => ImmutableArray.Create(metadata),
            _ => []
        };
        var aliases = new List<string>();
        foreach (var module in modules)
        {
            token.ThrowIfCancellationRequested();
            var reader = module.GetMetadataReader();
            if (!reader.AssemblyReferences.Any(handle => reader.GetString(reader.GetAssemblyReference(handle).Name)
                    is "IxIFlow" or "IxIFlow.Abstractions")) continue;
            foreach (var handle in reader.CustomAttributes)
            {
                var attribute = reader.GetCustomAttribute(handle);
                if (attribute.Parent.Kind != HandleKind.TypeDefinition ||
                    attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                var attributeType = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                if (reader.GetString(attributeType.Name) != "WorkflowActivityAttribute" ||
                    reader.GetString(attributeType.Namespace) != "IxIFlow.ActivitySdk") continue;
                var value = reader.GetBlobReader(attribute.Value);
                if (value.ReadUInt16() != 1 || value.ReadSerializedString() is not { } key) continue;
                aliases.Add(key + "\t" + MetadataName(reader, (TypeDefinitionHandle)attribute.Parent));
            }
        }
        return Lines(aliases);
    }

    private static string MetadataName(INamedTypeSymbol type) => type.ContainingType != null
        ? MetadataName(type.ContainingType) + "+" + type.MetadataName
        : type.ContainingNamespace.IsGlobalNamespace ? type.MetadataName : type.ContainingNamespace + "." + type.MetadataName;

    private static string MetadataName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        return type.GetDeclaringType() is { IsNil: false } parent
            ? MetadataName(reader, parent) + "+" + name
            : reader.GetString(type.Namespace) is { Length: > 0 } space ? space + "." + name : name;
    }

    private static void Generate(SourceProductionContext context, ImmutableArray<Input> files, TypeModel types, string generatedNamespace)
    {
        var options = new WorkflowSourceOptions { Namespace = generatedNamespace, RequireResolvedTypes = true };
        Copy(types.Names, options.Types);
        Copy(types.Members, options.Members);
        foreach (var line in types.Versions.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = line.Split('\t');
            options.Activities.Add(entry[0], new ActivityDocumentContract(entry[0], entry[1]));
        }
        Copy(types.WorkflowDataTypes, options.WorkflowDataTypes);
        var documents = new Dictionary<Input, WorkflowDocument>();
        var classNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
            try
            {
                var document = WorkflowYamlSerializer.Read(file.Text);
                var workflow = document.Workflow;
                var key = workflow.Name + "@" + workflow.Version;
                if (options.Workflows.ContainsKey(key))
                    throw new WorkflowSourceException($"Duplicate workflow '{key}'", file.Path, workflow.Location.Line, workflow.Location.Column);
                options.Workflows.Add(key, new WorkflowSourceReference(workflow.DataType, workflow.Version));
                documents.Add(file, document);
            }
            catch (WorkflowSourceException error) { Report(context, file, error); }
            catch (YamlException error)
            { Report(context, file, new WorkflowSourceException(error.Message, file.Path, (int)error.Start.Line, (int)error.Start.Column)); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            { Report(context, file, new WorkflowSourceException(error.Message, file.Path, 1, 1)); }
        foreach (var pair in documents)
        {
            var file = pair.Key;
            context.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                var className = WorkflowSourceTranslator.ClassName(file.Path);
                if (!classNames.Add(className))
                    throw new WorkflowSourceException($"Duplicate generated class '{className}'; use distinct YAML filenames", file.Path, 1, 1);
                var validation = new WorkflowDocumentValidator().ValidateAsync(pair.Value,
                    new WorkflowSourceContracts(options), context.CancellationToken).GetAwaiter().GetResult();
                if (!validation.IsValid)
                {
                    foreach (var diagnostic in validation.Diagnostics.Where(item => item.Severity == WorkflowDiagnosticSeverity.Error))
                    {
                        var location = DocumentModel.GetLocation(pair.Value, diagnostic.Path);
                        Report(context, file, new WorkflowSourceException(diagnostic.Message, file.Path, location.Line, location.Column));
                    }
                    continue;
                }
                var source = WorkflowSourceTranslator.Translate(pair.Value, file.Path, options);
                context.AddSource(className + ".g.cs", SourceText.From(source, Encoding.UTF8));
            }
            catch (WorkflowSourceException error) { Report(context, file, error); }
        }
    }

    private static void Copy(string rows, IDictionary<string, string> target)
    {
        foreach (var line in rows.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        { var entry = line.Split('\t'); target.Add(entry[0], entry[1]); }
    }

    private static void Report(SourceProductionContext context, Input file, WorkflowSourceException error)
    {
        var text = SourceText.From(file.Text);
        var line = Math.Max(0, Math.Min(error.Line - 1, text.Lines.Count - 1));
        var column = Math.Max(0, Math.Min(error.Column - 1, text.Lines[line].Span.Length));
        var position = text.Lines[line].Start + column;
        context.ReportDiagnostic(Diagnostic.Create(InvalidDocument,
            Location.Create(file.Path, new TextSpan(position, 0),
                new LinePositionSpan(new LinePosition(line, column), new LinePosition(line, column))), error.Detail));
    }

    private sealed record Input(string Path, string Text);
    private sealed record TypeRequests(string Names);
    private sealed record Lookup(string Names, string Aliases);
    private sealed record BoundTypes(string Rows);
    private sealed record TypeModel(string Names, string Members, string Versions, string WorkflowDataTypes);
}

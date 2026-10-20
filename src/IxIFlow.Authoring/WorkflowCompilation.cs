using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

namespace IxIFlow.Authoring;

public enum WorkflowPublishStage { Validate, Translate, Compile, Test, Publish }
public sealed record WorkflowPublishDiagnostic(WorkflowPublishStage Stage, string Code, string Message,
    WorkflowDiagnosticSeverity Severity, string Path, int Line, int Column);
public sealed record WorkflowCompilationResult(byte[] Assembly, byte[] Pdb,
    IReadOnlyList<WorkflowPublishDiagnostic> Diagnostics)
{
    public bool Success => Diagnostics.All(item => item.Severity != WorkflowDiagnosticSeverity.Error);
}

/// <summary>The product YAML compilation path, also used by document execution tests.</summary>
public static class WorkflowCompilation
{
    public static WorkflowCompilerSettings Settings { get; } = new(
        typeof(CSharpCompilation).Assembly.GetName().Version!.ToString(), "CSharp14", "Release", true, "PortablePdb");

    // TPA is the host's fixed runtime/dependency profile, independent of lazy loading.
    // Extend it only with the entry point's transitive closure, loaded by name.
    // SourceOptions types must resolve within this fixed profile, even on a warm host.
    public static string[] ReferencePaths(WorkflowSourceOptions? options = null)
    {
        var paths = new HashSet<string>(((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<Assembly>();
        if (Assembly.GetEntryAssembly() is { } entry) pending.Enqueue(entry);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var assembly))
        {
            if (!visited.Add(assembly.FullName!)) continue;
            if (!assembly.IsDynamic && assembly.Location.Length > 0) paths.Add(assembly.Location);
            foreach (var reference in assembly.GetReferencedAssemblies().OrderBy(item => item.FullName, StringComparer.Ordinal))
                pending.Enqueue(Assembly.Load(reference));
        }
        var references = NormalizeReferences(paths);
        if (options != null && ConfiguredTypeNames(options).Any())
            ValidateConfiguredTypes(options, new ReferenceSet(references));
        return references;
    }

    internal static ReferenceSet CreateReferences(WorkflowSourceOptions options)
    {
        var references = new ReferenceSet(ReferencePaths());
        ValidateConfiguredTypes(options, references);
        return references;
    }

    private static IEnumerable<string> ConfiguredTypeNames(WorkflowSourceOptions options) =>
        options.Types.Values.Concat(options.WorkflowDataTypes.Values)
            .Concat(options.Workflows.Values.Select(item => item.DataType)).Distinct(StringComparer.Ordinal);

    private static void ValidateConfiguredTypes(WorkflowSourceOptions options, ReferenceSet references)
    {
        foreach (var configured in ConfiguredTypeNames(options))
        {
            var type = SyntaxFactory.ParseTypeName(configured);
            // A qualified name is one component, not a type for each namespace segment.
            // Descending also visits generic arguments and array/nullable/tuple element types.
            foreach (var name in type.DescendantNodesAndSelf().OfType<NameSyntax>()
                         .Where(name => name.Parent is not (QualifiedNameSyntax or AliasQualifiedNameSyntax)))
            {
                // These contextual keywords parse as identifiers but need no named type lookup.
                if (name is IdentifierNameSyntax identifier && identifier.Identifier.Text is "dynamic" or "nint" or "nuint") continue;
                var candidates = MetadataNames(name, options.Namespace).ToArray();
                if (!references.Assemblies.Any(assembly => candidates.Any(candidate => WorkflowPublisher.FindType(assembly, candidate) != null)))
                    throw new InvalidOperationException($"Configured type '{configured}' has component '{name}' outside the host reference profile (trusted-platform assemblies and entry assembly closure)");
            }
        }
    }

    private static string MetadataName(NameSyntax name) => name switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText + "`" + generic.Arity,
        QualifiedNameSyntax qualified => MetadataName(qualified.Left) + "." + MetadataName(qualified.Right),
        AliasQualifiedNameSyntax alias => MetadataName(alias.Name),
        _ => throw new InvalidOperationException($"Unsupported C# name '{name}'")
    };

    private static IEnumerable<string> MetadataNames(NameSyntax name, string generatedNamespace)
    {
        var metadataName = MetadataName(name);
        yield return metadataName;
        var first = name;
        while (first is QualifiedNameSyntax qualified) first = qualified.Left;
        if (first is AliasQualifiedNameSyntax { Alias.Identifier.ValueText: "global" }) yield break;

        // Unqualified names also see the generated namespace and the translator's usings.
        for (var scope = generatedNamespace; scope.Length > 0;)
        {
            yield return scope + "." + metadataName;
            var dot = scope.LastIndexOf('.');
            scope = dot < 0 ? "" : scope[..dot];
        }
        foreach (var scope in WorkflowSourceTranslator.ImportedNamespaces)
            yield return scope + "." + metadataName;
    }

    // Owned by one publish; the same metadata objects feed validation, resolution and compilation.
    internal sealed class ReferenceSet(string[] paths)
    {
        public string[] Paths { get; } = paths;
        public PortableExecutableReference[] Metadata { get; } = paths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        private IAssemblySymbol[]? _assemblies;

        public IAssemblySymbol[] Assemblies
        {
            get
            {
                if (_assemblies != null) return _assemblies;
                var compilation = CSharpCompilation.Create("WorkflowReferences", references: Metadata);
                return _assemblies = compilation.References.Select(reference => compilation.GetAssemblyOrModuleSymbol(reference))
                    .OfType<IAssemblySymbol>().ToArray();
            }
        }
    }

    internal static string[] NormalizeReferences(IEnumerable<string> paths) => paths
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)
        .Select(path => (Path: path, Name: AssemblyName.GetAssemblyName(path)))
        .GroupBy(item => (item.Name.Name!.ToUpperInvariant(),
            Culture: item.Name.CultureName?.ToUpperInvariant() ?? "",
            Token: Convert.ToHexString(item.Name.GetPublicKeyToken() ?? [])))
        .Select(group =>
        {
            var versions = group.Select(item => item.Name.Version).Distinct().ToArray();
            if (versions.Length > 1)
                throw new InvalidOperationException($"Conflicting reference versions for assembly '{group.First().Name.Name}': {string.Join(", ", versions.Select(item => item?.ToString() ?? "unspecified"))}");
            return group.First().Path;
        }).Order(StringComparer.Ordinal).ToArray();

    public static WorkflowCompilationResult Compile(string source, IReadOnlyList<string>? references = null,
        string sourcePath = "workflow.yaml", CancellationToken cancellationToken = default) =>
        Compile(source, new ReferenceSet(NormalizeReferences(references ?? ReferencePaths())), sourcePath, cancellationToken);

    internal static WorkflowCompilationResult Compile(string source, ReferenceSet references,
        string sourcePath, CancellationToken cancellationToken)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14),
            "workflow.g.cs", Encoding.UTF8, cancellationToken);
        var compilation = CSharpCompilation.Create("Workflow_" + ArtifactDigest.Of(Encoding.UTF8.GetBytes(source)).Value[7..],
            [tree], references.Metadata,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                deterministic: true, nullableContextOptions: NullableContextOptions.Enable));
        using var assembly = new MemoryStream();
        using var pdb = new MemoryStream();
        var emitted = compilation.Emit(assembly, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb),
            cancellationToken: cancellationToken);
        var diagnostics = emitted.Diagnostics.Where(item => item.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(item =>
            {
                var span = item.Location.GetMappedLineSpan();
                var mapped = span.IsValid && span.Path != "workflow.g.cs";
                return new WorkflowPublishDiagnostic(WorkflowPublishStage.Compile, item.Id, item.GetMessage(),
                    item.Severity == DiagnosticSeverity.Error ? WorkflowDiagnosticSeverity.Error : WorkflowDiagnosticSeverity.Warning,
                    mapped ? span.Path : sourcePath, mapped ? span.StartLinePosition.Line + 1 : 1,
                    mapped ? span.StartLinePosition.Character + 1 : 1);
            }).ToArray();
        return new(assembly.ToArray(), pdb.ToArray(), diagnostics);
    }

    public static async Task<WorkflowDefinition> BuildAsync(WorkflowDocument document, WorkflowSourceOptions options)
    {
        var validation = await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(options));
        if (!validation.IsValid)
        {
            var error = validation.Diagnostics.First(item => item.Severity == WorkflowDiagnosticSeverity.Error);
            var location = DocumentModel.GetLocation(document, error.Path);
            throw new WorkflowSourceException(error.Message, "test.yaml", location.Line, location.Column);
        }
        return Build(WorkflowSourceTranslator.Translate(document, "test.yaml", options));
    }

    public static WorkflowDefinition Build(string source)
    {
        var emitted = Compile(source);
        if (!emitted.Success) throw new WorkflowSourceException(string.Join("; ", emitted.Diagnostics.Select(item => item.Message)),
            emitted.Diagnostics[0].Path, emitted.Diagnostics[0].Line, emitted.Diagnostics[0].Column);
        return BuildAssembly(emitted.Assembly, emitted.Pdb);
    }

    internal static WorkflowDefinition BuildAssembly(byte[] assemblyBytes, byte[] pdbBytes) =>
        WithDefinition(assemblyBytes, pdbBytes, static definition => definition);

    internal static T WithDefinition<T>(byte[] assemblyBytes, byte[] pdbBytes, Func<WorkflowDefinition, T> inspect)
    {
        var context = new ScratchContext();
        try
        {
            using var assemblyStream = new MemoryStream(assemblyBytes);
            using var pdbStream = new MemoryStream(pdbBytes);
            var assembly = context.LoadFromStream(assemblyStream, pdbStream);
            var method = assembly.GetTypes().Select(type => type.GetMethod("Build", BindingFlags.Public | BindingFlags.Static,
                Type.EmptyTypes)).Single(method => method?.ReturnType == typeof(WorkflowDefinition))!;
            try { return inspect((WorkflowDefinition)method.Invoke(null, null)!); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        finally { context.Unload(); }
    }

    private sealed class ScratchContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Default.Assemblies.FirstOrDefault(assembly =>
            AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }
}

using System.Reflection;
using System.Text;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using YamlDotNet.Core;

namespace IxIFlow.Authoring;

/// <summary>References prepared by the host for standalone activity compilation.</summary>
public sealed class ActivityCompilationOptions
{
    public IReadOnlyList<string> PackageReferencePaths { get; init; } = [];
    public IReadOnlyList<Assembly> SharedContractAssemblies { get; init; } = [];
}

/// <summary>Compiles authored activity YAML with its exact source and generated C# embedded in a portable PDB.</summary>
public static class ActivityCompilation
{
    public const string SourceResourceName = "ixiflow/activity.yaml";

    public static WorkflowCompilationResult Compile(string sourceText, string sourcePath = SourceResourceName,
        ActivityCompilationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        options ??= new();
        ActivityDocument document;
        try { document = WorkflowYamlSerializer.ReadActivity(sourceText); }
        catch (YamlException error)
        {
            return new([], [], [new(WorkflowPublishStage.Validate, "IXIYAML001", error.Message,
                WorkflowDiagnosticSeverity.Error, sourcePath, (int)error.Start.Line, (int)error.Start.Column)]);
        }
        var validation = ActivityDocumentValidator.Validate(document, sourceText);
        if (!validation.IsValid)
            return new([], [], validation.Diagnostics.Select(item =>
            {
                var location = DocumentModel.GetLocation(document, item.Path);
                return new WorkflowPublishDiagnostic(WorkflowPublishStage.Validate, item.Code, item.Message,
                    item.Severity, sourcePath, location.Line, location.Column);
            }).ToArray());
        string source;
        try
        {
            source = ActivitySourceTranslator.Translate(document, sourceText, sourcePath,
                new ActivitySourceOptions { CancellationToken = cancellationToken });
        }
        catch (WorkflowSourceException error)
        {
            return new([], [], [new(WorkflowPublishStage.Validate, "IXIYAML001", error.Detail,
                WorkflowDiagnosticSeverity.Error, error.Path, error.Line, error.Column)]);
        }
        var identity = new ActivityPackageIdentity(document.Activity.Key, document.Activity.Version);
        var generated = SourceText.From(source, new UTF8Encoding(false));
        var authored = SourceText.From(sourceText, new UTF8Encoding(false));
        var tree = CSharpSyntaxTree.ParseText(generated, new CSharpParseOptions(LanguageVersion.CSharp14),
            "activity.g.cs", cancellationToken);
        var references = CreateReferences(options);
        var compilation = CSharpCompilation.Create(identity.AssemblyName, [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                deterministic: true, nullableContextOptions: NullableContextOptions.Enable));
        var activityAttribute = compilation.GetTypeByMetadataName(typeof(ActivitySdk.WorkflowActivityAttribute).FullName!);
        var generatedType = compilation.GetTypeByMetadataName(identity.MetadataTypeName);
        if (generatedType != null && document.Input.Values.Concat(document.InOut.Values).Any(member => member.HasDefault()))
        {
            try
            {
                source = ActivitySourceTranslator.Translate(document, sourceText, sourcePath, new ActivitySourceOptions
                {
                    CancellationToken = cancellationToken,
                    MemberTypes = generatedType.GetMembers().OfType<IPropertySymbol>().GroupBy(property => property.Name)
                        .ToDictionary(group => group.Key, group => group.First().Type)
                });
            }
            catch (WorkflowSourceException error)
            {
                return new([], [], [new(WorkflowPublishStage.Validate, "ACTD010", error.Detail,
                    WorkflowDiagnosticSeverity.Error, error.Path, error.Line, error.Column)]);
            }
            generated = SourceText.From(source, new UTF8Encoding(false));
            var boundTree = CSharpSyntaxTree.ParseText(generated, new CSharpParseOptions(LanguageVersion.CSharp14),
                "activity.g.cs", cancellationToken);
            compilation = compilation.ReplaceSyntaxTree(tree, boundTree);
            tree = boundTree;
            generatedType = compilation.GetTypeByMetadataName(identity.MetadataTypeName);
            activityAttribute = compilation.GetTypeByMetadataName(typeof(ActivitySdk.WorkflowActivityAttribute).FullName!);
        }
        var semanticModel = compilation.GetSemanticModel(tree);
        foreach (var declaration in tree.GetRoot(cancellationToken).DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol type ||
                SymbolEqualityComparer.Default.Equals(type, generatedType)) continue;
            // Assembly scanning discovers attributed types regardless of visibility, nesting,
            // or whether the rest of their activity contract is valid.
            var attribute = type.GetAttributes().FirstOrDefault(item =>
                SymbolEqualityComparer.Default.Equals(item.AttributeClass, activityAttribute));
            if (attribute == null) continue;
            var location = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? declaration.Identifier.GetLocation();
            var span = location.GetMappedLineSpan();
            return new([], [], [new(WorkflowPublishStage.Validate, "IXIYAML001",
                "An activity document cannot declare another discoverable activity", WorkflowDiagnosticSeverity.Error,
                sourcePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1)]);
        }
        using var assembly = new MemoryStream();
        using var pdb = new MemoryStream();
        var bytes = Encoding.UTF8.GetBytes(sourceText);
        var emitted = compilation.Emit(assembly, pdb,
            manifestResources: [new ResourceDescription(SourceResourceName, () => new MemoryStream(bytes, writable: false), isPublic: true)],
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: "activity.pdb"),
            embeddedTexts: [EmbeddedText.FromSource(sourcePath, authored), EmbeddedText.FromSource("activity.g.cs", generated)],
            cancellationToken: cancellationToken);
        var diagnostics = emitted.Diagnostics.Where(item => item.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(item =>
            {
                var span = item.Location.GetMappedLineSpan();
                var mapped = span.IsValid && span.Path != "activity.g.cs";
                return new WorkflowPublishDiagnostic(WorkflowPublishStage.Compile, item.Id, item.GetMessage(),
                    item.Severity == DiagnosticSeverity.Error ? WorkflowDiagnosticSeverity.Error : WorkflowDiagnosticSeverity.Warning,
                    mapped ? span.Path : sourcePath, mapped ? span.StartLinePosition.Line + 1 : 1,
                    mapped ? span.StartLinePosition.Character + 1 : 1);
            }).ToArray();
        return new(assembly.ToArray(), pdb.ToArray(), diagnostics);
    }

    private static IEnumerable<PortableExecutableReference> CreateReferences(ActivityCompilationOptions? options = null) =>
        ActivityFrameworkReferences.Metadata.Concat(FileReferencePaths(options).Select(path => MetadataReference.CreateFromFile(path)));

    /// <summary>The bundled .NET 10 framework references plus SDK, prepared package and shared contract references.</summary>
    public static string[] ReferencePaths(ActivityCompilationOptions? options = null) =>
        WorkflowCompilation.NormalizeReferences(ActivityFrameworkReferences.Paths.Concat(FileReferencePaths(options)));

    private static IEnumerable<string> FileReferencePaths(ActivityCompilationOptions? options) =>
        RuntimeReferencePaths(options).Where(path =>
            !ActivityFrameworkReferences.Contains(Path.GetFileNameWithoutExtension(path)) &&
            Path.GetDirectoryName(path) != Path.GetDirectoryName(typeof(object).Assembly.Location));

    // Keep implementation paths out of Roslyn, but available for inspecting the
    // emitted dependency closure and deriving actual SDK package versions.
    internal static string[] RuntimeReferencePaths(ActivityCompilationOptions? options = null)
    {
        options ??= new();
        var platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => WorkflowSharedAssemblies.IsSharedName(Path.GetFileNameWithoutExtension(path)));
        var shared = options.SharedContractAssemblies.Select(assembly =>
        {
            if (assembly.IsDynamic || assembly.Location.Length == 0 ||
                System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly) != System.Runtime.Loader.AssemblyLoadContext.Default)
                throw new ArgumentException("Shared contract assemblies must be file-backed default-context assemblies", nameof(options));
            return assembly.Location;
        });
        return WorkflowCompilation.NormalizeReferences(platform.Concat([typeof(IAsyncActivity).Assembly.Location,
            typeof(WorkflowDefinition).Assembly.Location]).Concat(shared).Concat(options.PackageReferencePaths));
    }
}

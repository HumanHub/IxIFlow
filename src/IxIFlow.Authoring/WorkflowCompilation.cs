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

    public static string[] ReferencePaths() => ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic && !assembly.IsCollectible)
            .Select(assembly => assembly.Location).Where(path => path.Length != 0))
        .Append(typeof(WorkflowDefinition).Assembly.Location).Append(typeof(IAsyncActivity).Assembly.Location)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();

    public static WorkflowCompilationResult Compile(string source, IReadOnlyList<string>? references = null,
        string sourcePath = "workflow.yaml", CancellationToken cancellationToken = default)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14),
            "workflow.g.cs", Encoding.UTF8, cancellationToken);
        var compilation = CSharpCompilation.Create("Workflow_" + ArtifactDigest.Of(Encoding.UTF8.GetBytes(source)).Value[7..],
            [tree], (references ?? ReferencePaths()).Select(path => MetadataReference.CreateFromFile(path)),
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

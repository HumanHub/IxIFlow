using System.Reflection;
using System.Text;
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;
using YamlDotNet.Core;

namespace IxIFlow.Authoring;

public sealed class WorkflowPublisherOptions
{
    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public WorkflowSourceOptions SourceOptions { get; init; } = new();
}

public sealed record WorkflowPublishResult(WorkflowVersion? Version, IReadOnlyList<WorkflowPublishDiagnostic> Diagnostics)
{
    public bool Success => Version != null && Diagnostics.All(item => item.Severity != WorkflowDiagnosticSeverity.Error);
}

/// <summary>Publishes import-free drafts using the runtime shared set and already-loaded application assemblies.</summary>
public sealed class WorkflowPublisher(IWorkflowCatalogStore catalog, IArtifactStore artifacts, WorkflowPublisherOptions? options = null)
{
    private readonly WorkflowPublisherOptions _options = options ?? new();
    private const string Path = "workflow.yaml";

    public async Task<WorkflowPublishResult> PublishAsync(WorkflowDraftId draftId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.BuildTimeout, TimeSpan.Zero);
        var diagnostics = new List<WorkflowPublishDiagnostic>();
        var stage = WorkflowPublishStage.Validate;
        var location = new DocumentLocation(1, 1);
        WorkflowDocument document;
        WorkflowPublishResult Failure(string code, string message) => new(null,
            [.. diagnostics, new(stage, code, message, WorkflowDiagnosticSeverity.Error, Path, Math.Max(1, location.Line), Math.Max(1, location.Column))]);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var published = await catalog.GetWorkflowVersionAsync(draftId.Name, draftId.Version);
            if (published?.PublishedDraftRevision == draftId.Revision && draftId.Revision > 0) return new(published, []);
            var draft = await catalog.GetDefinitionAsync(draftId.Name, draftId.Version);
            if (draft == null || draft.Revision != draftId.Revision)
                return Failure("PUB001", "The draft was not found or its revision changed");
            if (draft.Kind != "workflow") return Failure("PUB002", "Only workflow YAML drafts can be published");
            document = WorkflowYamlSerializer.Read(draft.Source);
            location = document.Workflow.Location;
            if (document.Imports.Catalogs.Count != 0 || document.Imports.Workflows.Count != 0)
            {
                location = document.Imports.Location;
                return Failure("PUB003", "Imports are not supported by this publisher yet");
            }
            var sourceOptions = ResolveTypes(document, _options.SourceOptions);
            var validation = await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(sourceOptions), cancellationToken);
            diagnostics.AddRange(validation.Diagnostics.Select(item =>
            {
                var at = DocumentModel.GetLocation(document, item.Path);
                return new WorkflowPublishDiagnostic(stage, item.Code, item.Message, item.Severity, Path, at.Line, at.Column);
            }));
            if (!validation.IsValid) return new(null, diagnostics);
            stage = WorkflowPublishStage.Translate;
            var source = WorkflowSourceTranslator.Translate(document, Path, sourceOptions);
            stage = WorkflowPublishStage.Compile;
            var references = WorkflowCompilation.ReferencePaths();
            var compilation = WorkflowCompilation.Compile(source, references, Path, cancellationToken);
            diagnostics.AddRange(compilation.Diagnostics);
            if (!compilation.Success) return new(null, diagnostics);
            stage = WorkflowPublishStage.Test;
            var tested = await WorkflowBuildWorker.RunAsync(compilation, references, _options.BuildTimeout, cancellationToken);
            if (tested.Error != null)
            {
                location = tested.Line > 1 ? FindBuildLocation(document, tested.Line, tested.Column) : document.Workflow.Location;
                return Failure("PUB004", tested.Error);
            }
            if (tested.Name != draft.Name || tested.Version != draft.Version)
            {
                location = DocumentModel.GetLocation(document, tested.Name != draft.Name ? "workflow.name" : "workflow.version");
                return Failure("PUB005", $"Declared identity '{tested.Name}' v{tested.Version} does not match draft '{draft.Name}' v{draft.Version}");
            }
            stage = WorkflowPublishStage.Publish;
            var manifest = new WorkflowBundleManifest(1, tested.Name, tested.Version, tested.StructureHash, tested.DataType, tested.EntryType,
                typeof(WorkflowDefinition).Assembly.GetName().Version!.ToString(), WorkflowDefinitionValidator.CheckpointContractVersion,
                tested.CombinerSignature, WorkflowCompilation.Settings,
                references.Select(AssemblyName.GetAssemblyName).ToDictionary(name => name.FullName!, name => name.Version!.ToString(), StringComparer.Ordinal),
                new Dictionary<string, ArtifactDigest>());
            var archive = WorkflowBundleArchive.Create(manifest, new Dictionary<string, byte[]>
            {
                [WorkflowBundleArchive.AssemblyPath] = compilation.Assembly, [WorkflowBundleArchive.PdbPath] = compilation.Pdb,
                [WorkflowBundleArchive.YamlPath] = Encoding.UTF8.GetBytes(WorkflowYamlSerializer.Write(document)),
                [WorkflowBundleArchive.SourcePath] = Encoding.UTF8.GetBytes(source)
            });
            // Use the verified manifest, including entry hashes, as the durable catalog copy.
            var digest = ArtifactDigest.Of(archive);
            var bundle = await WorkflowBundleArchive.ReadAsync(new MemoryStream(archive), digest, cancellationToken);
            await using var upload = new MemoryStream(archive, writable: false);
            var storedDigest = await artifacts.PutAsync(ArtifactKind.Bundle, upload, cancellationToken);
            if (storedDigest != digest) return Failure("PUB006", "The artifact store returned a different archive digest");
            cancellationToken.ThrowIfCancellationRequested();
            var version = new WorkflowVersion(draft.Name, draft.Version, tested.StructureHash,
                ArtifactDigest: digest, Dependencies: tested.Dependencies, PublishedDraftRevision: draft.Revision,
                ManifestJson: JsonSerializer.Serialize(bundle.Manifest));
            return new(await catalog.PublishDefinitionAsync(draftId, version), diagnostics);
        }
        catch (WorkflowSourceException error)
        {
            location = new(error.Line, error.Column);
            return Failure("IXIYAML001", error.Detail);
        }
        catch (YamlException error)
        {
            location = new((int)error.Start.Line, (int)error.Start.Column);
            return Failure("IXIYAML001", error.Message);
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        { return Failure("PUB007", error.Message); }
    }

    private static WorkflowSourceOptions ResolveTypes(WorkflowDocument document, WorkflowSourceOptions configured)
    {
        var result = new WorkflowSourceOptions { RequireResolvedTypes = true, Namespace = configured.Namespace, ClassName = configured.ClassName };
        foreach (var pair in configured.Types) result.Types.Add(pair);
        foreach (var pair in configured.Members) result.Members.Add(pair);
        foreach (var pair in configured.Activities) result.Activities.Add(pair);
        foreach (var pair in configured.Workflows) result.Workflows.Add(pair);
        foreach (var pair in configured.WorkflowDataTypes) result.WorkflowDataTypes.Add(pair);
        foreach (var pair in configured.WorkflowIdentities) result.WorkflowIdentities.Add(pair);
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic && !assembly.IsCollectible).ToArray();
        foreach (var key in WorkflowSourceTranslator.TypeNames(document).Distinct())
        {
            if (result.Types.ContainsKey(key)) continue;
            var name = key.Replace("global::", "", StringComparison.Ordinal);
            var matches = assemblies.Select(assembly => FindType(assembly, name)).OfType<Type>().Distinct().ToArray();
            if (matches.Length == 1) result.Types[key] = "global::" + matches[0].FullName!.Replace('+', '.');
        }
        return result;
    }

    private static Type? FindType(Assembly assembly, string name)
    {
        var type = assembly.GetType(name);
        // YAML uses C# nested names, while reflection expects '+' between declaring types.
        for (var dot = name.LastIndexOf('.'); type == null && dot >= 0; dot = name.LastIndexOf('.', dot - 1))
        {
            var nested = name[..dot] + "+" + name[(dot + 1)..].Replace('.', '+');
            type = assembly.GetType(nested);
            if (dot == 0) break;
        }
        return type;
    }

    private static DocumentLocation FindBuildLocation(WorkflowDocument document, int line, int column)
    {
        DocumentLocation? found = null;
        void Visit(object? value)
        {
            if (found != null) return;
            if (value is DocumentNode node)
            {
                if (node.Location.Line == line) { found = node.Location; return; }
                foreach (var member in DocumentModel.GetMembers(node.GetType())) Visit(member.GetValue(node));
            }
            else if (value is System.Collections.IDictionary dictionary)
                foreach (var item in dictionary.Values) Visit(item);
            else if (value is System.Collections.IEnumerable items && value is not string)
                foreach (var item in items) Visit(item);
        }
        Visit(document);
        return new(line, column > 1 ? column : found?.Column ?? 1);
    }
}

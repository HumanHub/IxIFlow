using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Core;

namespace IxIFlow.Authoring;

public sealed class WorkflowPublisherOptions
{
    public string TargetFramework { get; init; } = "net10.0";
    /// <summary>Standard NuGet.Config. Feed credentials remain owned by NuGet and are never persisted in the catalog.</summary>
    public string? NuGetConfigPath { get; init; }
    public WorkflowBundleReadLimits PackageReadLimits { get; init; } = new();
    public int MaxPackageCandidates { get; init; } = 2048;
    public int MaxResolvedPackages { get; init; } = 256;
    public long MaxPackageResolutionBytes { get; init; } = 256 * 1024 * 1024;
    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public WorkflowSourceOptions SourceOptions { get; init; } = new();
}

public sealed record WorkflowPublishResult(WorkflowVersion? Version, IReadOnlyList<WorkflowPublishDiagnostic> Diagnostics)
{
    public bool Success => Version != null && Diagnostics.All(item => item.Severity != WorkflowDiagnosticSeverity.Error);
    public WorkflowPublicationReceipt? Receipt => Success && Version?.PublishedDraft != null ? WorkflowPublicationRules.Receipt(Version) : null;
}

/// <summary>Publishes immutable workflows with a digest-pinned NuGet dependency closure.</summary>
public sealed class WorkflowPublisher(IWorkflowCatalogStore catalog, IArtifactStore artifacts, WorkflowPublisherOptions? options = null,
    ILogger<WorkflowPublisher>? logger = null)
{
    private readonly WorkflowPublisherOptions _options = options ?? new();
    private readonly ILogger<WorkflowPublisher> _logger = logger ?? NullLogger<WorkflowPublisher>.Instance;
    private const string Path = "workflow.yaml";

    public Task<WorkflowPackage> UploadPackageAsync(Stream package, CancellationToken cancellationToken = default) =>
        UploadPackageCoreAsync(package, cancellationToken);

    private async Task<WorkflowPackage> UploadPackageCoreAsync(Stream package, CancellationToken token) =>
        (await new PackageIngestion(catalog, artifacts, _options).UploadPackageAsync(package, cancellationToken: token)).Stored;

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
            if (published?.PublishedDraft == draftId && draftId.Revision > 0) return new(published, []);
            var draft = await catalog.GetDefinitionAsync(draftId.Kind, draftId.Name, draftId.Version);
            if (draft == null || draft.Id != draftId)
            {
                published = await catalog.GetWorkflowVersionAsync(draftId.Name, draftId.Version);
                if (published?.PublishedDraft == draftId && draftId.Revision > 0) return new(published, []);
                return Failure("PUB001", "The draft was not found or its revision changed");
            }
            if (draft.Kind != "workflow") return Failure("PUB002", "Only workflow YAML drafts can be published");
            document = WorkflowYamlSerializer.Read(draft.Source);
            location = document.Workflow.Location;
            location = document.Imports.Catalogs.Count > 0 ? document.Imports.Location : document.Workflow.Location;
            using var packages = await new WorkflowPackageResolver(catalog, artifacts, _options).ResolveAsync(document.Imports.Catalogs, PackageAcquisition.Persist, [], cancellationToken);
            var callees = await new WorkflowCallees(catalog, artifacts, _options).ResolveAsync(document, packages, cancellationToken);
            await new WorkflowPackageResolver(catalog, artifacts, _options).RefreshDescriptorsAsync(packages, cancellationToken);
            var references = WorkflowCompilation.CreateReferences(_options.SourceOptions, packages.References);
            var sourceOptions = ResolveTypes(document, _options.SourceOptions, references);
            WorkflowCallees.Bind(callees, sourceOptions, references, packages);
            var activityClaims = ResolvePackageActivities(sourceOptions, references, packages);
            foreach (var import in document.Imports.Catalogs.Concat(document.Imports.Workflows)) sourceOptions.ResolvedImports.Add(import);
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
            var compilation = WorkflowCompilation.Compile(source, references, Path, cancellationToken);
            diagnostics.AddRange(compilation.Diagnostics);
            if (!compilation.Success) return new(null, diagnostics);
            stage = WorkflowPublishStage.Test;
            // Without a profile every runtime and SDK assembly would be bundled privately and no host could load it.
            if (!WorkflowSharedAssemblies.IsUsable)
                return Failure("PUB008", "This process has no usable shared compatibility profile (a self-contained single-file process, or unreadable runtime metadata); publish from a framework-dependent process");
            var (entries, shared) = BundleAssemblies(compilation.Assembly, references.Paths, packages);
            var implementations = packages.RuntimeAssemblies.ToDictionary();
            foreach (var bytes in entries.Values) implementations.TryAdd(ResolvedWorkflowPackages.AssemblyIdentity(bytes).Name!, bytes);
            var tested = await WorkflowBuild.RunAsync(compilation, implementations, _options.BuildTimeout, _logger, cancellationToken, activityClaims);
            if (tested.Error != null)
            {
                location = tested.Line > 1 ? FindBuildLocation(document, tested.Line, tested.Column) : document.Workflow.Location;
                return Failure("PUB004", tested.Error);
            }
            if (tested.Invocations == null)
                return Failure("PUB004", "The workflow build did not report its invocation metadata");
            if (tested.Name != draft.Name || tested.Version != draft.Version)
            {
                location = DocumentModel.GetLocation(document, tested.Name != draft.Name ? "workflow.name" : "workflow.version");
                return Failure("PUB005", $"Declared identity '{tested.Name}' v{tested.Version} does not match draft '{draft.Name}' v{draft.Version}");
            }
            stage = WorkflowPublishStage.Publish;
            var manifest = new WorkflowBundleManifest(4, tested.Name, tested.Version, tested.StructureHash, tested.DataType, tested.EntryType,
                typeof(WorkflowDefinition).Assembly.GetName().Version!.ToString(), WorkflowDefinitionValidator.CheckpointContractVersion,
                tested.CombinerSignature, tested.ContractJson, WorkflowContract.Parse(tested.ContractJson).Hash, WorkflowCompilation.Settings,
                shared,
                new Dictionary<string, ArtifactDigest>())
            { Invocations = tested.Invocations, TargetFramework = _options.TargetFramework, ActivityPackages = packages.Pins };
            entries.Add(WorkflowBundleArchive.AssemblyPath, compilation.Assembly);
            entries.Add(WorkflowBundleArchive.PdbPath, compilation.Pdb);
            entries.Add(WorkflowBundleArchive.YamlPath, Encoding.UTF8.GetBytes(WorkflowYamlSerializer.Write(document)));
            entries.Add(WorkflowBundleArchive.SourcePath, Encoding.UTF8.GetBytes(source));
            var archive = WorkflowBundleArchive.Create(manifest, entries);
            // Use the verified manifest, including entry hashes, as the durable catalog copy.
            var digest = ArtifactDigest.Of(archive);
            var bundle = await WorkflowBundleArchive.ReadAsync(new MemoryStream(archive), digest, cancellationToken);
            await using var upload = new MemoryStream(archive, writable: false);
            var storedDigest = await artifacts.PutAsync(ArtifactKind.Bundle, upload, cancellationToken);
            if (storedDigest != digest) return Failure("PUB006", "The artifact store returned a different archive digest");
            cancellationToken.ThrowIfCancellationRequested();
            var version = new WorkflowVersion(draft.Name, draft.Version, tested.StructureHash,
                tested.ContractJson, bundle.Manifest.ContractHash,
                ArtifactDigest: digest, Dependencies: tested.Dependencies, ArtifactReferences: packages.Pins.Select(package => package.Digest).ToArray(), PublishedDraft: draft.Id,
                ManifestJson: JsonSerializer.Serialize(bundle.Manifest), Invocations: bundle.Manifest.Invocations);
            return new(await catalog.PublishDefinitionAsync(draftId, version, artifacts), diagnostics);
        }
        catch (WorkflowPublishException error)
        {
            location = error.Location;
            return Failure(error.Code, error.Message);
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

    private static (Dictionary<string, byte[]> Entries, Dictionary<string, string> Shared) BundleAssemblies(byte[] workflow, IEnumerable<string> referencePaths,
        ResolvedWorkflowPackages packages)
    {
        var paths = referencePaths.ToDictionary(path => AssemblyName.GetAssemblyName(path).Name!, StringComparer.OrdinalIgnoreCase);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var shared = new Dictionary<string, string>(StringComparer.Ordinal);
        void Share(string path)
        {
            var identity = AssemblyName.GetAssemblyName(path);
            shared.TryAdd(identity.FullName!, identity.Version!.ToString());
        }
        Share(typeof(object).Assembly.Location);
        Share(paths["System.Runtime"]);
        var pending = new Queue<byte[]>();
        pending.Enqueue(workflow);
        foreach (var bytes in packages.AllRuntimeAssemblies) pending.Enqueue(bytes);
        while (pending.TryDequeue(out var bytes))
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes));
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                var name = metadata.GetString(reference.Name);
                if (packages.RuntimeNames.Contains(name)) continue;
                if (!paths.TryGetValue(name, out var path)) throw new InvalidOperationException($"Dependency '{name}' is outside the publishing reference closure");
                var identity = AssemblyName.GetAssemblyName(path);
                if (WorkflowSharedAssemblies.IsShared(identity))
                {
                    Share(path);
                    continue;
                }
                if (packages.Owners.ContainsKey(name))
                {
                    continue;
                }
                var key = "private/" + name + ".dll";
                if (entries.ContainsKey(key)) continue;
                var dependency = File.ReadAllBytes(path);
                entries.Add(key, dependency);
                pending.Enqueue(dependency);
            }
        }
        return (entries, shared);
    }

    private static WorkflowSourceOptions ResolveTypes(WorkflowDocument document, WorkflowSourceOptions configured, WorkflowCompilation.ReferenceSet references)
    {
        var result = new WorkflowSourceOptions { RequireResolvedTypes = true, Namespace = configured.Namespace, ClassName = configured.ClassName };
        foreach (var pair in configured.Types) result.Types.Add(pair);
        foreach (var pair in configured.Members) result.Members.Add(pair);
        foreach (var pair in configured.DataContracts) result.DataContracts.Add(pair);
        foreach (var pair in configured.Activities) result.Activities.Add(pair);
        foreach (var pair in configured.Workflows) result.Workflows.Add(pair);
        foreach (var pair in configured.WorkflowDataTypes) result.WorkflowDataTypes.Add(pair);
        foreach (var pair in configured.WorkflowIdentities) result.WorkflowIdentities.Add(pair);
        foreach (var key in WorkflowSourceTranslator.TypeNames(document).Distinct())
        {
            var name = result.Types.TryGetValue(key, out var configuredType) ? configuredType : key;
            if (references.BindType(name, result.Namespace) is { } type)
            {
                result.Types[key] = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                result.DataContracts[key] = WorkflowDataDocumentContract.FromSymbol(type);
                var workflowData = type.AllInterfaces.Where(contract => contract.OriginalDefinition.ToDisplayString() == "IxIFlow.Builders.Interfaces.IWorkflow<TWorkflowData>")
                    .Select(contract => contract.TypeArguments[0]).OfType<INamedTypeSymbol>().ToArray();
                if (workflowData.Length == 1)
                {
                    var dataType = workflowData[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    result.WorkflowDataTypes[key] = dataType;
                    result.DataContracts[dataType] = WorkflowDataDocumentContract.FromSymbol(workflowData[0]);
                    var identity = type.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "IxIFlow.Core.WorkflowAttribute");
                    if (identity?.ConstructorArguments.FirstOrDefault().Value is string workflowName)
                        result.WorkflowIdentities[key] = new(workflowName, identity.NamedArguments.FirstOrDefault(pair => pair.Key == "Version").Value.Value as int? ?? 1);
                }
                foreach (var property in Properties(type))
                    result.Members.TryAdd(key + "." + char.ToLowerInvariant(property.Name[0]) + property.Name[1..], property.Name);
            }
        }
        return result;
    }

    private static IEnumerable<IPropertySymbol> Properties(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>().Where(property => property.DeclaredAccessibility == Accessibility.Public))
                yield return property;
    }

    private static IReadOnlyList<PackageScopeClaim> ResolvePackageActivities(WorkflowSourceOptions options, WorkflowCompilation.ReferenceSet references, ResolvedWorkflowPackages packages)
    {
        var claims = new List<PackageScopeClaim>();
        foreach (var descriptor in packages.Activities.Values.SelectMany(items => items))
        {
            var type = references.Assemblies.Where(assembly => assembly.Identity.Name == packages.ActivityAssemblies[descriptor.Key])
                .Select(assembly => assembly.GetTypeByMetadataName(descriptor.MetadataTypeName)).OfType<INamedTypeSymbol>().Single();
            claims.Add(ScopePackageContracts.Describe(type, descriptor.PackageId, descriptor.PackageVersion, descriptor.Key, descriptor.Version));
            if (!options.Activities.TryAdd(descriptor.Key, new(descriptor.Key, descriptor.Version, descriptor.PackageId, descriptor.PackageVersion)))
                throw new WorkflowPublishException("PUB014", $"Duplicate package activity key '{descriptor.Key}'", new(1, 1));
            options.Types[descriptor.Key] = descriptor.CSharpTypeName;
            foreach (var member in descriptor.Inputs.Concat(descriptor.Outputs))
                options.Members.TryAdd(descriptor.Key + "." + char.ToLowerInvariant(member.Name[0]) + member.Name[1..], member.Name);
        }
        return claims;
    }

    internal static INamedTypeSymbol? FindType(IAssemblySymbol assembly, string name)
    {
        var type = assembly.GetTypeByMetadataName(name);
        // YAML uses C# nested names, while metadata expects '+' between declaring types.
        for (var dot = name.LastIndexOf('.'); type == null && dot >= 0; dot = name.LastIndexOf('.', dot - 1))
        {
            var nested = name[..dot] + "+" + name[(dot + 1)..].Replace('.', '+');
            type = assembly.GetTypeByMetadataName(nested);
            if (dot == 0) break;
        }
        return type;
    }

    internal static Type? FindType(Assembly assembly, string name)
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

using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;
using IxIFlow.Templates;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YamlDotNet.Core;

namespace IxIFlow.Authoring;

public sealed class WorkflowPublisherOptions
{
    /// <summary>Default-context application contracts. Their complete non-SDK closure must be declared; the first use freezes a snapshot.</summary>
    public IReadOnlyList<Assembly> SharedContractAssemblies { get; init; } = [];
    private WorkflowSharedAssemblyProfile? _sharedProfile;
    internal WorkflowSharedAssemblyProfile SharedProfile => LazyInitializer.EnsureInitialized(ref _sharedProfile,
        () => WorkflowSharedAssemblies.CreateProfile(SharedContractAssemblies));
    public string TargetFramework { get; init; } = "net10.0";
    /// <summary>Standard NuGet.Config. Feed credentials remain owned by NuGet and are never persisted in the catalog.</summary>
    public string? NuGetConfigPath { get; init; }
    public WorkflowBundleReadLimits PackageReadLimits { get; init; } = new();
    public int MaxPackageCandidates { get; init; } = 2048;
    public int MaxResolvedPackages { get; init; } = 256;
    public long MaxPackageResolutionBytes { get; init; } = 256 * 1024 * 1024;
    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PublicationHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PublicationStaleAfter { get; init; } = TimeSpan.FromSeconds(90);
    public int MaxPreviewModelBytes { get; init; } = 1024 * 1024;
    public int MaxPreviewOutputBytes { get; init; } = 1024 * 1024;
    public WorkflowSourceOptions SourceOptions { get; init; } = new();
}

/// <summary>Publishes immutable workflows, activity packages and templates with digest-pinned package dependencies.</summary>
public sealed partial class WorkflowPublisher(IWorkflowCatalogStore catalog, IArtifactStore artifacts, WorkflowPublisherOptions? options = null,
    ILogger<WorkflowPublisher>? logger = null, TemplateEngines? templateEngines = null)
{
    private readonly WorkflowPublisherOptions _options = options ?? new();
    private readonly ILogger<WorkflowPublisher> _logger = logger ?? NullLogger<WorkflowPublisher>.Instance;
    private const string Path = "workflow.yaml";

    public Task<PackageUploadCandidate> ReadPackageAsync(Stream package, CancellationToken cancellationToken = default) =>
        new PackageIngestion(catalog, artifacts, _options).ReadPackageAsync(package, cancellationToken);

    public Task<PackageRegistration> StorePackageAsync(PackageUploadCandidate candidate, string? actor = null, CancellationToken cancellationToken = default) =>
        new PackageIngestion(catalog, artifacts, _options).StorePackageAsync(candidate, actor, cancellationToken);

    public async Task<PackageRegistration> UploadPackageAsync(Stream package, string? actor = null, CancellationToken cancellationToken = default) =>
        await StorePackageAsync(await ReadPackageAsync(package, cancellationToken), actor, cancellationToken);

    public async Task<WorkflowPublishResult> PublishAsync(WorkflowDraftId draftId, string? actor = null, CancellationToken cancellationToken = default)
    {
        WorkflowPublicationRules.Id(draftId);
        WorkflowPublicationRules.Actor(actor);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var recovered = await FindPublishedAsync(draftId);
            if (recovered != null) return recovered;
            var draft = await catalog.GetDefinitionAsync(draftId.Kind, draftId.Name, draftId.Version);
            if (draft?.Id != draftId)
                return await FindPublishedAsync(draftId) ?? Failed(draftId.Kind, WorkflowPublishFailures.Diagnostic(
                    "PUB001", "The draft was not found or its identity changed", WorkflowPublishDiagnosticClass.Conflict));
            return await RunAsync(draft, PackageAcquisition.Persist, actor, cancellationToken);
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogError(error, "Publication failed for {Draft}", draftId);
            return Failed(draftId.Kind, WorkflowPublishFailures.Classify(error));
        }
    }

    public async Task<WorkflowValidationReport> ValidateAsync(WorkflowDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var result = await RunAsync(draft, PackageAcquisition.Scratch, null, cancellationToken);
        return new(draft.Kind, result.Diagnostics);
    }

    public async Task<TemplatePreviewResult> PreviewTemplateAsync(WorkflowDraft draft, TemplatePreviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (draft.Kind != WorkflowDraftKinds.Template) return new(null, [WorkflowPublishFailures.Unsupported(draft.Kind)]);
        using var profile = WorkflowSharedAssemblies.Enter(_options.SharedProfile);
        var result = await PrepareTemplateAsync(draft, PackageAcquisition.Scratch, request, cancellationToken);
        return new(result.Rendered, result.Diagnostics);
    }

    internal async Task<WorkflowPublishResult?> FindPublishedAsync(WorkflowDraftId draft)
    {
        // Kind handlers and their reconciliation reads share this single dispatcher.
        switch (draft.Kind)
        {
            case WorkflowDraftKinds.Workflow:
                var workflow = await catalog.GetWorkflowVersionAsync(draft.Name, draft.Version);
                return workflow?.PublishedDraft == draft ? new(draft.Kind, workflow, null, null, []) : null;
            case WorkflowDraftKinds.Activity:
                var package = await catalog.GetPackageAsync(draft.Name, PackageIndexRules.PublishedVersion(draft));
                return package?.PublishedDraft == draft ? new(draft.Kind, null, package, null, []) : null;
            case WorkflowDraftKinds.Template:
                var template = await catalog.GetTemplateVersionAsync(draft.Name, draft.Version);
                return template?.PublishedDraft == draft ? new(draft.Kind, null, null, template, []) : null;
            default:
                return null;
        }
    }

    private static WorkflowPublishResult Failed(string kind, WorkflowPublishDiagnostic diagnostic) => new(kind, null, null, null, [diagnostic]);

    private async Task<WorkflowPublishResult> RunAsync(WorkflowDraft draft, PackageAcquisition acquisition, string? actor, CancellationToken cancellationToken)
    {
        using var profile = WorkflowSharedAssemblies.Enter(_options.SharedProfile);
        if (draft.Kind == WorkflowDraftKinds.Activity) return await RunActivityAsync(draft, acquisition, actor, cancellationToken);
        if (draft.Kind == WorkflowDraftKinds.Template) return await RunTemplateAsync(draft, acquisition, actor, cancellationToken);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.BuildTimeout, TimeSpan.Zero);
        var diagnostics = new List<WorkflowPublishDiagnostic>();
        var stage = WorkflowPublishStage.Validate;
        var location = new DocumentLocation(1, 1);
        WorkflowDocument document;
        WorkflowPublishResult Failure(string code, string message, WorkflowPublishDiagnosticClass @class = WorkflowPublishDiagnosticClass.Authoring) =>
            new(draft.Kind, null, null, null, [.. diagnostics, WorkflowPublishFailures.Diagnostic(code, message, @class, stage, location)]);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { WorkflowCatalogRules.ValidateText(draft.Source); }
            catch (ArgumentException) { return Failure("IXIYAML001", "The document contains invalid text"); }
            if (draft.Kind is not WorkflowDraftKinds.Workflow)
                return Failed(draft.Kind, WorkflowPublishFailures.Unsupported(draft.Kind));
            var identity = WorkflowDocumentKinds.Identify(draft.Source);
            if ((identity.Kind, identity.Name, identity.Version) != (draft.Kind, draft.Name, draft.Version))
            {
                location = identity.Location;
                return Failure("PUB005", "The document identity does not match the draft identity");
            }
            document = WorkflowYamlSerializer.Read(draft.Source);
            location = document.Workflow.Location;
            location = document.Imports.Catalogs.Count > 0 ? document.Imports.Location : document.Workflow.Location;
            using var packages = await new WorkflowPackageResolver(catalog, artifacts, _options).ResolveAsync(document.Imports.Catalogs, acquisition, [], cancellationToken);
            // Preserve the new authoring closure before verified callees contribute their existing pins.
            // Shared pins still belong to this closure and must be rechecked at the final catalog write.
            var authoringPackages = packages.Pins.Select(package => package.Digest).ToArray();
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
            if (!validation.IsValid) return new(draft.Kind, null, null, null, diagnostics);
            // Document validation uses authored positions. Generated diagnostics use the
            // exact pinned source that will be stored in the bundle.
            var canonical = WorkflowYamlSerializer.Write(document);
            document = WorkflowYamlSerializer.Read(canonical);
            location = document.Workflow.Location;
            stage = WorkflowPublishStage.Translate;
            var source = WorkflowSourceTranslator.Translate(document, Path, sourceOptions);
            stage = WorkflowPublishStage.Compile;
            var compilation = WorkflowCompilation.Compile(source, references, Path, cancellationToken);
            diagnostics.AddRange(compilation.Diagnostics);
            if (!compilation.Success) return new(draft.Kind, null, null, null, diagnostics);
            stage = WorkflowPublishStage.Test;
            // Without a profile every runtime and SDK assembly would be bundled privately and no host could load it.
            if (!WorkflowSharedAssemblies.IsUsable)
                return Failure("PUB008", "This process has no usable shared compatibility profile (a self-contained single-file process, or unreadable runtime metadata); publish from a framework-dependent process", WorkflowPublishDiagnosticClass.Unavailable);
            var (entries, shared) = BundleAssemblies(compilation.Assembly, references.Paths, packages);
            var implementations = packages.RuntimeAssemblies.ToDictionary();
            foreach (var bytes in entries.Values) implementations.TryAdd(ResolvedWorkflowPackages.AssemblyIdentity(bytes).Name!, bytes);
            var tested = await WorkflowBuild.RunAsync(compilation, implementations, _options.BuildTimeout, _logger, cancellationToken, activityClaims, packages.RuntimeSymbols);
            if (tested.Error != null)
            {
                location = tested.Line > 1 ? FindBuildLocation(document, tested.Line, tested.Column) : document.Workflow.Location;
                return Failure(tested.ErrorCode, tested.Error, tested.ErrorClass);
            }
            if (tested.Invocations == null)
                return Failure("PUB004", "The workflow build did not report its invocation metadata");
            if (tested.Name != draft.Name || tested.Version != draft.Version)
            {
                location = DocumentModel.GetLocation(document, tested.Name != draft.Name ? "workflow.name" : "workflow.version");
                return Failure("PUB005", $"Declared identity '{tested.Name}' v{tested.Version} does not match draft '{draft.Name}' v{draft.Version}");
            }
            if (acquisition == PackageAcquisition.Scratch) return new(draft.Kind, null, null, null, diagnostics);
            stage = WorkflowPublishStage.Publish;
            var manifest = new WorkflowBundleManifest(4, tested.Name, tested.Version, tested.StructureHash, tested.DataType, tested.EntryType,
                typeof(WorkflowDefinition).Assembly.GetName().Version!.ToString(), WorkflowDefinitionValidator.CheckpointContractVersion,
                tested.CombinerSignature, tested.ContractJson, WorkflowContract.Parse(tested.ContractJson).Hash, WorkflowCompilation.Settings,
                shared,
                new Dictionary<string, ArtifactDigest>())
            { Invocations = tested.Invocations, TargetFramework = _options.TargetFramework, ActivityPackages = packages.Pins };
            entries.Add(WorkflowBundleArchive.AssemblyPath, compilation.Assembly);
            entries.Add(WorkflowBundleArchive.PdbPath, compilation.Pdb);
            entries.Add(WorkflowBundleArchive.YamlPath, Encoding.UTF8.GetBytes(canonical));
            entries.Add(WorkflowBundleArchive.SourcePath, Encoding.UTF8.GetBytes(source));
            var archive = WorkflowBundleArchive.Create(manifest, entries);
            // Use the verified manifest, including entry hashes, as the durable catalog copy.
            var digest = ArtifactDigest.Of(archive);
            var bundle = await WorkflowBundleArchive.ReadAsync(new MemoryStream(archive), digest, cancellationToken);
            await using var upload = new MemoryStream(archive, writable: false);
            var storedDigest = await artifacts.PutAsync(ArtifactKind.Bundle, upload, cancellationToken);
            if (storedDigest != digest) return Failure("PUB006", "The artifact store returned a different archive digest", WorkflowPublishDiagnosticClass.Unavailable);
            cancellationToken.ThrowIfCancellationRequested();
            var version = new WorkflowVersion(draft.Name, draft.Version, tested.StructureHash,
                tested.ContractJson, bundle.Manifest.ContractHash, WorkflowDescriptor.Parse(tested.DescriptorJson),
                ArtifactDigest: digest, Dependencies: tested.Dependencies, ArtifactReferences: packages.Pins.Select(package => package.Digest).ToArray(), PublishedDraft: draft.Id,
                ManifestJson: JsonSerializer.Serialize(bundle.Manifest), Invocations: bundle.Manifest.Invocations)
            { AuthoringPackageReferences = authoringPackages };
            return new(draft.Kind, await catalog.PublishDefinitionAsync(draft.Id, version, artifacts, actor), null, null, diagnostics);
        }
        catch (WorkflowPublishException error)
        {
            if (error.InnerException != null) _logger.LogWarning(error, "Workflow dependency resolution failed for {Draft}", draft.Id);
            location = error.Location ?? location;
            return Failure(error.Code, error.Message, WorkflowPublishFailures.ClassFor(error.Code));
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
        {
            _logger.LogError(error, "Workflow pipeline failed for {Draft}", draft.Id);
            var diagnostic = WorkflowPublishFailures.Classify(error, stage, location);
            return new(draft.Kind, null, null, null, [.. diagnostics, diagnostic]);
        }
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
                if (!paths.TryGetValue(name, out var path)) throw new WorkflowPublishException("PUB003", $"Dependency '{name}' is outside the publishing reference closure", null);
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
        foreach (var pair in configured.Templates) result.Templates.Add(pair);
        foreach (var pair in configured.Members) result.Members.Add(pair);
        foreach (var pair in configured.DataContracts) result.DataContracts.Add(pair);
        foreach (var pair in configured.Activities) result.Activities.Add(pair);
        foreach (var pair in configured.Workflows) result.Workflows.Add(pair);
        foreach (var pair in configured.WorkflowDataTypes) result.WorkflowDataTypes.Add(pair);
        foreach (var pair in configured.WorkflowIdentities) result.WorkflowIdentities.Add(pair);
        // Built-in activities belong to the shared profile, so they have no package import.
        const string sendKey = "ixiflow.templates.send-message";
        var templatesAssembly = references.Assemblies.SingleOrDefault(assembly => assembly.Identity.Name == "IxIFlow.Templates" &&
            WorkflowSharedAssemblies.IsShared(new AssemblyName(assembly.Identity.ToString())));
        if (templatesAssembly?.GetTypeByMetadataName("IxIFlow.Templates.SendTemplateMessage") is { } send)
        {
            result.Types[sendKey] = send.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            result.Activities[sendKey] = new(sendKey, "1.0");
            foreach (var property in Properties(send))
                result.Members.TryAdd(sendKey + "." + char.ToLowerInvariant(property.Name[0]) + property.Name[1..], property.Name);
        }
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

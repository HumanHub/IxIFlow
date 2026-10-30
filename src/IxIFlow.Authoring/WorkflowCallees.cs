using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;

namespace IxIFlow.Authoring;

internal sealed class WorkflowPublishException(string code, string message, DocumentLocation? location, Exception? inner = null)
    : Exception(message, inner)
{
    internal string Code { get; } = code;
    internal DocumentLocation? Location { get; } = location;
}

/// <summary>
/// A load context's deliberate refusal of an assembly the author's closure does not provide. It derives from
/// FileNotFoundException because a load context must report a missing assembly that way; publication reports it
/// as the author's error (PUB003) with the assembly named, not as an unavailable artifact.
/// </summary>
internal sealed class PublishDependencyNotFoundException(string message) : FileNotFoundException(message)
{
    /// <summary>The refusal anywhere in the chain a load failure is wrapped in (FileLoadException, TypeLoadException, invocation).</summary>
    internal static PublishDependencyNotFoundException? Find(Exception? error)
    {
        for (var depth = 0; error != null && depth < 16; depth++, error = error.InnerException)
        {
            if (error is PublishDependencyNotFoundException refusal)
                return refusal;

            if (error is System.Reflection.ReflectionTypeLoadException types)
                return types.LoaderExceptions.Select(Find).FirstOrDefault(item => item != null);
        }

        return null;
    }
}

internal sealed record WorkflowCallee(WorkflowVersion Version, string DataType, DocumentLocation Location)
{
    internal IReadOnlyDictionary<string, ReadOnlyMemory<byte>> PrivateAssemblies { get; init; } =
        new Dictionary<string, ReadOnlyMemory<byte>>();

    internal WorkflowBundle? Bundle { get; init; }
    internal bool Inline { get; set; }
}

internal sealed class WorkflowCallees(IWorkflowCatalogStore catalog, IArtifactStore artifacts, WorkflowPublisherOptions options)
{
    internal async Task<IReadOnlyList<WorkflowCallee>> ResolveAsync(WorkflowDocument document, ResolvedWorkflowPackages packages,
        CancellationToken token)
    {
        var result = new Dictionary<(string, int), WorkflowCallee>();
        var requests = new Dictionary<(string, int?), WorkflowCallee>();

        void CheckInvocation(WorkflowVersion row, InvokeWorkflowStepDocument? step)
        {
            if (step == null || WorkflowContract.Parse(row.ContractJson).RequiredScopes.Count == 0)
                return;

            var identity = string.IsNullOrWhiteSpace(step.Id) ? step.Name ?? $"invokeWorkflow at line {step.Location.Line}" : step.Id;

            throw new WorkflowPublishException(WorkflowErrorKind.InvalidDefinition.ToString(),
                $"Workflow '{document.Workflow.Name}', step '{identity}' cannot invoke " +
                $"'{row.Name}' v{row.Version}: an independent child cannot supply required scopes", step.Location);
        }

        static void Use(WorkflowCallee callee, DocumentLocation location)
        {
            if (callee.Bundle == null)
                throw new WorkflowPublishException("PUB012",
                    $"Callee '{callee.Version.Name}@{callee.Version.Version}' has no published artifact; use useType for a code-defined workflow",
                    location);

            callee.Inline = true;
        }

        async Task<WorkflowCallee> Resolve(string name, int? version, DocumentLocation location,
            InvokeWorkflowStepDocument? invocation = null, bool inline = false)
        {
            token.ThrowIfCancellationRequested();

            if (requests.TryGetValue((name, version), out var existing))
            {
                CheckInvocation(existing.Version, invocation);

                if (inline)
                    Use(existing, location);

                return existing;
            }

            var row = version is { } pin
                ? await catalog.GetWorkflowVersionAsync(name, pin)
                : (await catalog.GetWorkflowVersionsAsync(name)).Where(row => row.State == WorkflowVersionState.Published)
                .OrderByDescending(row => row.Version).FirstOrDefault();

            if (row == null || row.State is not (WorkflowVersionState.Published or WorkflowVersionState.Deprecated))
                throw new WorkflowPublishException("STEP011",
                    $"Workflow '{name}' needs a resolved workflow contract and pinned version; the requested catalog version is unavailable",
                    location);

            CheckInvocation(row, invocation);

            if (inline && row.ArtifactDigest == null)
                throw new WorkflowPublishException("PUB012",
                    $"Callee '{row.Name}@{row.Version}' has no published artifact; use useType for a code-defined workflow", location);

            if (result.TryGetValue((row.Name, row.Version), out existing))
            {
                requests.Add((name, version), existing);

                if (inline)
                    Use(existing, location);

                return existing;
            }

            string dataType;
            WorkflowBundle? bundle = null;
            IReadOnlyDictionary<string, ReadOnlyMemory<byte>> privateAssemblies = new Dictionary<string, ReadOnlyMemory<byte>>();

            if (row.ArtifactDigest is { } digest)
            {
                await using var stream = await artifacts.OpenAsync(digest, token)
                    ?? throw new WorkflowPublishException("PUB009",
                        $"Callee '{row.Name}@{row.Version}' bundle is unavailable; republish the callee", location);

                try
                {
                    bundle = await WorkflowBundleArchive.ReadAsync(stream, digest, token, options.PackageReadLimits);
                }
                catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.ArtifactMismatch)
                {
                    throw new WorkflowPublishException("PUB009",
                        $"Callee '{row.Name}@{row.Version}' bundle is invalid; republish the callee", location, error);
                }

                var manifest = bundle.Manifest;

                if (manifest.Name != row.Name || manifest.Version != row.Version || manifest.StructureHash != row.StructureHash ||
                    manifest.Contract != row.ContractJson || manifest.ContractHash != row.ContractHash ||
                    manifest.EngineVersion != typeof(WorkflowDefinition).Assembly.GetName().Version!.ToString() ||
                    manifest.CheckpointContractVersion != WorkflowDefinitionValidator.CheckpointContractVersion ||
                    manifest.TargetFramework != options.TargetFramework || manifest.SharedAssemblies.Any(pair =>
                    {
                        var identity = new AssemblyName(pair.Key);
                        var shared = WorkflowSharedAssemblies.Identity(identity.Name!);

                        return shared?.FullName != identity.FullName || shared.Version?.ToString() != pair.Value;
                    }))
                    throw new WorkflowPublishException("PUB009",
                        $"Callee '{row.Name}@{row.Version}' is incompatible with the publishing profile; republish the callee", location);

                await new WorkflowPackageResolver(catalog, artifacts, options).UnionAsync(packages, manifest.ActivityPackages, location,
                    token);

                dataType = manifest.DataType;
                var privateMap = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);

                foreach (var bytes in bundle.Entries.Where(pair =>
                             pair.Key.StartsWith("private/", StringComparison.Ordinal) &&
                             pair.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value))
                {
                    var assemblyName = ResolvedWorkflowPackages.AssemblyIdentity(bytes).Name!;

                    if (privateMap.TryGetValue(assemblyName, out var previous) && !previous.Span.SequenceEqual(bytes.Span))
                        throw new WorkflowPublishException("PUB010",
                            $"Callee '{row.Name}@{row.Version}' contains conflicting private assembly '{assemblyName}'", location);

                    privateMap.TryAdd(assemblyName, bytes);
                }

                privateAssemblies = privateMap;
            }
            else
            {
                var definition = catalog is IWorkflowCatalog executable
                    ? await executable.GetWorkflowDefinitionAsync(row.Name, row.Version)
                    : null;

                if (definition?.WorkflowDataType is not { } type)
                    throw new WorkflowPublishException("PUB009",
                        $"Callee '{row.Name}@{row.Version}' has no available data type metadata; publish the callee or supply its code registration",
                        location);

                dataType = WorkflowTypeIdentity.SavedName(type);

                try
                {
                    privateAssemblies = CodePrivateAssemblies(type);
                }
                catch (Exception error) when (error is WorkflowEngineException { Kind: WorkflowErrorKind.ArtifactUnavailable }
                                                  or IOException or BadImageFormatException or UnauthorizedAccessException
                                                  or NotSupportedException)
                {
                    throw new WorkflowPublishException("PUB009",
                        $"Callee '{row.Name}@{row.Version}' executable DTO assembly closure is unavailable; publish the callee or supply its code registration",
                        location, error);
                }
            }

            var resolved = new WorkflowCallee(row, dataType, location) { PrivateAssemblies = privateAssemblies, Bundle = bundle };

            if (inline)
                Use(resolved, location);

            result.Add((row.Name, row.Version), resolved);
            requests.Add((name, version), resolved);

            return resolved;
        }

        var imports = new List<string>();

        // Resolve invocations before unused imports so a known scope requirement is
        // refused before looking for DTO assemblies or downloading a callee bundle.
        foreach (var step in WorkflowSourceTranslator
                     .Flatten(document.Definitions.Concat(document.Steps.Values.SelectMany(template => template.Steps)))
                     .OfType<InvokeWorkflowStepDocument>())
        {
            var callee = await Resolve(step.Workflow.Name, step.Workflow.Version, step.Workflow.Location, step);
            step.Workflow.Version = callee.Version.Version;
            imports.Add(callee.Version.Name + "@" + callee.Version.Version);
        }

        foreach (var step in WorkflowSourceTranslator
                     .Flatten(document.Definitions.Concat(document.Steps.Values.SelectMany(template => template.Steps)))
                     .OfType<UseStepDocument>())
        {
            var callee = await Resolve(step.Workflow.Name, step.Workflow.Version, step.Workflow.Location, inline: true);
            step.Workflow.Version = callee.Version.Version;
            imports.Add(callee.Version.Name + "@" + callee.Version.Version);
        }

        foreach (var import in document.Imports.Workflows)
        {
            var at = import.LastIndexOf('@');
            int? version = null;

            if (at >= 0)
            {
                if (!int.TryParse(import[(at + 1)..], out var parsed) || parsed < 1)
                    throw new WorkflowPublishException("STEP011", $"Invalid workflow import '{import}'", document.Imports.Location);

                version = parsed;
            }

            var callee = await Resolve(at < 0 ? import : import[..at], version, document.Imports.Location);
            imports.Add(callee.Version.Name + "@" + callee.Version.Version);
        }

        document.Imports.Workflows.Clear();
        document.Imports.Workflows.AddRange(imports.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        packages.Pins.Sort((left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));

        return result.Values.ToArray();
    }

    private static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> CodePrivateAssemblies(Type dataType)
    {
        var pending = new Queue<Assembly>();

        void Root(Type type)
        {
            if (type.HasElementType)
            {
                Root(type.GetElementType()!);

                return;
            }

            pending.Enqueue(type.Assembly);

            foreach (var argument in type.GetGenericArguments())
                Root(argument);
        }

        Root(dataType);
        var result = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);

        while (pending.TryDequeue(out var assembly))
        {
            var identity = assembly.GetName();

            if (WorkflowSharedAssemblies.IsShared(identity) || result.ContainsKey(identity.Name!))
                continue;

            if (assembly.IsDynamic || assembly.Location.Length == 0)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    $"Assembly '{identity.Name}' has no readable implementation");

            result.Add(identity.Name!, File.ReadAllBytes(assembly.Location));

            // Resolve through the executable type's context, never through caller-selected package references.
            var context = AssemblyLoadContext.GetLoadContext(assembly)!;

            foreach (var reference in assembly.GetReferencedAssemblies())
                if (!WorkflowSharedAssemblies.IsShared(reference))
                    pending.Enqueue(context.LoadFromAssemblyName(reference));
        }

        return result;
    }

    internal static void Bind(IReadOnlyList<WorkflowCallee> callees, WorkflowSourceOptions source,
        WorkflowCompilation.ReferenceSet references,
        ResolvedWorkflowPackages packages)
    {
        foreach (var callee in callees)
        {
            var row = callee.Version;
            var contract = WorkflowContract.Parse(row.ContractJson);

            var members = contract.Inputs.Concat(contract.Outputs).DistinctBy(member => member.Name).Select(member =>
                new WorkflowMemberDocumentContract(member.Name, contract.Inputs.Contains(member), contract.Outputs.Contains(member),
                    contract.Inputs.Contains(member), contract.Outputs.Contains(member), member.Required, member.HasDefault)).ToArray();

            string dataType;
            var assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                dataType = references.SavedTypeName(callee.DataType, assemblies);

                foreach (var member in contract.Inputs.Concat(contract.Outputs))
                    references.SavedTypeName(member.Type, assemblies);
            }
            catch (Exception error) when (error is WorkflowEngineException { Kind: WorkflowErrorKind.ArtifactMismatch } or ArgumentException
                                              or FormatException or OverflowException)
            {
                throw new WorkflowPublishException("PUB009",
                    $"Callee '{row.Name}@{row.Version}' data type is unavailable in the publishing profile; republish the callee",
                    callee.Location, error);
            }

            CheckPrivateAssemblies(callee, assemblies, references, packages);
            source.Types[dataType] = dataType;

            source.Workflows[row.Name + "@" + row.Version] = new(dataType, row.Version)
            {
                ContractJson = row.ContractJson, Contract = new(members),
                EntryType = callee.Inline ? callee.Bundle!.Manifest.EntryType : null,
                AssemblyName = callee.Inline ? WorkflowBundleIdentity.AssemblyName(row.Name, row.Version) : null,
                BundleDigest = callee.Inline ? row.ArtifactDigest!.Value.Value : null,
                StructureHash = row.StructureHash, ContractHash = row.ContractHash
            };
        }
    }

    private static void CheckPrivateAssemblies(WorkflowCallee callee, IEnumerable<string> roots,
        WorkflowCompilation.ReferenceSet references, ResolvedWorkflowPackages packages)
    {
        var selected = references.Paths.ToDictionary(path => AssemblyName.GetAssemblyName(path).Name!, StringComparer.OrdinalIgnoreCase);

        var runtimeAssemblies = packages.AllRuntimeAssemblies.GroupBy(bytes => ResolvedWorkflowPackages.AssemblyIdentity(bytes).Name!,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var pending = new Queue<string>(roots);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.TryDequeue(out var name))
        {
            if (!visited.Add(name))
                continue;

            var implementations = runtimeAssemblies.GetValueOrDefault(name);
            IEnumerable<ReadOnlyMemory<byte>> dependencies;

            if (callee.PrivateAssemblies.TryGetValue(name, out var bytes))
            {
                var identity = ResolvedWorkflowPackages.AssemblyIdentity(bytes);

                if (!selected.TryGetValue(name, out var path))
                    Refuse("is unavailable");
                else
                {
                    var referenceBytes = File.ReadAllBytes(path);

                    if (ResolvedWorkflowPackages.AssemblyIdentity(referenceBytes).FullName != identity.FullName)
                        Refuse("has a conflicting identity");

                    // A package reference may be metadata-only. Compare the implementation for every pinned RID.
                    implementations ??= [referenceBytes];

                    if (implementations.Any(item => !bytes.Span.SequenceEqual(item)))
                        Refuse("has conflicting contents");
                }

                dependencies = [bytes];
            }
            else if (implementations != null)
                dependencies = implementations.Select(item => (ReadOnlyMemory<byte>)item);
            else
                continue;

            // Package DTOs can lead to private dependencies, including through other packages.
            // Walk every pinned runtime implementation; reference assets can omit implementation dependencies.
            foreach (var dependency in dependencies)
            {
                using var pe = new PEReader(new MemoryStream(dependency.ToArray()));
                var metadata = pe.GetMetadataReader();

                foreach (var handle in metadata.AssemblyReferences)
                    pending.Enqueue(metadata.GetString(metadata.GetAssemblyReference(handle).Name));
            }

            void Refuse(string reason) => throw new WorkflowPublishException("PUB010",
                $"Callee '{callee.Version.Name}@{callee.Version.Version}' private DTO assembly '{name}' {reason} in the publishing profile; republish the callee",
                callee.Location);
        }
    }
}
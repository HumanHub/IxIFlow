using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;
using IxIFlow.Templates;

namespace IxIFlow.Authoring;

/// <summary>Publication-owned value metadata. Runtime types never leave the bounded scratch inspection.</summary>
internal sealed class WorkflowTemplates(IWorkflowCatalogStore catalog, TemplateEngines? engines, CancellationToken token)
{
    private readonly Dictionary<TemplateReference, TemplateVersion> _versions = [];
    private readonly List<WorkflowCallee> _callees = [];
    private readonly HashSet<TemplateReference> _newAuthoring = [];
    private readonly List<(RenderTemplateStepDocument Step, string Model)> _explicitModels = [];
    internal IEnumerable<TemplateVersion> Versions => _versions.Values;

    private void Add(TemplateVersion version)
    {
        if (_versions.TryGetValue(version.Reference, out var prior) && !TemplateJson.Serialize(prior).AsSpan().SequenceEqual(TemplateJson.Serialize(version)))
            throw new WorkflowPublishException("TPL020", $"Conflicting verified template payloads for '{version.Reference}'", null);
        _versions.TryAdd(version.Reference, version);
    }

    internal async Task ResolveDocumentAsync(WorkflowDocument document)
    {
        foreach (var step in WorkflowSourceTranslator.Flatten(document.Definitions.Concat(document.Steps.Values.SelectMany(item => item.Steps))).OfType<RenderTemplateStepDocument>())
        {
            var parts = (step.Template ?? "").Split('@');
            try { TemplateVersion.CheckKey(parts[0]); }
            catch (ArgumentException) { throw new WorkflowPublishException("TPL010", "Invalid template key", step.Location); }
            int? version = parts.Length == 2 && int.TryParse(parts[1], out var number) && number > 0 ? number : null;
            if (parts.Length > 2 || parts.Length == 2 && version == null)
                throw new WorkflowPublishException("TPL010", "Invalid template reference", step.Location);
            var selected = version is { } pin ? await catalog.GetTemplateVersionAsync(parts[0], pin) :
                (await catalog.GetTemplateVersionsAsync(parts[0])).OrderByDescending(item => item.Version.Version).FirstOrDefault();
            if (selected == null) throw new WorkflowPublishException("TPL010", $"Template '{step.Template}' is not published", step.Location);
            if (step.ModelType != null) _explicitModels.Add((step, step.ModelType));
            Add(selected.Version);
            _newAuthoring.Add(selected.Version.Reference);
            // Canonical source retains the inferred model and exact version, including reusable/graph bodies.
            step.Template = selected.Version.Key + "@" + selected.Version.Version;
            step.ModelType = selected.Version.ModelType;
        }
    }

    internal void Compose(IReadOnlyList<WorkflowCallee> callees)
    {
        foreach (var callee in callees.Where(item => item.Inline))
        {
            foreach (var version in WorkflowBundleTemplates.Read(callee.Bundle!).Versions) Add(version);
            _callees.Add(callee);
        }
    }

    internal void Bind(WorkflowSourceOptions options, WorkflowCompilation.ReferenceSet references)
    {
        foreach (var (step, declared) in _explicitModels)
        {
            var name = options.Types.TryGetValue(declared, out var mapped) ? mapped : declared;
            var type = references.BindType(name, options.Namespace);
            var expected = references.BindType(step.ModelType!, options.Namespace);
            if (type == null || !Microsoft.CodeAnalysis.SymbolEqualityComparer.Default.Equals(type, expected))
                throw new WorkflowPublishException("TPL011", "Explicit modelType differs from the selected template model", step.Location);
        }
        foreach (var version in Versions) options.Templates[version.Key + "@" + version.Version] = new(version.Key, version.Version, version.ModelType);
    }

    internal IReadOnlyList<WorkflowTemplateBinding> Inspect(WorkflowDefinition definition, ResolvedWorkflowPackages packages)
    {
        var validated = new HashSet<TemplateReference>();
        void Validate(TemplateReference reference, Type? model)
        {
            token.ThrowIfCancellationRequested();
            if (!_versions.TryGetValue(reference, out var version))
            {
                version = catalog.GetTemplateVersionAsync(reference.Key, reference.Version).GetAwaiter().GetResult()?.Version
                    ?? throw new WorkflowPublishException("TPL016", $"Scanned template '{reference}' is not published", null);
                Add(version);
                _newAuthoring.Add(reference);
            }
            foreach (var package in version.ModelPackages)
                if (packages.Pins.Any(pin => pin.Id == package.Id && (pin.Version != package.Version || pin.Digest != package.Digest)))
                    throw new WorkflowPublishException("TPL017", $"Template model closure conflicts with the bundle at '{package.Id}'", null);
            model ??= WorkflowTypeIdentity.Resolve(version.ModelTypeId);
            if (model == null) throw new WorkflowPublishException("TPL014", $"Template '{reference}' model is unavailable in the bundle", null);
            if (version.ModelTypeId != WorkflowTypeIdentity.SavedName(model) || version.ModelType != TemplateVersion.ModelTypeName(model))
                throw new WorkflowPublishException("TPL014", $"Template '{reference}' model identity differs from the render activity", null);
            if (!validated.Add(reference)) return;
            if (engines == null || engines.Count == 0) throw new WorkflowPublishException("TPL012", "Template engines are not registered", null);
            ITemplateEngine engine;
            try { engine = engines.Get(version.Engine); }
            catch (TemplateNotFoundException error)
            { throw new WorkflowPublishException("TPL001", $"Template engine '{version.Engine}' is not registered", null, error); }
            foreach (var variant in version.Variants)
                if (engine.ValidateAsync(TemplateSource.Of(version, variant), model, token).AsTask().GetAwaiter().GetResult().Count > 0)
                    throw new WorkflowPublishException("TPL015", $"Template '{reference}' is invalid for the bundled model", null);
        }
        var bindings = TemplateBindings.Scan(definition, Validate);
        foreach (var version in Versions.ToArray())
            if (!validated.Contains(version.Reference)) Validate(version.Reference, null);
        foreach (var callee in _callees)
        {
            var graft = WorkflowDefinitionValidator.GetInlineWorkflows(definition).SingleOrDefault(item => item.Name == callee.Version.Name && item.Version == callee.Version.Version);
            if (graft == null) continue;
            foreach (var site in graft.Sites)
            {
                var prefix = site + "/inline";
                if (callee.Bundle!.Manifest.TemplateBindings.Any(binding => !binding.Step.StartsWith("root/", StringComparison.Ordinal)))
                    throw new WorkflowPublishException("TPL020", "Callee template bindings must use its runtime catalog addresses", callee.Location);
                var expected = callee.Bundle!.Manifest.TemplateBindings.Select(binding => binding with
                    { Step = prefix + binding.Step[4..].Replace("/compensation/root/", "/compensation/" + prefix + "/", StringComparison.Ordinal) }).ToArray();
                var actual = bindings.Where(binding => binding.Step.StartsWith(prefix + "/", StringComparison.Ordinal)).ToArray();
                if (!TemplateBindings.Same(expected, actual))
                    throw new WorkflowPublishException("TPL020", "Rebuilt callee template bindings differ from its verified bundle", callee.Location);
            }
        }
        return bindings;
    }

    internal async Task<IReadOnlyList<ArtifactDigest>> CompletePackagesAsync(ResolvedWorkflowPackages packages,
        IArtifactStore artifacts, WorkflowPublisherOptions options, PackageAcquisition acquisition)
    {
        var demands = _newAuthoring.SelectMany(reference => _versions[reference].ModelPackages).Distinct().ToArray();
        foreach (var demand in demands)
        {
            var row = await catalog.GetPackageAsync(demand.Id, demand.Version);
            if (row?.Withdrawn == true) throw WorkflowPackageResolver.WithdrawnPackage(row.Id, row.Version);
            if (row == null || row.Digest != demand.Digest || demands.Any(other => other.Id == demand.Id && other != demand) ||
                packages.Pins.Any(pin => pin.Id == demand.Id && (pin.Version != demand.Version || pin.Digest != demand.Digest)))
                throw new WorkflowPublishException("TPL017", $"Conflicting template model package '{demand.Id}'", null);
        }
        var missing = demands.Where(demand => !packages.Pins.Any(pin => pin.Id == demand.Id)).ToArray();
        if (missing.Length > 0)
        {
            var resolver = new WorkflowPackageResolver(catalog, artifacts, options);
            using var selected = await resolver.ResolveAsync(missing.Select(item => item.Id + "@" + item.Version).ToList(), acquisition, [], token);
            try { await resolver.UnionAsync(packages, selected.Pins, new(1, 1), token); }
            catch (WorkflowPublishException error) when (error.Code == "PUB010")
            { throw new WorkflowPublishException("TPL017", "Template model closure conflicts with the bundle", error.Location, error); }
        }
        foreach (var demand in Versions.SelectMany(version => version.ModelPackages))
            if (!packages.Pins.Any(pin => pin.Id == demand.Id && pin.Version == demand.Version && pin.Digest == demand.Digest))
                throw new WorkflowPublishException("TPL017", $"Missing template model package pin '{demand.Id}'", null);
        return demands.Select(item => item.Digest).ToArray();
    }

    internal IReadOnlyList<WorkflowTemplatePin> Embed(Dictionary<string, byte[]> entries)
    {
        return Versions.OrderBy(item => item.Key, StringComparer.Ordinal).ThenBy(item => item.Version).Select(version =>
        {
            var path = $"templates/{version.Key}@{version.Version}.json";
            entries.Add(path, TemplateJson.Serialize(version));
            return new WorkflowTemplatePin(version.Key, version.Version, version.ModelTypeId, path);
        }).ToArray();
    }
}

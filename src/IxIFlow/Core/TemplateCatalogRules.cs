using IxIFlow.Templates;

namespace IxIFlow.Core;

internal static class TemplateCatalogRules
{
    internal static void Key(string key, int version)
    {
        WorkflowCatalogRules.ValidateIdentifier(key);
        _ = new TemplateReference(key, version);
    }

    internal static void Publication(WorkflowDraftId id, TemplateVersion version)
    {
        WorkflowPublicationRules.Id(id);

        if (id.Kind != WorkflowDraftKinds.Template || id.Name != version.Key || id.Version != version.Version)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Template and draft identities differ");
    }

    internal static async Task CheckPackagesAsync(TemplateVersion version, Func<string, string, Task<WorkflowPackage?>> find,
        IArtifactStore artifacts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        var references = version.ModelPackages.ToDictionary(item => item.Id, StringComparer.Ordinal);

        foreach (var reference in version.ModelPackages)
        {
            var package = await find(reference.Id, reference.Version);

            if (package == null || package.Digest != reference.Digest)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Template model package identity is unavailable");

            if (package.Withdrawn)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Template model package is withdrawn from new authoring");

            if (package.Closure == null || package.Closure.Any(item => !references.TryGetValue(item.Id, out var pin) || pin != item))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Template model package closure is incomplete");

            if (!await artifacts.ExistsAsync(reference.Digest, cancellationToken))
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Template model package artifact is unavailable");
        }
    }
}
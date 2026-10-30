using IxIFlow.Templates;

namespace IxIFlow.Core;

internal static class WorkflowBundleTemplates
{
    internal static TemplateSet Read(WorkflowBundle bundle)
    {
        try
        {
            return new(bundle.Manifest.Templates.Select(pin =>
            {
                var version = TemplateJson.Deserialize(bundle.Entries[pin.Entry].Span);

                if (version.Key != pin.Key || version.Version != pin.Version || version.ModelTypeId != pin.ModelTypeId ||
                    !TemplateJson.Serialize(version).AsSpan().SequenceEqual(bundle.Entries[pin.Entry].Span) ||
                    version.ModelPackages.Any(package => !bundle.Manifest.ActivityPackages.Any(item =>
                        item.Id == package.Id && item.Version == package.Version && item.Digest == package.Digest)))
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch,
                        "Bundled template identity, content or model closure differs from its pins");

                return version;
            }).ToArray());
        }
        catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Invalid bundled template payload", error);
        }
    }
}
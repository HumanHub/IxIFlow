using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;

namespace IxIFlow.Authoring;

public sealed record DocumentIdentity(string Kind, string Name, int Version, DocumentLocation Location);

/// <summary>Reads the identity of the one declared document root.</summary>
public static class WorkflowDocumentKinds
{
    public static DocumentIdentity Identify(string yaml)
    {
        try { WorkflowCatalogRules.ValidateText(yaml); }
        catch (ArgumentException) { throw new WorkflowSourceException("The document contains invalid text", "document", 1, 1); }
        var kind = WorkflowYamlSerializer.ReadRootKind(yaml);
        DocumentIdentity identity;
        switch (kind)
        {
            case WorkflowDraftKinds.Workflow:
                var workflow = WorkflowYamlSerializer.Read(yaml);
                identity = new(kind, workflow.Workflow.Name, workflow.Workflow.Version, workflow.Workflow.Location);
                break;
            case WorkflowDraftKinds.Activity:
                var activity = WorkflowYamlSerializer.ReadActivity(yaml);
                identity = new(kind, activity.Activity.Key, activity.Activity.Version, activity.Activity.Location);
                break;
            case WorkflowDraftKinds.Template:
                var template = WorkflowYamlSerializer.ReadTemplate(yaml);
                identity = new(kind, template.Template.Key, template.Template.Version, template.Template.Location);
                break;
            default:
                throw new WorkflowSourceException("Unsupported document kind", kind, 1, 1);
        }
        try { WorkflowPublicationRules.Key(identity.Kind, identity.Name, identity.Version); }
        catch (ArgumentException error) { throw new WorkflowSourceException(error.Message, kind, identity.Location.Line, identity.Location.Column); }
        return identity;
    }
}

using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>The scope step's authoring contract. Activity selection supplies its value and input/output metadata.</summary>
public static class ScopeStepDescriptors
{
    public static CatalogStepDescriptor Scope() => new("scope", "Scope", "Scopes", "box",
    [
        Member("Activity", typeof(string), "string", true),
        Member("ActivityVersion", typeof(string), "string", false),
        new("Defaults", null, null, false, "activity.Inputs"),
        new("Input", null, null, false, "activity.Inputs"),
        new("Output", null, null, false, "activity.Outputs"),
        Member("Retry", typeof(IxIFlow.Builders.RetryPolicy), "IxIFlow.Builders.RetryPolicy", false),
        Member("Threading", typeof(ScopeThreading?), "IxIFlow.Core.ScopeThreading?", false),
        Member("Steps", typeof(List<WorkflowStep>), "System.Collections.Generic.List<IxIFlow.Core.WorkflowStep>", false)
    ], []);

    private static StepMemberDescriptor Member(string name, Type type, string source, bool required) =>
        new(name, WorkflowTypeIdentity.SavedName(type), source, required, null);
}
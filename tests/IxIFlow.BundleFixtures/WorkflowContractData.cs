using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using IxIFlow.ActivitySdk;

namespace IxIFlow.BundleFixtures;

public sealed class WorkflowContractData
{
    [WorkflowInput, Required, DefaultValue("child default"), Display(Name = nameof(WorkflowContractResources.Label), Description = nameof(WorkflowContractResources.Help), ResourceType = typeof(WorkflowContractResources)), UIHint("text")]
    public string? Value { get; set; }

    [WorkflowOutput]
    public string? Result { get; set; }
}

public static class WorkflowContractResources
{
    public static string Label => System.Globalization.CultureInfo.CurrentUICulture.Name.Length == 0 ? "Value label" : "Localized label";
    public static string Help => System.Globalization.CultureInfo.CurrentUICulture.Name.Length == 0 ? "Value help" : "Localized help";
}

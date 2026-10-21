using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;

namespace IxIFlow.Tests.Infrastructure;

internal static class YamlTestContracts
{
    public static WorkflowSourceOptions Create(params (string Key, Type Type)[] types)
    {
        var options = new WorkflowSourceOptions { RequireResolvedTypes = true };
        foreach (var (key, type) in types) AddType(options, key, type);
        return options;
    }

    public static void AddType(WorkflowSourceOptions options, string key, Type type)
    {
        options.Types[key] = Name(type);
        foreach (var property in type.GetProperties())
            options.Members[key + "." + char.ToLowerInvariant(property.Name[0]) + property.Name[1..]] = property.Name;
    }

    public static void AddActivity(WorkflowSourceOptions options, ActivityDescriptor activity)
    {
        AddType(options, activity.Key, activity.ActivityType);
        options.Activities[activity.Key] = new(activity.Key, activity.Version, activity.PackageId, activity.PackageVersion);
        if (activity.PackageId.Length != 0 && activity.PackageVersion.Length != 0)
            options.ResolvedImports.Add(activity.PackageId + "@" + activity.PackageVersion);
    }

    public static Task<WorkflowValidationResult> ValidateAsync(WorkflowDocument document, WorkflowSourceOptions options) =>
        new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(options));

    public static string Name(Type type) => "global::" + type.FullName!.Replace('+', '.');
}

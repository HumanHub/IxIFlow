using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Tests.ExecutionTests;

internal static class YamlTestCompilation
{
    public static async Task<WorkflowDefinition> BuildAsync(WorkflowDocument document, WorkflowValidationContext context,
        IReadOnlyDictionary<string, Type>? activityTypes = null)
    {
        var validation = await new WorkflowDocumentValidator().ValidateAsync(document, context);
        if (!validation.IsValid)
            throw new WorkflowSourceException(string.Join("; ", validation.Diagnostics.Select(item => item.Message)), "test.yaml", 1, 1);
        var options = new WorkflowSourceOptions();
        var data = await context.DataTypeRegistry.FindAsync(document.Workflow.DataType);
        options.Types[document.Workflow.DataType] = Name(data!.ClrType);
        foreach (var property in data.ClrType.GetProperties())
            options.Members[document.Workflow.DataType + "." + char.ToLowerInvariant(property.Name[0]) + property.Name[1..]] = property.Name;
        foreach (var activity in document.Activities.Values) await Activity(activity.Activity);
        await Steps(document.Definitions);
        foreach (var template in document.Steps.Values) await Steps(template.Steps);
        return Build(WorkflowSourceTranslator.Translate(WorkflowYamlSerializer.Write(document), "test.yaml", options));

        async Task Activity(string key)
        {
            var descriptor = await context.ActivityRegistry.FindAsync(key);
            var type = activityTypes?.GetValueOrDefault(key) ?? descriptor?.ActivityType ?? typeof(YamlTestCompilation).Assembly.GetTypes().Single(type => type.Name == key);
            options.Types[key] = Name(type);
            if (descriptor != null)
            {
                options.ActivityVersions[key] = descriptor.Version;
                if (descriptor.PackageName.Length != 0 && descriptor.PackageVersion.Length != 0)
                    options.ResolvedImports.Add(descriptor.PackageName + "@" + descriptor.PackageVersion);
            }
        }
        async Task Steps(IEnumerable<WorkflowStepDocument> steps)
        {
            foreach (var step in steps)
                switch (step)
                {
                    case ActivityStepDocument activity: await Activity(activity.Activity); break;
                    case SequenceStepDocument sequence: await Steps(sequence.Steps); break;
                    case ConditionalStepDocument condition: await Steps(condition.Then); await Steps(condition.Else); break;
                    case ForEachStepDocument each: await Steps(each.Steps); break;
                    case SuspendStepDocument wait:
                        options.Types[wait.Event] = Name((await context.EventRegistry.FindAsync(wait.Event))!.EventType); break;
                    case InvokeWorkflowStepDocument invocation:
                        var child = (await context.WorkflowCatalog.FindAsync(invocation.Workflow.Name, invocation.Workflow.Version))!;
                        options.Workflows[child.Name + "@" + child.Version] = new(Name(child.RuntimeDefinition!.WorkflowDataType!), child.Version); break;
                }
        }
    }

    private static string Name(Type type) => "global::" + type.FullName!.Replace('+', '.');

    public static WorkflowDefinition Build(string source)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([typeof(WorkflowDefinition).Assembly.Location, typeof(IAsyncActivity).Assembly.Location,
                typeof(YamlTestCompilation).Assembly.Location]).Distinct(StringComparer.OrdinalIgnoreCase);
        var compilation = CSharpCompilation.Create("Yaml_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)], paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        stream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        try { return (WorkflowDefinition)assembly.GetTypes().Single(type => type.GetMethod("Build") != null)
            .GetMethod("Build")!.Invoke(null, null)!; }
        catch (TargetInvocationException error) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
    }
}

public sealed class YamlMappingActivity : IAsyncActivity
{
    public string Value { get; set; } = "";
    public string Result { get; set; } = "";
    public bool Flag { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    { Result = Value; return Task.CompletedTask; }
}
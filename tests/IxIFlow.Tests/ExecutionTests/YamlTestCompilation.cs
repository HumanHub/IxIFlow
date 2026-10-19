using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators;
using IxIFlow.Tests.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis;

namespace IxIFlow.Tests.ExecutionTests;

internal static class YamlTestCompilation
{
    public static async Task<WorkflowDefinition> BuildAsync(WorkflowDocument document, WorkflowSourceOptions options)
    {
        var validation = await YamlTestContracts.ValidateAsync(document, options);
        if (!validation.IsValid)
            throw new WorkflowSourceException(string.Join("; ", validation.Diagnostics.Select(item => item.Message)), "test.yaml", 1, 1);
        return Build(WorkflowSourceTranslator.Translate(WorkflowYamlSerializer.Write(document), "test.yaml", options));
    }

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
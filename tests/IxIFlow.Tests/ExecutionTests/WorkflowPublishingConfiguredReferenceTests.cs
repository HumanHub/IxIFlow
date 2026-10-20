using System.Diagnostics;
using IxIFlow.Authoring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-01")]
[Trait("Spec", "F-04")]
[Trait("Spec", "F-09")]
public sealed class WorkflowPublishingConfiguredReferenceTests
{
    [Theory]
    [InlineData("Types")]
    [InlineData("WorkflowDataTypes")]
    [InlineData("Workflows")]
    public async Task ConfiguredTypesUseTheEntryClosureAndRejectDynamicAssembliesColdAndWarm(string collection)
    {
        var directory = Path.Combine(Path.GetTempPath(), "IxIFlow.ConfiguredReferences", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var application = Emit(directory, "EntryApplication", """
                namespace EntryApplication;
                public class Data { }
                public class Echo : IxIFlow.Core.IAsyncActivity
                {
                    public System.Threading.Tasks.Task ExecuteAsync(IxIFlow.Core.IActivityContext context,
                        System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.CompletedTask;
                }
                """);
            var bridge = Emit(directory, "EntryBridge", "public class Bridge { public object NeverCalled() => new EntryApplication.Data(); }", [application]);
            Emit(directory, "DynamicPlugin", "namespace DynamicPlugin; public class Data { }");
            var host = Emit(directory, "ConfiguredPublisherHost", """
                using System;
                using System.IO;
                using System.Linq;
                using System.Reflection;
                using System.Runtime.Loader;
                using System.Threading.Tasks;
                using IxIFlow.Authoring;
                using IxIFlow.Core;
                using IxIFlow.Generators;
                public class Program
                {
                    public static object NeverCalled() => new Bridge();
                    public static async Task<int> Main(string[] args)
                    {
                        AssemblyLoadContext.Default.Resolving += (_, name) =>
                        {
                            var path = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, name.Name + ".dll");
                            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
                        };
                        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "EntryApplication" or "DynamicPlugin")) return 10;
                        var source = new WorkflowSourceOptions();
                        source.Types["Data"] = "global::EntryApplication.Data";
                        source.Types["Echo"] = "global::EntryApplication.Echo";
                        // Exercise all configured type collections inside the transitive entry closure.
                        source.WorkflowDataTypes["Child"] = "global::EntryApplication.Data";
                        source.Workflows["Child@1"] = new WorkflowSourceReference("global::EntryApplication.Data", 1);
                        var yaml = "workflow:\n  name: Configured\n  version: 1\n  dataType: Data\ndefinitions:\n  - kind: activity\n    activity: Echo\n";
                        var store = new InMemoryWorkflowStateRepository();
                        var draft = await store.SaveDefinitionAsync(new("Configured", 1, "workflow", yaml), 0);
                        var valid = await new WorkflowPublisher(store, store, new() { SourceOptions = source }).PublishAsync(draft.Id);
                        if (!valid.Success) throw new Exception(string.Join("; ", valid.Diagnostics));
                        if (valid.Version!.ManifestJson!.Contains("DynamicPlugin")) return 11;
                        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "DynamicPlugin")) return 12;

                        const string pluginType = "global::DynamicPlugin.Data";
                        switch (args[0])
                        {
                            case "Types": source.Types["Plugin"] = pluginType; break;
                            case "WorkflowDataTypes": source.WorkflowDataTypes["Plugin"] = pluginType; break;
                            case "Workflows": source.Workflows["Plugin@1"] = new WorkflowSourceReference(pluginType, 1); break;
                            default: return 13;
                        }
                        var rejectedStore = new InMemoryWorkflowStateRepository();
                        var rejected = await rejectedStore.SaveDefinitionAsync(new("Configured", 1, "workflow", yaml), 0);
                        var publisher = new WorkflowPublisher(rejectedStore, rejectedStore, new() { SourceOptions = source });
                        var cold = await publisher.PublishAsync(rejected.Id);
                        Assembly.Load("DynamicPlugin");
                        var warm = await publisher.PublishAsync(rejected.Id);
                        if (cold.Success || warm.Success || !cold.Diagnostics.SequenceEqual(warm.Diagnostics)) return 14;
                        var error = cold.Diagnostics.Single();
                        if (error.Code != "PUB007" || error.Stage != WorkflowPublishStage.Validate ||
                            !error.Message.Contains(pluginType) || !error.Message.Contains("host reference profile"))
                            throw new Exception(error.ToString());
                        if (await rejectedStore.GetDefinitionAsync("Configured", 1) != rejected ||
                            await rejectedStore.GetWorkflowVersionAsync("Configured", 1) != null) return 15;
                        return 0;
                    }
                }
                """, [bridge], OutputKind.ConsoleApplication);
            var testAssembly = typeof(WorkflowPublishingConfiguredReferenceTests).Assembly.Location;
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                         "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"), host, collection }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            Assert.True(process.ExitCode == 0, $"Host exit {process.ExitCode}: {await errors}; {await output}");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Emit(string directory, string name, string source, string[]? additional = null,
        OutputKind kind = OutputKind.DynamicallyLinkedLibrary)
    {
        var path = Path.Combine(directory, name + ".dll");
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
            WorkflowCompilation.ReferencePaths().Concat(additional ?? []).Select(item => MetadataReference.CreateFromFile(item)),
            new CSharpCompilationOptions(kind, optimizationLevel: OptimizationLevel.Release, deterministic: true));
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return path;
    }
}

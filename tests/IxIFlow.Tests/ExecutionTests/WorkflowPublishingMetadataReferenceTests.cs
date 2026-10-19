using System.Diagnostics;
using IxIFlow.Authoring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-01")]
[Trait("Spec", "F-04")]
[Trait("Spec", "F-09")]
public sealed class WorkflowPublishingMetadataReferenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishingWithSourceOptionsDoesNotLoadUnrelatedReferences(bool inferActivity)
    {
        var directory = Path.Combine(Path.GetTempPath(), "IxIFlow.MetadataReferences", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var host = Path.Combine(directory, "MetadataPublisherHost.dll");
            var compilation = CSharpCompilation.Create("MetadataPublisherHost", [CSharpSyntaxTree.ParseText("""
                using System;
                using System.IO;
                using System.Linq;
                using System.Reflection;
                using System.Runtime.Loader;
                using System.Threading;
                using System.Threading.Tasks;
                using IxIFlow.Authoring;
                using IxIFlow.Core;
                using IxIFlow.Generators;

                namespace MetadataPublisherHost;
                public class Contracts
                {
                    public class Nested
                    {
                        public class Data { }
                        public class Echo : IAsyncActivity
                        {
                            public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
                        }
                    }
                }

                public class Program
                {
                    public static async Task Main(string[] args)
                    {
                        const string data = "global::MetadataPublisherHost.Contracts.Nested.Data";
                        const string echo = "global::MetadataPublisherHost.Contracts.Nested.Echo";
                        var source = new WorkflowSourceOptions();
                        source.Types["Data"] = data;
                        source.Types[echo] = echo;
                        source.WorkflowDataTypes["Child"] = data;
                        source.Workflows["Child@1"] = new WorkflowSourceReference(data, 1);
                        var yaml = "workflow:\n  name: Metadata\n  version: 1\n  dataType: Data\ndefinitions:\n  - kind: activity\n    activity: " + echo + "\n";

                        // Warm the entry closure, compiler and workflow execution, without inspecting configured types.
                        var references = WorkflowCompilation.ReferencePaths();
                        WorkflowCompilation.Build(WorkflowSourceTranslator.Translate(yaml, "workflow.yaml", source));
                        if (args[0] == "infer") source.Types.Remove(echo);
                        var before = AssemblyLoadContext.Default.Assemblies.ToHashSet();
                        if (!references.Any(path => !before.Any(assembly => assembly.FullName == AssemblyName.GetAssemblyName(path).FullName)))
                            throw new Exception("The host must retain unloaded references for this regression check");

                        WorkflowCompilation.ReferencePaths(source);
                        CheckLoads(before, "configured type check");

                        var store = new InMemoryWorkflowStateRepository();
                        var draft = await store.SaveDefinitionAsync(new("Metadata", 1, "workflow", yaml), 0);
                        var result = await new WorkflowPublisher(store, store, new() { SourceOptions = source }).PublishAsync(draft.Id);
                        if (!result.Success) throw new Exception(string.Join("; ", result.Diagnostics));
                        CheckLoads(before, "publication");
                    }

                    private static void CheckLoads(System.Collections.Generic.HashSet<Assembly> before, string stage)
                    {
                        var loaded = AssemblyLoadContext.Default.Assemblies.Except(before).Select(assembly => assembly.FullName).ToArray();
                        if (loaded.Length != 0) throw new Exception(stage + " loaded unrelated assemblies: " + string.Join("; ", loaded));
                    }
                }
                """)], WorkflowCompilation.ReferencePaths().Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release, deterministic: true));
            var emitted = compilation.Emit(host);
            Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));

            var testAssembly = typeof(WorkflowPublishingMetadataReferenceTests).Assembly.Location;
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                         "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"), host, inferActivity ? "infer" : "configured" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            Assert.True(process.ExitCode == 0, $"Host exit {process.ExitCode}: {await errors}; {await output}");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

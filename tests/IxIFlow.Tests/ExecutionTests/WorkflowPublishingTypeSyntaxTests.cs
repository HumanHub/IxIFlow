using System.Diagnostics;
using IxIFlow.Authoring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-01")]
[Trait("Spec", "F-04")]
[Trait("Spec", "F-09")]
public sealed class WorkflowPublishingTypeSyntaxTests
{
    [Theory]
    [InlineData("Types")]
    [InlineData("WorkflowDataTypes")]
    [InlineData("Workflows")]
    public async Task ConfiguredCSharpTypesPublishAndMissingGenericArgumentsAreRejectedColdAndWarm(string collection)
    {
        var directory = Path.Combine(Path.GetTempPath(), "IxIFlow.TypeSyntax", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var application = Emit(directory, "EntryApplication", """
                public class RootData { }
                namespace EntryApplication
                {
                    public class Data { }
                    public class @event { }
                    public class Container<T> { public class Nested<U> { } }
                    public class Echo : IxIFlow.Core.IAsyncActivity
                    {
                        public System.Threading.Tasks.Task ExecuteAsync(IxIFlow.Core.IActivityContext context,
                            System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.CompletedTask;
                    }
                }
                """);
            var bridge = Emit(directory, "EntryBridge", "public class Bridge { public object NeverCalled() => new EntryApplication.Data(); }", [application]);
            Emit(directory, "DynamicPlugin", "namespace DynamicPlugin; public class Data { }");
            var host = Emit(directory, "TypeSyntaxPublisherHost", """
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
                    public static async Task Main(string[] args)
                    {
                        AssemblyLoadContext.Default.Resolving += (_, name) =>
                        {
                            var path = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, name.Name + ".dll");
                            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
                        };
                        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "EntryApplication" or "DynamicPlugin"))
                            throw new Exception("Expected a cold application/plugin load context");

                        const string yaml = "workflow:\n  name: TypeSyntax\n  version: 1\n  dataType: Data\ndefinitions:\n  - kind: activity\n    activity: global::EntryApplication.Echo\n";
                        var source = new WorkflowSourceOptions();
                        source.Types["Data"] = "global::EntryApplication.Data";
                        string[] valid = [
                            "global::System.Collections.Generic.List<global::EntryApplication.Data>",
                            "int", "string", "global::EntryApplication.Data[,][]", "global::System.DateTime?",
                            "global::EntryApplication.Data?", "RootData", "global::EntryApplication.@event",
                            "global::EntryApplication.Container<int>.Nested<global::EntryApplication.Data[]>",
                            "global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.List<global::EntryApplication.Data?[]>>",
                            "(int count, global::EntryApplication.Data value)", "dynamic", "nint", "nuint",
                            "Exception", "Tuple<int, global::EntryApplication.Data>"
                        ];
                        foreach (var configured in valid)
                        {
                            // Compile reference-type forms as the actual workflow data type, too.
                            // Value types remain configured aliases because workflow data has a class constraint.
                            source.Types["Data"] = configured.StartsWith("global::System.Collections.Generic.", StringComparison.Ordinal) ||
                                configured.StartsWith("global::EntryApplication.", StringComparison.Ordinal) ||
                                configured is "RootData" or "string" or "Exception" || configured.StartsWith("Tuple<", StringComparison.Ordinal)
                                ? configured : "global::EntryApplication.Data";
                            Configure(source, args[0], configured);
                            // Exercise the public path API as well as the publisher. Echo remains inferred,
                            // so publishing uses metadata in validation, resolution and compilation.
                            WorkflowCompilation.ReferencePaths(source);
                            var store = new InMemoryWorkflowStateRepository();
                            var draft = await store.SaveDefinitionAsync(new("TypeSyntax", 1, "workflow", yaml), 0);
                            var result = await new WorkflowPublisher(store, store, new() { SourceOptions = source }).PublishAsync(draft.Id);
                            if (!result.Success) throw new Exception(configured + ": " + string.Join("; ", result.Diagnostics));
                            if (result.Version!.ManifestJson!.Contains("DynamicPlugin")) throw new Exception("Plugin entered the profile");
                        }

                        const string missing = "global::DynamicPlugin.Data";
                        source.Types["Data"] = "global::EntryApplication.Data";
                        string[] invalid = [
                            "global::System.Collections.Generic.List<global::DynamicPlugin.Data>",
                            "global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.List<global::DynamicPlugin.Data?[]>>"
                        ];
                        var cold = new WorkflowPublishDiagnostic[invalid.Length];
                        for (var warm = 0; warm < 2; warm++)
                        {
                            if (warm == 0 && AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "DynamicPlugin"))
                                throw new Exception("Plugin was loaded before the cold check");
                            if (warm == 1) Assembly.Load("DynamicPlugin");
                            for (var i = 0; i < invalid.Length; i++)
                            {
                                Configure(source, args[0], invalid[i]);
                                var store = new InMemoryWorkflowStateRepository();
                                var draft = await store.SaveDefinitionAsync(new("TypeSyntax", 1, "workflow", yaml), 0);
                                var result = await new WorkflowPublisher(store, store, new() { SourceOptions = source }).PublishAsync(draft.Id);
                                if (result.Success) throw new Exception("Accepted an out-of-profile generic argument");
                                var error = result.Diagnostics.Single();
                                if (error.Code != "PUB007" || error.Stage != WorkflowPublishStage.Validate ||
                                    !error.Message.Contains("Configured type '" + invalid[i] + "'") ||
                                    !error.Message.Contains("component '" + missing + "'") ||
                                    !error.Message.Contains("host reference profile")) throw new Exception(error.ToString());
                                if (warm == 0) cold[i] = error;
                                else if (cold[i] != error) throw new Exception("Cold/warm diagnostics differ");
                                if (await store.GetDefinitionAsync("TypeSyntax", 1) != draft ||
                                    await store.GetWorkflowVersionAsync("TypeSyntax", 1) != null)
                                    throw new Exception("Failed publication changed the draft/version");
                            }
                        }
                    }

                    private static void Configure(WorkflowSourceOptions source, string collection, string value)
                    {
                        switch (collection)
                        {
                            case "Types": source.Types["Configured"] = value; break;
                            case "WorkflowDataTypes": source.WorkflowDataTypes["Child"] = value; break;
                            case "Workflows": source.Workflows["Child@1"] = new WorkflowSourceReference(value, 1); break;
                            default: throw new Exception(collection);
                        }
                    }
                }
                """, [bridge], OutputKind.ConsoleApplication);

            var testAssembly = typeof(WorkflowPublishingTypeSyntaxTests).Assembly.Location;
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                         "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"), host, collection }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
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
        var emitted = compilation.Emit(path);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
        return path;
    }
}

using System.Diagnostics;
using System.Reflection;
using IxIFlow.Authoring;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-01")]
[Trait("Spec", "F-04")]
[Trait("Spec", "F-09")]
public sealed class WorkflowPublishingReferenceRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateAssemblyIdentitiesProduceOneReference(bool differentBytes)
    {
        var directory = Folder();
        try
        {
            var first = Emit(directory, "first.dll", "Duplicate", "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")] public class Duplicate { }");
            var second = differentBytes
                ? Emit(directory, "second.dll", "Duplicate", "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")] public class Duplicate { public int Value; }")
                : Path.Combine(directory, "second.dll");
            if (!differentBytes) File.Copy(first, second);
            var paths = WorkflowCompilation.NormalizeReferences([first, second, first]);
            Assert.Single(paths);
            var shared = paths.Select(AssemblyName.GetAssemblyName).ToDictionary(name => name.FullName!, name => name.Version!.ToString());
            Assert.Single(shared);
            var compilation = WorkflowCompilation.Compile("public class Use { public Duplicate Value = new(); }",
                [.. WorkflowCompilation.ReferencePaths(), first, second]);
            Assert.True(compilation.Success, string.Join("; ", compilation.Diagnostics));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ConflictingAssemblyVersionsHaveAnExplicitDiagnostic()
    {
        var directory = Folder();
        try
        {
            var first = Emit(directory, "first.dll", "Duplicate", "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")] public class Duplicate { }");
            var second = Emit(directory, "second.dll", "Duplicate", "[assembly: System.Reflection.AssemblyVersion(\"2.0.0.0\")] public class Duplicate { }");
            var error = Assert.Throws<InvalidOperationException>(() => WorkflowCompilation.NormalizeReferences([first, second]));
            Assert.Equal("Conflicting reference versions for assembly 'Duplicate': 1.0.0.0, 2.0.0.0", error.Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ColdAndWarmHostsResolveTransitiveApplicationTypesAndPublishIdenticalBundles()
    {
        var directory = Folder();
        try
        {
            var application = Emit(directory, "ColdApplication.dll", "ColdApplication", """
                namespace ColdApplication;
                public class Data { public string OrderId { get; set; } = "value"; }
                public class Echo : IxIFlow.Core.IAsyncActivity
                {
                    public string Value { get; set; } = "";
                    public System.Threading.Tasks.Task ExecuteAsync(IxIFlow.Core.IActivityContext context,
                        System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.Task.CompletedTask;
                }
                """);
            var bridge = Emit(directory, "ColdBridge.dll", "ColdBridge",
                "public class Bridge { public static object NeverCalled() => new ColdApplication.Data(); }", [application]);
            Emit(directory, "AmbientOnly.dll", "AmbientOnly", "public class UnrelatedWarmupType { }");
            var host = Emit(directory, "ColdPublisherHost.dll", "ColdPublisherHost", """
                using System;
                using System.IO;
                using System.Linq;
                using System.Reflection;
                using System.Runtime.Loader;
                using System.Threading.Tasks;
                using IxIFlow.Authoring;
                using IxIFlow.Core;
                public class Program
                {
                    // Keep an indirect AssemblyRef without JITting a method using the application type.
                    public static object NeverCalled() => new Bridge();
                    public static async Task<int> Main(string[] args)
                    {
                        AssemblyLoadContext.Default.Resolving += (_, name) =>
                        {
                            var path = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, name.Name + ".dll");
                            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
                        };
                        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "ColdApplication")) return 10;
                        if (args.Length > 0) { Assembly.Load("ColdApplication"); Assembly.Load("AmbientOnly"); }
                        var yaml = "workflow:\n  name: Cold\n  version: 1\n  dataType: ColdApplication.Data\ndefinitions:\n  - kind: activity\n    activity: ColdApplication.Echo\n    input:\n      - target: Value\n        from: {kind: path, path: workflow.orderId}\n";
                        var store = new InMemoryWorkflowStateRepository();
                        var draft = await store.SaveDefinitionAsync(new("Cold", 1, "workflow", yaml), 0);
                        var result = await new WorkflowPublisher(store, store).PublishAsync(draft.Id);
                        if (!result.Success) { Console.Error.WriteLine(string.Join("; ", result.Diagnostics)); return 11; }
                        Console.WriteLine(result.Version!.ArtifactDigest);
                        Console.WriteLine(result.Version.ManifestJson);
                        return 0;
                    }
                }
                """, [bridge], OutputKind.ConsoleApplication);
            var cold = await RunHost(host, false);
            var warm = await RunHost(host, true);
            Assert.Equal(cold, warm);
            // The manifest names the application data type by its saved identifier, which carries its assembly.
            Assert.Contains("ixi-type:1:", cold);
            Assert.Contains("ColdApplication", cold);
            // The bridge is only in the publishing host's reference graph; the workflow never references it,
            // so the bundle neither carries nor declares it.
            Assert.DoesNotContain("ColdBridge", cold);
            Assert.DoesNotContain("AmbientOnly", cold);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<string> RunHost(string host, bool warm)
    {
        var testAssembly = typeof(WorkflowPublishingReferenceRegressionTests).Assembly.Location;
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                     "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"), host }) info.ArgumentList.Add(argument);
        if (warm) info.ArgumentList.Add("warm");
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 0, $"Host exit {process.ExitCode}: {await errors}; {await output}");
        return await output;
    }

    private static string Emit(string directory, string file, string name, string source, string[]? additional = null,
        OutputKind kind = OutputKind.DynamicallyLinkedLibrary)
    {
        var path = Path.Combine(directory, file);
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
            WorkflowCompilation.ReferencePaths().Concat(additional ?? []).Select(item => MetadataReference.CreateFromFile(item)),
            new CSharpCompilationOptions(kind, optimizationLevel: OptimizationLevel.Release, deterministic: true));
        var emitted = compilation.Emit(path);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
        return path;
    }

    private static string Folder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IxIFlow.PublishReferenceReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

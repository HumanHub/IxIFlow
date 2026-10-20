using System.IO.Compression;
using System.Text;
using IxIFlow.Authoring;
using IxIFlow.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>Real offline NuGet fixtures emitted with Roslyn and ZipArchive, without child processes.</summary>
public static class ActivityPackageFixture
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<(byte[] Activity, byte[] Dependency)>> Packages = new();

    public static (byte[] Activity, byte[] Dependency) Create(int version = 1) => Packages.GetOrAdd(version,
        v => new(() => Build(v))).Value;

    private static (byte[] Activity, byte[] Dependency) Build(int version)
    {
        var references = WorkflowCompilation.ReferencePaths().Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        (byte[] Runtime, byte[] Reference) Compile(string name, string source, IEnumerable<MetadataReference> refs)
        {
            var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using var implementation = new MemoryStream();
            var result = compilation.Emit(implementation);
            Assert.True(result.Success, string.Join("; ", result.Diagnostics));
            using var contract = new MemoryStream();
            result = compilation.Emit(contract, options: new EmitOptions(metadataOnly: true, includePrivateMembers: false));
            Assert.True(result.Success, string.Join("; ", result.Diagnostics));
            return (implementation.ToArray(), contract.ToArray());
        }
        var helper = Compile("Package.Dependency", $$"""
            [assembly: System.Reflection.AssemblyVersion("{{version}}.0.0.0")]
            namespace Packaged.Dependency;
            public static class Helper { public static string Echo(string text) => "package-v{{version}}:" + text; }
            """, references);
        var activity = Compile("Package.Activities", $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using System.ComponentModel.DataAnnotations;
            using IxIFlow.Core;
            using IxIFlow.ActivitySdk;
            [assembly: System.Reflection.AssemblyVersion("{{version}}.0.0.0")]
            namespace Packaged;
            public sealed class Data { public string Value { get; set; } = ""; public string Result { get; set; } = ""; }
            public sealed class Signal { public string Value { get; set; } = ""; }
            [WorkflowActivity("fixture.echo", Version="1.0")]
            public sealed class Echo : IAsyncActivity
            {
                [ActivityInput, Required] public string Text { get; set; } = "";
                [ActivityOutput] public string Result { get; private set; } = "";
                public string Unmarked { get; set; } = "";
                public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
                { Result = Packaged.Dependency.Helper.Echo(Text); return Task.CompletedTask; }
            }
            """, references.Append(MetadataReference.CreateFromImage(helper.Reference)));
        return (Package("fixture.activities", version + ".0.0", new()
        {
            ["ref/net10.0/Package.Activities.dll"] = activity.Reference,
            ["lib/net10.0/Package.Activities.dll"] = activity.Runtime
        }, $"<dependency id=\"fixture.dependency\" version=\"[{version}.0.0]\" />"),
            Package("fixture.dependency", version + ".0.0", new()
            {
                ["ref/net10.0/Package.Dependency.dll"] = helper.Reference,
                ["lib/net10.0/Package.Dependency.dll"] = helper.Runtime
            }));
    }

    public static byte[] Package(string id, string version, Dictionary<string, byte[]> entries, string dependencies = "")
    {
        entries.Add(id + ".nuspec", Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
            <id>{id}</id><version>{version}</version><authors>IxIFlow.Tests</authors><description>Offline test fixture</description>
            <dependencies><group targetFramework="net10.0">{dependencies}</group></dependencies>
            </metadata></package>
            """));
        using var result = new MemoryStream();
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, bytes) in entries)
            {
                var entry = zip.CreateEntry(path);
                entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        return result.ToArray();
    }

    public static byte[] NativeActivityPackage()
    {
        var extension = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        var source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using System.Runtime.InteropServices;
            using IxIFlow.Core;
            using IxIFlow.ActivitySdk;
            namespace NativeFixture;
            public sealed class Data
            {
                [DllImport("fixture_native", EntryPoint="fixture_value")]
                private static extern int GetValue();
                public int NativeValue => GetValue();
                public string Value { get; set; } = "";
                public string Result { get; set; } = "";
            }
            [WorkflowActivity("fixture.native-call", Version="1.0")]
            public sealed class Echo : IAsyncActivity
            {
                [DllImport("fixture_native", EntryPoint="fixture_value")]
                private static extern int GetValue();
                [ActivityInput] public string Text { get; set; } = "";
                [ActivityOutput] public string Result { get; private set; } = "";
                public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
                { Result = Text + ":" + (GetValue() == 42); return Task.CompletedTask; }
            }
            """;
        var emitted = WorkflowCompilation.Compile(source);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
        return Package("fixture.native-call", "1.0.0", new()
        {
            ["lib/net10.0/NativeCall.dll"] = emitted.Assembly,
            ["runtimes/" + System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier + "/native/fixture_native" + extension] = NativeLibraryBytes(),
            ["runtimes/test-other-platform/native/not-selected.bin"] = NativeLibraryBytes()
        });
    }

    public static byte[] NativeLibraryBytes(string name = "fixture_native", string? rid = null)
    {
        rid ??= System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        if (rid is not ("win-x64" or "linux-x64" or "osx-arm64"))
            throw Xunit.Sdk.SkipException.ForSkip($"Prebuilt native activity fixtures are available only for win-x64, linux-x64 and osx-arm64; current RID is {rid}");
        using var stream = typeof(ActivityPackageFixture).Assembly.GetManifestResourceStream(
            "IxIFlow.TestSupport.NativeFixtures." + rid + "." + name + (rid == "win-x64" ? ".dll" : rid == "osx-arm64" ? ".dylib" : ".so"))!;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    }

    public static byte[] LowerReferencePackage(string framework)
    {
        string root;
        if (framework == "netstandard2.0")
            root = Path.Combine(AppContext.BaseDirectory, "PackageDownloads", "netstandard.library", "2.0.3", "build", framework, "ref");
        else
            root = Path.Combine(AppContext.BaseDirectory, "PackageDownloads", "microsoft.netcore.app.ref", "8.0.0", "ref", framework);
        var references = Directory.EnumerateFiles(root, "*.dll").Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Lower.Reference." + framework,
            [CSharpSyntaxTree.ParseText("public sealed class LowerReference { public System.Collections.Generic.List<string> Values { get; } = new(); public string Value { get; set; } = \"\"; public string Result { get; set; } = \"\"; }")], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
        var package = Package("fixture.lower", "1.0.0", new() { ["lib/" + framework + "/Lower.Reference.dll"] = output.ToArray() },
            framework == "netstandard2.0" ? "<dependency id=\"NETStandard.Library\" version=\"[2.0.3,)\" />" : "");
        // The nuspec's dependency framework must match the actual compile target.
        using var input = new System.IO.Compression.ZipArchive(new MemoryStream(package));
        var entries = input.Entries.ToDictionary(entry => entry.FullName, entry =>
        { using var source = entry.Open(); using var bytes = new MemoryStream(); source.CopyTo(bytes); return bytes.ToArray(); });
        var nuspec = entries.Keys.Single(path => path.EndsWith(".nuspec", StringComparison.Ordinal));
        var text = Encoding.UTF8.GetString(entries[nuspec]).Replace("targetFramework=\"net10.0\"", "targetFramework=\"" + framework + "\"");
        entries.Remove(nuspec);
        using var result = new MemoryStream();
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var pair in entries.Append(new(nuspec, Encoding.UTF8.GetBytes(text))))
            { using var stream = zip.CreateEntry(pair.Key).Open(); stream.Write(pair.Value); }
        return result.ToArray();
    }

    public static byte[] SharedAssemblyConflictPackage()
    {
        var references = WorkflowCompilation.ReferencePaths().Where(path => System.Reflection.AssemblyName.GetAssemblyName(path).Name != "IxIFlow.Abstractions")
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        var shared = CSharpCompilation.Create("IxIFlow.Abstractions",
            [CSharpSyntaxTree.ParseText("[assembly: System.Reflection.AssemblyVersion(\"99.0.0.0\")] namespace FakeShared; public class Token { }")], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var sharedBytes = new MemoryStream();
        var emitted = shared.Emit(sharedBytes);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
        var privateAssembly = CSharpCompilation.Create("Conflicting.Activity",
            [CSharpSyntaxTree.ParseText("public class RequiresShared { public FakeShared.Token Value { get; set; } }")],
            references.Append(MetadataReference.CreateFromImage(sharedBytes.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var bytes = new MemoryStream();
        emitted = privateAssembly.Emit(bytes);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
        return Package("fixture.assembly-conflict", "1.0.0", new() { ["lib/net10.0/Conflicting.Activity.dll"] = bytes.ToArray() });
    }

    public static string Yaml(int version = 1, string? extraImport = null) => $$"""
        workflow:
          name: PackageLifecycle
          version: {{version}}
          dataType: Packaged.Data
        imports:
          catalogs:
            - fixture.activities@{{version}}.0.0
        {{(extraImport == null ? "" : "    - " + extraImport)}}
        definitions:
          - kind: suspend
            id: signal
            reason: continue
            event: Packaged.Signal
            output:
              - source: Value
                to: {kind: path, path: workflow.Value}
          - kind: activity
            id: echo
            activity: fixture.echo
            activityVersion: "1.0"
            input:
              - target: Text
                from: {kind: path, path: workflow.Value}
            output:
              - source: Result
                to: {kind: path, path: workflow.Result}
        """;

    public static async Task<WorkflowVersion> PublishAsync(IWorkflowStateRepository store, int version = 1,
        WorkflowPublisherOptions? options = null, bool upload = true, string? yaml = null)
    {
        var publisher = new WorkflowPublisher(store, store, options);
        if (upload)
        {
            var packages = Create(version);
            await publisher.UploadPackageAsync(new MemoryStream(packages.Activity));
            await publisher.UploadPackageAsync(new MemoryStream(packages.Dependency));
        }
        var draft = await store.SaveDefinitionAsync(new("PackageLifecycle", version, "workflow", yaml ?? Yaml(version)), 0);
        var result = await publisher.PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return result.Version!;
    }

    public static async Task CrossHostAsync(Func<IWorkflowStateRepository> repository, string root,
        Action<IServiceCollection>? configure = null, Func<IServiceProvider, Task>? startLinks = null)
    {
        var store = repository();
        var version = await PublishAsync(store);
        string id;
        await using (var host = PublishedBundleScenario.Host(store, Path.Combine(root, "first"), configure: configure))
        {
            if (startLinks != null) await startLinks(host);
            id = await host.GetRequiredService<IWorkflowClient>().StartAsync(version.Name, 1, new { Value = "before" });
            await PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Suspended);
        }
        if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
        var next = repository();
        await using (var host = PublishedBundleScenario.Host(next, Path.Combine(root, "second"), configure: configure))
        {
            if (startLinks != null) await startLinks(host);
            var catalog = host.GetRequiredService<IWorkflowCatalog>();
            using var definition = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1);
            var assembly = definition.Definition.WorkflowDataType!.Assembly;
            Assert.NotEqual(System.Runtime.Loader.AssemblyLoadContext.Default, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly));
            var signal = Activator.CreateInstance(assembly.GetType("Packaged.Signal")!)!;
            signal.GetType().GetProperty("Value")!.SetValue(signal, "resumed");
            var result = await host.GetRequiredService<IWorkflowClient>().ResumeAsync(id, signal);
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal("package-v1:resumed", result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));
            Assert.Equal(version.ArtifactDigest, (await next.GetWorkflowInstanceAsync(id))!.BundleDigest);
        }
        if (next is IAsyncDisposable nextDisposable) await nextDisposable.DisposeAsync();
    }
}

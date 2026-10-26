using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security;
using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Generators;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

// SESSION ONLY: every fact here starts dotnet processes. Do not run in the Codex sandbox.
[Trait("Feature", "authoring")]
[Trait("Spec", "F-16")]
[Trait("Spec", "F-19")]
[Trait("Category", "PackageIntegration")]
public sealed class ActivityPublishingConsumerProcessTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task CleanProjectRestoresBuildsAndRegistersThePublishedNupkg(bool http)
    {
        using var folder = new ActivityPublishingTests.TemporaryFolder();
        var sdk = await SdkFeed.GetAsync();
        var store = new InMemoryWorkflowStateRepository();
        var key = "acme.consumer-" + Guid.NewGuid().ToString("N").Replace('0', 'a').Replace('1', 'b').Replace('2', 'c').Replace('3', 'd').Replace('4', 'e').Replace('5', 'f').Replace('6', 'g').Replace('7', 'h').Replace('8', 'i').Replace('9', 'j');
        var source = ActivityPublishingTests.Source.Replace("acme.transform", key);
        var package = await ActivityPublishingTests.PublishActivity(store, source);
        var bytes = await ActivityPublishingTests.Bytes(store, package.Digest);
        var feed = Path.Combine(folder.Path, "published"); Directory.CreateDirectory(feed);
        await File.WriteAllBytesAsync(Path.Combine(feed, key + ".2.0.0.nupkg"), bytes);
        await using var server = new ActivityConsumerNuGetFeed(store);
        var fixtureSource = http ? server.Index : feed;
        var output = await BuildConsumer(folder.Path, sdk, fixtureSource, key, "2.0.0", executableProbe: true);
        // AddActivities requires default-context code. The fixture key/assembly is unique
        // per test, and the consumer references only package assets, never projects.
        _ = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(await File.ReadAllBytesAsync(Path.Combine(output, key + ".dll"))));
        var consumer = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(await File.ReadAllBytesAsync(Directory.GetFiles(output, "Consumer_*.dll").Single())));
        var run = (Task<int>)consumer.GetType("Consumer.Probe")!.GetMethod("Execute")!.Invoke(null, null)!;
        Assert.Equal(6, await run);
        if (http)
        {
            Assert.Contains($"/flat/{key}/index.json", server.Requests);
            Assert.Contains($"/flat/{key}/2.0.0/{key}.2.0.0.nupkg", server.Requests);
        }
    }

    [Fact]
    public async Task WithdrawnDirectAndTransitiveDependenciesRestoreFromFreshPackageAndHttpCaches()
    {
        using var folder = new ActivityPublishingTests.TemporaryFolder();
        var sdk = await SdkFeed.GetAsync();
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        var dependency = ActivityPackageFixture.Package("fixture.consumer-dependency", "1.0.0", new()
        { ["lib/net10.0/Consumer.Dependency.dll"] = ActivityPublishingTests.Emit("Consumer.Dependency", "public static class ConsumerDependency { public static int Value => 7; }") });
        await publisher.UploadPackageAsync(new MemoryStream(dependency));
        var source = ActivityPublishingTests.Source.Replace("acme.transform", "acme.withdrawn-consumer")
            .Replace("input:", "packages: [fixture.consumer-dependency@1.0.0]\ninput:")
            .Replace("Result = Value * 2;", "Result = ConsumerDependency.Value;");
        var direct = await ActivityPublishingTests.PublishActivity(store, source);
        var transitive = (await store.GetPackageAsync("fixture.consumer-dependency", "1.0.0"))!;
        foreach (var package in new[] { direct, transitive })
            await store.SetPackageWithdrawnAsync(package.Id, package.Version, true, new(package.LifecycleRevision), "withdrawer");
        await using var feed = new ActivityConsumerNuGetFeed(store);
        // Both caches start empty; fixture packages are mapped exclusively to this HTTP source.
        var output = await BuildConsumer(folder.Path, sdk, feed.Index, direct.Id, direct.Version, executableProbe: false);
        Assert.Single(Directory.GetFiles(output, "Consumer_*.dll"));
        using var client = new HttpClient();
        foreach (var package in new[] { direct, transitive })
        {
            var indexPath = $"/flat/{package.Id}/index.json";
            using var index = JsonDocument.Parse(await client.GetByteArrayAsync(feed.Base + indexPath));
            Assert.Contains(index.RootElement.GetProperty("versions").EnumerateArray(), value => value.GetString() == package.Version);
            var path = $"/flat/{package.Id}/{package.Version}/{package.Id}.{package.Version}.nupkg";
            Assert.Contains(path, feed.Requests);
            Assert.Equal(await ActivityPublishingTests.Bytes(store, package.Digest), await client.GetByteArrayAsync(feed.Base + path));
        }
        var report = await publisher.ValidateAsync(new("WithdrawnConsumer", 1, "workflow", ActivityPublishingTests.ConsumerYaml("WithdrawnConsumer", direct.Id)));
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "PUB025");
        var transitiveImport = ActivityPublishingTests.Source.Replace("acme.transform", "acme.new-consumer")
            .Replace("input:", "packages: [fixture.consumer-dependency@1.0.0]\ninput:");
        var refused = await publisher.ValidateAsync(new("acme.new-consumer", 2, "activity", transitiveImport));
        Assert.Contains(refused.Diagnostics, diagnostic => diagnostic.Code == "PUB025");
    }

    // The consumer harness accepts a source URL so P4 PR-4 can run the same proof
    // against its policy/scoped production routes. This slice's server is a fixture.
    private static async Task<string> BuildConsumer(string root, SdkFeed sdk, string fixtureSource, string key, string version, bool executableProbe)
    {
        var project = Path.Combine(root, "consumer"); Directory.CreateDirectory(project);
        var consumerName = "Consumer_" + key.Replace('.', '_').Replace('-', '_');
        var config = Path.Combine(root, "NuGet.Config");
        await File.WriteAllTextAsync(config, "<configuration><packageSources><clear/>" +
            $"<add key=\"published\" value=\"{SecurityElement.Escape(fixtureSource)}\" allowInsecureConnections=\"true\"/>" +
            string.Concat(sdk.Sources.Select((path, index) => $"<add key=\"sdk{index}\" value=\"{SecurityElement.Escape(path)}\"/>")) +
            "</packageSources><packageSourceMapping><packageSource key=\"published\"><package pattern=\"acme.*\"/><package pattern=\"fixture.*\"/></packageSource>" +
            string.Concat(sdk.Sources.Select((_, index) => $"<packageSource key=\"sdk{index}\"><package pattern=\"*\"/></packageSource>")) +
            "</packageSourceMapping></configuration>");
        await File.WriteAllTextAsync(Path.Combine(project, "Consumer.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>{{consumerName}}</AssemblyName><CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies></PropertyGroup>
              <ItemGroup><PackageReference Include="{{key}}" Version="[{{version}}]"/><PackageReference Include="IxIFlow" Version="[1.0.0]"/></ItemGroup>
            </Project>
            """);
        var type = new ActivityPackageIdentity(key, int.Parse(version.Split('.')[0])).CSharpTypeName;
        var code = ActivityConsumerSource.Create(type, executableProbe);
        await File.WriteAllTextAsync(Path.Combine(project, "Consumer.cs"), code);
        var cache = Path.Combine(root, "packages"); var httpCache = Path.Combine(root, "http-cache");
        Assert.False(Directory.Exists(cache)); Assert.False(Directory.Exists(httpCache));
        await Dotnet(project, cache, httpCache, "restore", "--configfile", config, "--no-cache", "--force", "-p:NuGetAudit=false");
        await Dotnet(project, cache, httpCache, "build", "--no-restore", "-p:UseSharedCompilation=false");
        using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(project, "obj/project.assets.json")));
        Assert.Contains(assets.RootElement.GetProperty("libraries").EnumerateObject(), entry => entry.Name == key + "/" + version);
        return Path.Combine(project, "bin/Debug/net10.0");
    }

    private static async Task Dotnet(string directory, string packages, string httpCache, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["NUGET_PACKAGES"] = packages;
        start.Environment["NUGET_HTTP_CACHE_PATH"] = httpCache;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', arguments)} failed:\n{await stdout}\n{await stderr}");
    }

    private sealed record SdkFeed(string[] Sources)
    {
        private static string RepositoryRoot([CallerFilePath] string source = "") => Path.GetFullPath("../../..", Path.GetDirectoryName(source)!);
        private static readonly Lazy<Task<SdkFeed>> Shared = new(Create);
        internal static Task<SdkFeed> GetAsync() => Shared.Value;
        private static async Task<SdkFeed> Create()
        {
            var repository = new DirectoryInfo(RepositoryRoot());
            Assert.True(File.Exists(Path.Combine(repository.FullName, "IxIFlow.sln")));
            var root = Path.Combine(Path.GetTempPath(), "IxIFlow.ActivityConsumerSdk", Guid.NewGuid().ToString("N"));
            var feed = Path.Combine(root, "feed"); Directory.CreateDirectory(feed);
            string? assetsPath = null;
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "obj/IxIFlow.Tests/project.assets.json");
                if (File.Exists(candidate)) { assetsPath = candidate; break; }
            }
            assetsPath ??= Path.Combine(repository.FullName, "tests/IxIFlow.Tests/obj/project.assets.json");
            using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(assetsPath));
            var caches = assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(folder => folder.Name).ToArray();
            var sources = new[] { feed }.Concat(caches).ToArray();
            var config = Path.Combine(root, "NuGet.Config");
            await File.WriteAllTextAsync(config, "<configuration><packageSources><clear/>" + string.Concat(sources.Select((path, index) => $"<add key=\"local{index}\" value=\"{SecurityElement.Escape(path)}\"/>")) + "</packageSources></configuration>");
            foreach (var project in new[] { "IxIFlow.Abstractions", "IxIFlow" })
                await Dotnet(repository.FullName, Path.Combine(root, "packages"), Path.Combine(root, "http"), "pack", Path.Combine(repository.FullName, "src", project), "--output", feed,
                    "--artifacts-path", Path.Combine(root, "artifacts"), "-p:PackageVersion=1.0.0", "-p:RestoreConfigFile=" + config, "-p:NuGetAudit=false", "-p:GeneratePackageOnBuild=false", "-p:UseSharedCompilation=false");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } };
            return new(sources);
        }
    }
}

// Minimal NuGet V3 consumer fixture, reusable by the later feed-route gate.
// It intentionally includes available withdrawn identities in its content index.
internal sealed class ActivityConsumerNuGetFeed : IAsyncDisposable
{
    private readonly IWorkflowStateRepository _store;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    internal ConcurrentBag<string> Requests { get; } = [];
    internal string Base { get; }
    internal string Index => Base + "/index.json";
    internal ActivityConsumerNuGetFeed(IWorkflowStateRepository store)
    {
        _store = store; _listener.Start(); Base = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Serve();
    }
    private async Task Serve()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
                var request = await reader.ReadLineAsync(_stop.Token);
                for (var line = await reader.ReadLineAsync(_stop.Token); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync(_stop.Token)) { }
                var path = new Uri(Base + request!.Split(' ')[1]).AbsolutePath;
                Requests.Add(path);
                byte[]? content = null;
                if (path == "/index.json") content = JsonSerializer.SerializeToUtf8Bytes(new { version = "3.0.0", resources = new[] { new Dictionary<string, string> { ["@id"] = Base + "/flat/", ["@type"] = "PackageBaseAddress/3.0.0" } } });
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts is ["flat", var id, "index.json"])
                {
                    var versions = new List<string>();
                    foreach (var package in await _store.GetPackagesAsync(id)) if (await _store.ExistsAsync(package.Digest)) versions.Add(package.Version);
                    content = JsonSerializer.SerializeToUtf8Bytes(new { versions });
                }
                if (parts is ["flat", var packageId, var version, var file] && file == packageId + "." + version + ".nupkg" && await _store.GetPackageAsync(packageId, version) is { } found)
                    content = await ActivityPublishingTests.Bytes(_store, found.Digest);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(content == null ? "404 Not Found" : "200 OK")}\r\nContent-Length: {content?.Length ?? 0}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, _stop.Token);
                if (content != null) await stream.WriteAsync(content, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _loop; _stop.Dispose(); }
}

internal static class ActivityConsumerSource
{
    internal static string Create(string type, bool executableProbe) => executableProbe ? $$"""
            using System;
            using System.Threading.Tasks;
            using IxIFlow.Builders;
            using IxIFlow.Core;
            using IxIFlow.Extensions;
            using Microsoft.Extensions.DependencyInjection;
            namespace Consumer;
            public sealed class Data { public int Result { get; set; } }
            public static class Probe {
              public static async Task<int> Execute() {
                var services = new ServiceCollection();
                services.AddIxIFlow().AddActivities(typeof({{type}}).Assembly);
                await using var provider = services.BuildServiceProvider();
                var definition = Workflow.Create<Data>("FluentPackageConsumer").Step<{{type}}>(step => step.Output(activity => activity.Result).To(context => context.WorkflowData.Result)).Build();
                var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
                if (result.Status == WorkflowExecutionStatus.Running) result = await provider.GetRequiredService<IWorkflowClient>().AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(15));
                if (result.Status != WorkflowExecutionStatus.Success) throw new Exception(result.Status.ToString());
                return ((Data)result.WorkflowData!).Result;
              }
            }
            """ : $$"""
            namespace Consumer;
            public static class CompileProof { public static System.Type Activity => typeof({{type}}); }
            """;
}

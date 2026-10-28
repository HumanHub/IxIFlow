using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security;
using System.Security.Claims;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IxIFlow.AspNetCore;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Generators;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.Conformance;

// SESSION ONLY. Each row starts dotnet pack/restore/build child processes. Never run in the Codex sandbox.
[Trait("Feature", "authoring"), Trait("Spec", "F-19"), Trait("Category", "PackageIntegration")]
public sealed class NuGetFeedRestoreProcessTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task ExistingConsumerRestoresAndBuildsWithdrawnDirectOrTransitiveDependencyWithFreshCaches(bool transitive)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.HttpRestore", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var store = new InMemoryWorkflowStateRepository(); var publisher = new WorkflowPublisher(store, store);
        var leafAssembly = PublishingFeed.Emit("HttpRestore.Leaf", "public static class HttpLeaf { public static int Value => 7; }");
        var leafBytes = PublishingFeed.Package("fixture.http.leaf", new() { ["lib/net10.0/HttpRestore.Leaf.dll"] = leafAssembly });
        var leaf = (await publisher.UploadPackageAsync(new MemoryStream(leafBytes))).Stored;
        // Use the same standalone activity/compiler/package identity and consumer shape as P1 PR-C.
        var source = PublishingHttpScenarios.ActivityYaml.Replace("body:", "packages: [fixture.http.leaf@1.0.0]\nbody:")
            .Replace("Result = 7;", "Result = HttpLeaf.Value;");
        var draft = await store.SaveDefinitionAsync(new("acme.http", 1, "activity", source), null);
        var published = await publisher.PublishAsync(draft.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
        var root = published.Package!;
        byte[] rootBytes;
        await using (var stream = await store.OpenAsync(root.Digest))
        { using var bytes = new MemoryStream(); await stream!.CopyToAsync(bytes); rootBytes = bytes.ToArray(); }
        var requests = new ConcurrentQueue<string>();
        using var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders()).ConfigureWebHostDefaults(web =>
            web.UseKestrel().UseUrls("http://127.0.0.1:0").ConfigureServices(services =>
            {
                var options = new WorkflowPublishingOptions
                {
                    ReadPolicy = "feed", AuthorPolicy = "feed", ExecutePolicy = "feed", PreviewPolicy = "feed",
                    PublishPolicy = "feed", LifecyclePolicy = "feed", DownloadPolicy = "feed"
                };
                services.AddIxIFlow().AddWorkflowPublishing(options);
                services.RemoveAll<IHostedService>();
                services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(store));
                services.AddSingleton<ICatalogQueryScope>(new PublishingHttpFactory.ScopeProvider());
                services.AddAuthorization(options => options.AddPolicy("feed", policy => policy.RequireAuthenticatedUser()));
            }).Configure(app =>
            {
                // This loopback-only fixture supplies a principal; production route filters and resource policies still run.
                app.Use(async (context, next) =>
                {
                    requests.Enqueue(context.Request.Path);
                    context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "restore-consumer")], "fixture"));
                    await next(context);
                });
                app.UseRouting(); app.UseEndpoints(endpoints => endpoints.MapWorkflowPublishing("/pub"));
            })).Build();
        try
        {
            await host.StartAsync();
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var index = address + "/pub/nuget/index.json";
            var sdkSources = await SdkSources(folder);
            var config = Path.Combine(folder, "NuGet.Config");
            // More-specific fixture mappings make HTTP their only source; SDK packages use P1's offline SDK feed/cache sources.
            await File.WriteAllTextAsync(config, "<configuration><packageSources><clear/><add key=\"http\" value=\"" +
                SecurityElement.Escape(index) + "\" allowInsecureConnections=\"true\"/>" +
                string.Concat(sdkSources.Select((path, number) => $"<add key=\"sdk{number}\" value=\"{SecurityElement.Escape(path)}\"/>")) + "</packageSources>" +
                "<fallbackPackageFolders><clear/></fallbackPackageFolders><packageSourceMapping><clear/><packageSource key=\"http\"><package pattern=\"acme.http\"/><package pattern=\"fixture.http.*\"/></packageSource>" +
                string.Concat(sdkSources.Select((_, number) => $"<packageSource key=\"sdk{number}\"><package pattern=\"*\"/></packageSource>")) + "</packageSourceMapping></configuration>");
            var direct = root.Id;
            var consumer = Path.Combine(folder, "consumer"); Directory.CreateDirectory(consumer);
            await File.WriteAllTextAsync(Path.Combine(consumer, "Consumer.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies></PropertyGroup>
                  <ItemGroup><PackageReference Include="{{direct}}" Version="[1.0.0]"/><PackageReference Include="IxIFlow" Version="[1.0.0]"/></ItemGroup>
                </Project>
                """);
            var activityType = new ActivityPackageIdentity(root.Id, 1).CSharpTypeName;
            await File.WriteAllTextAsync(Path.Combine(consumer, "Consumer.cs"), "public static class Consumer { public static System.Type Activity => typeof(" + activityType + "); public static int Value => new " + activityType + "().Result; }");
            // Establish the existing consumer, then repeat after withdrawal with separate empty caches.
            await RestoreBuild("before");
            var withdrawn = transitive ? leaf : root;
            await store.SetPackageWithdrawnAsync(withdrawn.Id, withdrawn.Version, true, new(withdrawn.LifecycleRevision), "owner");
            requests.Clear();
            Directory.Delete(Path.Combine(consumer, "bin"), recursive: true);
            Directory.Delete(Path.Combine(consumer, "obj"), recursive: true);
            await RestoreBuild("after");
            Assert.True(File.Exists(Path.Combine(consumer, "bin/Debug/net10.0/Consumer.dll")));
            Assert.True(File.Exists(Path.Combine(consumer, "bin/Debug/net10.0/HttpRestore.Leaf.dll")));
            foreach (var (id, expected) in new[] { (leaf.Id, leafBytes), (root.Id, rootBytes) })
            {
                var path = $"/pub/nuget/flatcontainer/{id}/1.0.0/{id}.1.0.0.nupkg";
                Assert.Contains($"/pub/nuget/flatcontainer/{id}/index.json", requests);
                Assert.Contains(path, requests);
                Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(folder, "after-packages", id, "1.0.0", id + ".1.0.0.nupkg")));
                using var client = new HttpClient();
                Assert.Equal(expected, await client.GetByteArrayAsync(address + path));
                using var versions = JsonDocument.Parse(await client.GetByteArrayAsync(address + $"/pub/nuget/flatcontainer/{id}/index.json"));
                Assert.Contains(versions.RootElement.GetProperty("versions").EnumerateArray(), item => item.GetString() == "1.0.0");
            }
            using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(consumer, "obj/project.assets.json")));
            Assert.True(assets.RootElement.GetProperty("libraries").TryGetProperty(leaf.Id + "/1.0.0", out _));
            Assert.True(assets.RootElement.GetProperty("libraries").TryGetProperty(root.Id + "/1.0.0", out _));
            var refused = await publisher.ValidateAsync(new("Refused", 1, "workflow", $"workflow: {{name: Refused, dataType: Missing}}\nimports: {{catalogs: [{direct}@1.0.0]}}\ndefinitions: []"));
            Assert.Contains(refused.Diagnostics, item => item.Code == "PUB025");

            async Task RestoreBuild(string phase)
            {
                var packages = Path.Combine(folder, phase + "-packages"); var http = Path.Combine(folder, phase + "-http");
                Assert.False(Directory.Exists(packages)); Assert.False(Directory.Exists(http));
                await Dotnet(consumer, packages, http, "restore", "--configfile", config, "--no-cache", "--force", "-p:NuGetAudit=false", "-p:RestoreIgnoreFailedSources=false");
                await Dotnet(consumer, packages, http, "build", "--no-restore", "-p:UseSharedCompilation=false");
            }
        }
        finally { await host.StopAsync(); Directory.Delete(folder, recursive: true); }
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") => Path.GetFullPath("../..", Path.GetDirectoryName(source)!);
    private static async Task<string[]> SdkSources(string folder)
    {
        var repository = RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(repository, "IxIFlow.sln")));
        string? assetsPath = null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "obj/IxIFlow.Conformance/project.assets.json");
            if (File.Exists(candidate)) { assetsPath = candidate; break; }
        }
        assetsPath ??= Path.Combine(repository, "tests/IxIFlow.Conformance/obj/project.assets.json");
        using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(assetsPath));
        var feed = Path.Combine(folder, "sdk-feed"); Directory.CreateDirectory(feed);
        var sources = new[] { feed }.Concat(assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(item => item.Name)).ToArray();
        var config = Path.Combine(folder, "sdk.NuGet.Config");
        await File.WriteAllTextAsync(config, "<configuration><packageSources><clear/>" + string.Concat(sources.Select((path, index) =>
            $"<add key=\"local{index}\" value=\"{SecurityElement.Escape(path)}\"/>")) + "</packageSources></configuration>");
        foreach (var project in new[] { "IxIFlow.Abstractions", "IxIFlow" })
            await Dotnet(repository, Path.Combine(folder, "sdk-packages"), Path.Combine(folder, "sdk-http"), "pack", Path.Combine(repository, "src", project),
                "--output", feed, "--artifacts-path", Path.Combine(folder, "sdk-artifacts"), "-p:PackageVersion=1.0.0", "-p:RestoreConfigFile=" + config,
                "-p:NuGetAudit=false", "-p:GeneratePackageOnBuild=false", "-p:UseSharedCompilation=false");
        return sources;
    }

    private static async Task Dotnet(string folder, string packages, string http, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["NUGET_PACKAGES"] = packages; start.Environment["NUGET_HTTP_CACHE_PATH"] = http;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', arguments)} failed:\n{await stdout}\n{await stderr}");
    }
}

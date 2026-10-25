using System.Diagnostics;
using System.Security;
using System.Text.Json;

namespace IxIFlow.Generators.Tests;

/// <summary>One immutable, offline package feed per test process; consumers keep separate caches.</summary>
internal sealed record PackageTestFeed(string Feed, string Config, string Version)
{
    // ExecutionAndPublication locks initialization, including concurrent requests from the three classes.
    private static readonly Lazy<Task<PackageTestFeed>> Shared = new(CreateAsync,
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static Task<PackageTestFeed> GetAsync() => Shared.Value;

    private static async Task<PackageTestFeed> CreateAsync()
    {
        var root = WorkflowSchemaFiles.RepositoryRoot();
        var temporary = Path.Combine(Path.GetTempPath(), "IxIFlow.SharedPackageFeed", Guid.NewGuid().ToString("N"));
        var feed = Path.Combine(temporary, "feed");
        var config = Path.Combine(temporary, "NuGet.Config");
        var version = "1.0.0-package." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(feed);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        try
        {
            using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,
                "tests/IxIFlow.Generators.Tests/obj/project.assets.json")));
            var caches = assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(folder => folder.Name);
            await File.WriteAllTextAsync(config, "<configuration><packageSources><clear/>" +
                string.Concat(new[] { feed }.Concat(caches).Select((path, index) =>
                    $"<add key=\"local{index}\" value=\"{SecurityElement.Escape(path)}\" />")) +
                "</packageSources></configuration>");
            foreach (var project in new[] { "IxIFlow.Abstractions", "IxIFlow" })
            {
                string[] arguments = ["pack", Path.Combine(root, "src", project), "--output", feed,
                    "--artifacts-path", Path.Combine(temporary, "artifacts"), "-p:PackageVersion=" + version,
                    "-p:RestoreConfigFile=" + config, "-p:NuGetAudit=false",
                    "-p:GeneratePackageOnBuild=false", "-p:UseSharedCompilation=false"];
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true
                };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                start.Environment["NUGET_PACKAGES"] = Path.Combine(temporary, "packages");
                start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
                var output = await stdout + await stderr;
                Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', arguments)} failed:\n{output}");
            }
            return new(feed, config, version);
        }
        catch { Cleanup(); throw; }

        void Cleanup()
        {
            try { Directory.Delete(temporary, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using System.Text.Json;

namespace IxIFlow.Generators.Tests;

public sealed class WorkflowSchemaPackageTests
{
    [Fact]
    [Trait("Category", "PackageIntegration")]
    public async Task PackageShipsTheSchemaAndConsumersResolveItsPath()
    {
        var root = WorkflowSchemaFiles.RepositoryRoot();
        var temporary = Path.Combine(Path.GetTempPath(), "IxIFlow.SchemaPackage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var feed = Path.Combine(temporary, "feed");
            var artifacts = Path.Combine(temporary, "artifacts");
            var config = Path.Combine(temporary, "NuGet.Config");
            Directory.CreateDirectory(feed);
            using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,
                "tests/IxIFlow.Generators.Tests/obj/project.assets.json")));
            var caches = assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(folder => folder.Name);
            await File.WriteAllTextAsync(config, "<configuration><packageSources><clear/>" +
                string.Concat(new[] { feed }.Concat(caches).Select((path, index) =>
                    $"<add key=\"local{index}\" value=\"{SecurityElement.Escape(path)}\" />")) +
                "</packageSources></configuration>");
            var version = "1.0.0-schema." + Guid.NewGuid().ToString("N");
            foreach (var project in new[] { "IxIFlow.Abstractions", "IxIFlow" })
                await Dotnet(root, "pack", Path.Combine(root, "src", project), "--output", feed,
                    "--artifacts-path", artifacts, "-p:PackageVersion=" + version,
                    "-p:RestoreConfigFile=" + config, "-p:NuGetAudit=false",
                    "-p:GeneratePackageOnBuild=false", "-p:UseSharedCompilation=false");
            using (var package = ZipFile.OpenRead(Path.Combine(feed, $"IxIFlow.{version}.nupkg")))
            {
                var schema = Assert.Single(package.Entries, entry => entry.FullName == "schemas/workflow.schema.json");
                using var bytes = new MemoryStream();
                await using (var content = schema.Open()) await content.CopyToAsync(bytes);
                Assert.Equal(await File.ReadAllBytesAsync(WorkflowSchemaFiles.SchemaPath), bytes.ToArray());
            }

            // A consumer that only references the packed package resolves the property to the restored copy.
            var consumer = Path.Combine(temporary, "consumer");
            Directory.CreateDirectory(consumer);
            await File.WriteAllTextAsync(Path.Combine(consumer, "Consumer.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="IxIFlow" Version="{{version}}" />
                  </ItemGroup>
                </Project>
                """);
            await Dotnet(consumer, "restore", "--configfile", config, "-p:NuGetAudit=false");
            var property = (await Dotnet(consumer, "msbuild", "-getProperty:IxIFlowWorkflowSchema")).Trim();
            Assert.Equal(Path.Combine(temporary, "packages", "ixiflow", version, "schemas", "workflow.schema.json"), property);
            Assert.Equal(await File.ReadAllBytesAsync(WorkflowSchemaFiles.SchemaPath), await File.ReadAllBytesAsync(property));
        }
        finally { Directory.Delete(temporary, recursive: true); }

        async Task<string> Dotnet(string workingDirectory, params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
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
            return output;
        }
    }
}

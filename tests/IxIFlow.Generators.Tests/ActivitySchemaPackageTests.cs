using System.Diagnostics;
using System.IO.Compression;

namespace IxIFlow.Generators.Tests;

[Trait("Feature", "documents")]
[Trait("Spec", "F-19")]
public sealed class ActivitySchemaPackageTests
{
    [Fact]
    [Trait("Category", "PackageIntegration")]
    public async Task PackageShipsActivitySchemaAndIgnoresActivityFilesByConvention()
    {
        var root = WorkflowSchemaFiles.RepositoryRoot();
        var temporary = Path.Combine(Path.GetTempPath(), "IxIFlow.SchemaPackage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);

        try
        {
            var packed = await PackageTestFeed.GetAsync();
            var feed = packed.Feed;
            var config = packed.Config;
            var version = packed.Version;

            using (var package = ZipFile.OpenRead(Path.Combine(feed, $"IxIFlow.{version}.nupkg")))
            {
                var schema = Assert.Single(package.Entries, entry => entry.FullName == "schemas/activity.schema.json");
                using var bytes = new MemoryStream();

                await using (var content = schema.Open())
                    await content.CopyToAsync(bytes);

                Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(root, "src/IxIFlow/schemas/activity.schema.json")), bytes.ToArray());
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

            await File.WriteAllTextAsync(Path.Combine(consumer, "ignored.activity.yaml"),
                "activity: {key: acme.test, version: 1}\nbody: |\n  ThisWouldNotCompile();");

            await File.WriteAllTextAsync(Path.Combine(consumer, "Valid.workflow.yaml"),
                "workflow: {name: Valid, dataType: Data}\ndefinitions: []\n");

            await File.WriteAllTextAsync(Path.Combine(consumer, "Consumer.cs"),
                "public sealed class Data {}\npublic static class Control { public static IxIFlow.Core.WorkflowDefinition Build() => Consumer.Generated.Workflow_Valid_workflow.Build(); }\n");

            await Dotnet(consumer, "restore", "--configfile", config, "-p:NuGetAudit=false");
            await Dotnet(consumer, "build", "--no-restore");
            var property = (await Dotnet(consumer, "msbuild", "-getProperty:IxIFlowActivitySchema")).Trim();
            Assert.Equal(Path.Combine(temporary, "packages", "ixiflow", version, "schemas", "activity.schema.json"), property);

            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(root, "src/IxIFlow/schemas/activity.schema.json")),
                await File.ReadAllBytesAsync(property));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }

        async Task<string> Dotnet(string workingDirectory, params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };

            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);

            start.Environment["NUGET_PACKAGES"] = Path.Combine(temporary, "packages");
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);

                throw;
            }

            var output = await stdout + await stderr;
            Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', arguments)} failed:\n{output}");

            return output;
        }
    }
}
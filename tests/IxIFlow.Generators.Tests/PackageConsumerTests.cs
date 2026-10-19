using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json;

namespace IxIFlow.Generators.Tests;

public sealed class PackageConsumerTests
{
    [Fact]
    [Trait("Category", "PackageIntegration")]
    public async Task PackedWorkflowsAreDiscoveredAndExecutedOffline()
    {
        var root = RepositoryRoot();
        var temporary = Path.Combine(Path.GetTempPath(), "IxIFlow.PackageIntegration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var feed = Path.Combine(temporary, "feed");
            var artifacts = Path.Combine(temporary, "artifacts");
            var config = Path.Combine(temporary, "NuGet.Config");
            Directory.CreateDirectory(feed);
            // A restored solution already has every external dependency in these caches. Use them
            // as feeds, with no network source and an isolated consumer package cache.
            using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,
                "tests/IxIFlow.Generators.Tests/obj/project.assets.json")));
            var caches = assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(folder => folder.Name).ToArray();
            await File.WriteAllTextAsync(config, "<configuration><packageSources><clear/>" +
                string.Concat(new[] { feed }.Concat(caches).Select((path, index) =>
                    $"<add key=\"local{index}\" value=\"{SecurityElement.Escape(path)}\" />")) +
                "</packageSources></configuration>");
            var version = "1.0.0-yaml." + Guid.NewGuid().ToString("N");
            foreach (var project in new[] { "IxIFlow.Abstractions", "IxIFlow" })
                await Dotnet(root, "pack", Path.Combine(root, "src", project), "--output", feed,
                    "--artifacts-path", artifacts, "-p:PackageVersion=" + version,
                    "-p:RestoreConfigFile=" + config, "-p:NuGetAudit=false",
                    "-p:GeneratePackageOnBuild=false", "-p:UseSharedCompilation=false");

            using (var package = ZipFile.OpenRead(Path.Combine(feed, $"IxIFlow.{version}.nupkg")))
            {
                Assert.Equal(new[]
                {
                    "analyzers/dotnet/cs/IxIFlow.Analyzers.CodeFixes.dll",
                    "analyzers/dotnet/cs/IxIFlow.Analyzers.dll",
                    "analyzers/dotnet/cs/IxIFlow.Generators.dll",
                    "analyzers/dotnet/cs/YamlDotNet.dll"
                }, package.Entries.Where(entry => entry.FullName.StartsWith("analyzers/", StringComparison.Ordinal))
                    .Select(entry => entry.FullName).Order(StringComparer.Ordinal));
                Assert.Contains(package.Entries, entry => entry.FullName == "buildTransitive/IxIFlow.props");
                Assert.Contains(package.Entries, entry => entry.FullName == "build/IxIFlow.props");
            }

            // No project references: this fresh project sees only the packed NuGet assets.
            var consumer = Path.Combine(temporary, "consumer");
            Directory.CreateDirectory(consumer);
            await File.WriteAllTextAsync(Path.Combine(consumer, "Consumer.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <RootNamespace>Consumer</RootNamespace>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="IxIFlow" Version="{{version}}" />
                    <IxIFlowWorkflow Include="custom.yaml" />
                    <AdditionalFiles Include="./custom.yaml;docker-compose.yaml" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(consumer, "Orders.workflow.yaml"), Workflow("Orders", 7));
            await File.WriteAllTextAsync(Path.Combine(consumer, "custom.yaml"), Workflow("Custom", 11));
            await File.WriteAllTextAsync(Path.Combine(consumer, "docker-compose.yaml"), "services: {web: {image: nginx}}\n");
            await File.WriteAllTextAsync(Path.Combine(consumer, "Program.cs"), Program);
            foreach (var directory in new[] { "bin", "obj" })
            {
                Directory.CreateDirectory(Path.Combine(consumer, directory));
                await File.WriteAllTextAsync(Path.Combine(consumer, directory, "ignored.workflow.yaml"), "invalid: [");
            }
            await Dotnet(consumer, "restore", "--configfile", config, "-p:NuGetAudit=false");
            await Dotnet(consumer, "build", "--no-restore", "-p:UseSharedCompilation=false");
            Assert.Contains("Orders=7;Custom=11", await Dotnet(consumer, "run", "--no-build"));

            using (var items = JsonDocument.Parse(await Dotnet(consumer, "msbuild", "-target:IncludeIxIFlowWorkflowFiles", "-getItem:AdditionalFiles")))
            {
                var customFiles = items.RootElement.GetProperty("Items").GetProperty("AdditionalFiles").EnumerateArray()
                    .Where(item => Path.GetFileName(item.GetProperty("FullPath").GetString()) == "custom.yaml").ToArray();
                Assert.Single(customFiles);
                Assert.Equal("true", customFiles[0].GetProperty("IxIFlowWorkflow").GetString());
            }

            // Design-time builds use the same AdditionalFiles metadata before the compiler runs.
            await Dotnet(consumer, "msbuild", "-target:GenerateMSBuildEditorConfigFile", "-p:DesignTimeBuild=true");
            var editorConfig = await File.ReadAllTextAsync(Path.Combine(consumer, "obj/Debug/net10.0/Consumer.GeneratedMSBuildEditorConfig.editorconfig"));
            Assert.Contains("Orders.workflow.yaml", editorConfig);
            Assert.Contains("build_property.RootNamespace = Consumer", editorConfig);
            Assert.Contains("build_metadata.AdditionalFiles.IxIFlowWorkflow = true", editorConfig);

            await Dotnet(consumer, "build", "--no-restore", "-p:IxIFlowIncludeWorkflowFiles=false", "-p:UseSharedCompilation=false");
            Assert.Contains("Orders=absent;Custom=11", await Dotnet(consumer, "run", "--no-build", "--", "disabled"));
            // EnableDefaultItems disables the automatic workflow glob too; explicit items still apply.
            await File.WriteAllTextAsync(Path.Combine(consumer, "Directory.Build.targets"),
                "<Project><ItemGroup Condition=\"'$(EnableDefaultItems)' == 'false'\"><Compile Include=\"Program.cs\" /></ItemGroup></Project>");
            await Dotnet(consumer, "build", "--no-restore", "-p:EnableDefaultItems=false", "-p:UseSharedCompilation=false");
            Assert.Contains("Orders=absent;Custom=11", await Dotnet(consumer, "run", "--no-build", "--", "disabled"));

            async Task<string> Dotnet(string workingDirectory, params string[] arguments)
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = workingDirectory, RedirectStandardOutput = true,
                    RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
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
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));

    private static string Workflow(string name, int value) => $$"""
        workflow:
          name: {{name}}
          dataType: Data
        definitions:
          - kind: activity
            activity: SetValue
            input:
              - target: Value
                from: {kind: constant, value: {{value}}}
            output:
              - source: Value
                to: {path: workflow.Value}
        """;

    private const string Program = """
        using IxIFlow;
        using IxIFlow.Core;
        using IxIFlow.Extensions;
        using Microsoft.Extensions.DependencyInjection;
        using System.Reflection;

        var disabled = args.Contains("disabled");
        var orders = Assembly.GetExecutingAssembly().GetType("Consumer.Generated.Workflow_Orders_workflow");
        if ((orders == null) != disabled) throw new Exception("Unexpected Orders generation");
        if (Assembly.GetExecutingAssembly().GetType("Consumer.Generated.Workflow_docker_compose") != null)
            throw new Exception("Docker Compose was generated");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow(options => options.ReplyTokenKeys = new ReplyTokenKeyRing(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        async Task<int> Execute(WorkflowDefinition definition)
        {
            var result = await client.RunAsync(definition, new Data());
            if (result.Status == WorkflowExecutionStatus.Running)
                result = await client.AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
            if (result.Status != WorkflowExecutionStatus.Success) throw new Exception(result.ErrorMessage);
            return ((Data)result.WorkflowData!).Value;
        }
        var custom = await Execute(Consumer.Generated.Workflow_custom.Build());
        var orderValue = orders == null ? "absent" : (await Execute((WorkflowDefinition)orders.GetMethod("Build")!.Invoke(null, null)!)).ToString();
        if (custom != 11 || !disabled && orderValue != "7") throw new Exception("Unexpected workflow result");
        Console.WriteLine($"Orders={orderValue};Custom={custom}");

        public sealed class Data { public int Value { get; set; } }
        public sealed class SetValue : IAsyncActivity
        {
            public int Value { get; set; }
            public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
        """;
}

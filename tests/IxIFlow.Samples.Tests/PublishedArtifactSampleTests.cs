using System.Runtime.Loader;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Samples;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Samples.Tests;

[Trait("Feature", "authoring")]
[Trait("Spec", "F-12"), Trait("Spec", "F-13"), Trait("Spec", "F-16"), Trait("Spec", "F-19")]
public sealed class PublishedActivitySampleTests
{
    [Fact]
    public async Task PublishedNupkgFeedsFluentAndColdYamlConsumersAndCanBeUploaded()
    {
        using var scratch = new SampleScratch();
        var store = new InMemoryWorkflowStateRepository();
        var (package, workflow) = await PublishedActivities.PublishAsync(store);
        Assert.Equal("acme.double", package.Id);
        Assert.Equal("1.0.0", package.Version);
        Assert.Equal(WorkflowPackageOrigin.Published, package.Origin);
        Assert.NotNull(package.PublishedDraft);

        await using var archive = (await store.OpenAsync(package.Digest))!;
        using var buffer = new MemoryStream();
        await archive.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        var uploaded = new InMemoryWorkflowStateRepository();
        var receipt = await new WorkflowPublisher(uploaded, uploaded).UploadPackageAsync(new MemoryStream(bytes), "uploader");
        Assert.True(receipt.Created);
        Assert.Equal(package.Digest, receipt.Stored.Digest);
        Assert.Equal(WorkflowPackageOrigin.Uploaded, receipt.Stored.Origin);

        var entries = await WorkflowPackageArchive.ReadAsync(new MemoryStream(bytes), package.Digest, new());
        Assert.Equal(PublishedActivities.ActivityYaml, System.Text.Encoding.UTF8.GetString(entries["ixiflow/activity.yaml"].Span));
        var dll = entries["lib/net10.0/acme.double.dll"].ToArray();
        var path = Path.Combine(scratch.Path, "acme.double.dll");
        await File.WriteAllBytesAsync(path, dll);
        var compiled = WorkflowCompilation.Compile(PublishedActivities.FluentConsumerSource,
            WorkflowCompilation.ReferencePaths().Append(path).ToArray());
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
        var context = new AssemblyLoadContext("Sample package consumer", isCollectible: true);
        try
        {
            context.LoadFromStream(new MemoryStream(dll));
            var consumer = context.LoadFromStream(new MemoryStream(compiled.Assembly));
            var definition = (WorkflowDefinition)consumer.GetType("DoubleConsumer")!.GetMethod("Build")!.Invoke(null, null)!;
            await using var fluentHost = SampleTests.Services();
            var fluent = await SampleTests.Client(fluentHost).RunAsync(definition, new PublishedNumber { Value = 7 });
            Assert.Equal(WorkflowExecutionStatus.Success, fluent.Status);
            Assert.Equal(14, Assert.IsType<PublishedNumber>(fluent.WorkflowData).Value);

            await using var yamlHost = PublishedArtifactSampleTests.Host(store, scratch.Path);
            var client = yamlHost.GetRequiredService<IWorkflowClient>();
            var start = await client.StartAsync(workflow.Name, version: 1, workflowData: new PublishedNumber { Value = 7 });
            var yaml = await client.AwaitAsync(start, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(yaml.Status == WorkflowExecutionStatus.Success, yaml.ErrorMessage);
            using var data = JsonDocument.Parse(JsonSerializer.Serialize(yaml.WorkflowData));
            Assert.Equal(14, data.RootElement.GetProperty("Value").GetInt32());
            Assert.NotEqual(typeof(PublishedNumber).Assembly, yaml.WorkflowData!.GetType().Assembly);
            await yamlHost.GetRequiredService<WorkflowHost>().StopAsync(default);
        }
        finally { context.Unload(); }
    }
}

[Trait("Feature", "templates")]
[Trait("Spec", "F-01"), Trait("Spec", "F-17"), Trait("Spec", "F-18"), Trait("Spec", "H-01"), Trait("Spec", "H-02")]
public sealed class PublishedArtifactSampleTests
{
    [Fact]
    public async Task TemplatePreviewIsReadOnlyAndPublishedRenderUsesEmbeddedCultureVariant()
    {
        using var scratch = new SampleScratch();
        var store = new InMemoryWorkflowStateRepository();
        var publisher = PublishedArtifacts.Publisher(store);
        var before = await store.GetCatalogRevisionAsync();
        var preview = await publisher.PreviewTemplateAsync(new("samples.greeting", 1, "template", PublishedArtifacts.TemplateYaml),
            new("fr-CA", JsonSerializer.SerializeToElement(new GreetingModel { Name = "Ada" })));
        Assert.Empty(preview.Diagnostics);
        Assert.Equal("Bonjour Ada", preview.Rendered!.Content);
        Assert.Equal(before, await store.GetCatalogRevisionAsync());
        Assert.Empty(await store.GetTemplateVersionsAsync());

        var workflow = await PublishedArtifacts.PublishGreetingAsync(store);
        var bundle = await ReadBundle(store, workflow);
        Assert.NotEmpty(bundle.Manifest.Templates);
        Assert.Contains("samples.greeting@1", System.Text.Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.YamlPath].Span));
        // A newer live version cannot replace the version embedded in the bundle.
        await new TemplatePublisher(store, store, new TemplateEngines([new LiquidTemplateEngine()]))
            .PublishAsync<GreetingModel>(TemplateVersion.Create<GreetingModel>("samples.greeting", 2, "liquid", TemplateFormat.Text,
                new TemplateVariant("", "Changed")));
        await using var host = Host(store, scratch.Path);
        var client = host.GetRequiredService<IWorkflowClient>();
        var start = await client.StartAsync(workflow.Name, version: 1,
            workflowData: new GreetingData { Model = new() { Name = "Ada" } });
        var result = await client.AwaitAsync(start, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(result.WorkflowData));
        Assert.Equal("Bonjour Ada", data.RootElement.GetProperty("Body").GetProperty("Content").GetString());
        await host.GetRequiredService<WorkflowHost>().StopAsync(default);
    }

    [Fact]
    public async Task PublishedUseAndUseTypeExpandAndExecuteInOneInstance()
    {
        using var scratch = new SampleScratch();
        var store = new InMemoryWorkflowStateRepository();
        using var logging = LoggerFactory.Create(builder => builder.AddConsole());
        var workflow = await PublishedArtifacts.PublishInlineAsync(store, logging.CreateLogger<WorkflowPublisher>());
        Assert.Equal(new[]
        {
            new WorkflowVersionDependency("DoubleNumber", 1, WorkflowDependencyKind.Inline),
            new WorkflowVersionDependency("NumberBody", 1, WorkflowDependencyKind.Inline)
        }, workflow.Dependencies!);
        Assert.Equal(2, (await ReadBundle(store, workflow)).Manifest.InlineWorkflows.Count);
        await using var host = Host(store, scratch.Path);
        var client = host.GetRequiredService<IWorkflowClient>();
        var start = await client.StartAsync(workflow.Name, version: 1, workflowData: new PublishedNumber { Value = 7 });
        var result = await client.AwaitAsync(start, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(result.WorkflowData));
        Assert.Equal(16, data.RootElement.GetProperty("Value").GetInt32());
        Assert.Empty((await store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: start))).Items);
        await host.GetRequiredService<WorkflowHost>().StopAsync(default);
    }

    internal static ServiceProvider Host(IWorkflowStateRepository store, string cache)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow().UseWorkflowBundles(Path.Combine(cache, "bundles")).AddIxIFlowTemplates().AddLiquid();
        return services.BuildServiceProvider();
    }

    private static async Task<WorkflowBundle> ReadBundle(IWorkflowStateRepository store, WorkflowVersion workflow)
    {
        await using var stream = (await store.OpenAsync(workflow.ArtifactDigest!.Value))!;
        return await WorkflowBundleArchive.ReadAsync(stream, workflow.ArtifactDigest!.Value);
    }
}

internal sealed class SampleScratch : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IxIFlow.PublishedSamples", Guid.NewGuid().ToString("N"));
    public SampleScratch() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

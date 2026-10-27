using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Extensions;
using IxIFlow.Generators;
using IxIFlow.Samples;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Samples.Tests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26"), Trait("Spec", "F-06")]
public sealed class ScopeSampleTests
{
    [Fact]
    public async Task CatalogAndRenderGuideSnippetsUseTheCompiledPublicApi()
    {
        var collection = new ServiceCollection(); collection.AddIxIFlow().AddIxIFlowTemplates().AddLiquid();
        await using var host = collection.BuildServiceProvider();
        await QuoteMessage.PublishAsync(host);
        var yaml = WorkflowCompilation.Build(WorkflowSourceTranslator.Translate(CatalogExamples.RenderYaml, "catalog-render.yaml"));
        var done = await host.GetRequiredService<IWorkflowClient>().RunAsync(yaml, new MessageData());
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Equal("Devis pour Ada", Assert.IsType<MessageData>(done.WorkflowData).Body!.Content);
        var (templates, steps) = await CatalogExamples.ReadAsync(host.GetRequiredService<IWorkflowStateRepository>(), CatalogScope.Unrestricted);
        Assert.Equal("quote", Assert.Single(templates.Items).Key);
        Assert.Contains(steps, s => s.Kind == "scope"); Assert.Contains(steps, s => s.Kind == "renderTemplate");
    }

    [Theory, InlineData("connection"), InlineData("mailbox"), InlineData("workbook")]
    public async Task FluentAndCompiledYamlUseOnlyPassLocalResources(string sample)
    {
        var fluent = sample switch { "connection" => ScopeExamples.Connection(), "mailbox" => ScopeExamples.Mailbox(), _ => ScopeExamples.Excel() };
        var path = Path.Combine(RepositoryRoot(), "samples/IxIFlow.Samples/Definitions/scoped-" + sample + ".yaml");
        var options = new WorkflowSourceOptions();
        foreach (var type in new[] { typeof(FakeConnectionScope), typeof(MailboxScope), typeof(ExcelScope) })
        {
            var scope = ActivityDescriptor.OfType(type, "samples", "1.0.0").Scope!;
            options.Activities[type.FullName!] = new ActivityDocumentContract(type.FullName!, "")
            { Scope = scope.ValueTypeName, Concurrency = "serialized", Threading = type == typeof(ExcelScope) ? "threadAffine" : "anyThread" };
        }
        var yaml = WorkflowCompilation.Build(WorkflowSourceTranslator.Translate(File.ReadAllText(path), path, options));
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
        Assert.Equal(WorkflowDefinitionValidator.GetContract(fluent).Hash, WorkflowDefinitionValidator.GetContract(yaml).Hash);
        foreach (var definition in new[] { fluent, yaml })
        {
            var factory = new FakeScopeSessions(); var collection = new ServiceCollection();
            collection.AddSingleton<IConnectionSessionFactory>(factory); collection.AddSingleton<IMailSessionFactory>(factory);
            collection.AddSingleton<IExcelSessionFactory>(factory); collection.AddIxIFlow();
            await using var services = collection.BuildServiceProvider();
            var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ScopeData());
            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
            Assert.Equal(sample == "workbook" ? "" : "customer-db", Assert.IsType<ScopeData>(result.WorkflowData).Result);
            Assert.Equal(1, factory.Opens);
            if (sample == "workbook")
            {
                Assert.Equal(2, factory.Workbook!.Writes); Assert.Equal(1, factory.Workbook.Saves); Assert.True(factory.Workbook.Disposed);
            }
            else Assert.True(factory.Session!.Disposed);
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "IxIFlow.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot find scope samples");
    }
}

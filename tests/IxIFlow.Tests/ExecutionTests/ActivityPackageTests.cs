using IxIFlow.ActivitySdk;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using IxIFlow.Generators;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "documents")]
[Trait("Spec", "F-21")]
public class ActivityPackageTests
{
    [Fact]
    public async Task GeneratedYaml_ExecutesRegisteredPackageActivity()
    {
        var services = new ServiceCollection();
        services.AddIxIFlow().AddActivity<SendTestEmailActivity>();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IActivityRegistry>();
        var descriptor = registry.Find("example.email.send");
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(SendTestEmailActivity), descriptor.ActivityType);
        Assert.Equal("ixiflow.tests@1.0.0", Assert.Single(registry.Packages).Id + "@" + Assert.Single(registry.Packages).Version);

        var document = Document();
        var options = await ResolvedContracts(registry);
        var definition = await IxIFlow.Authoring.WorkflowCompilation.BuildAsync(document, options);

        var result = await provider.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new EmailWorkflowData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(Assert.IsType<EmailWorkflowData>(result.WorkflowData).Sent);
    }

    [Fact]
    public async Task MissingOrMismatchedActivity_FailsValidationBeforeExecution()
    {
        var services = new ServiceCollection();
        var builder = services.AddIxIFlow();
        using var emptyProvider = services.BuildServiceProvider();
        var options = await ResolvedContracts(emptyProvider.GetRequiredService<IActivityRegistry>());
        var missing = await YamlTestContracts.ValidateAsync(Document(), options);
        Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Code == "STEP003");

        builder.AddActivity<SendTestEmailActivity>();
        using var provider = services.BuildServiceProvider();
        options = await ResolvedContracts(provider.GetRequiredService<IActivityRegistry>());
        var wrongVersion = Document(activityVersion: "2.0");
        var versionResult = await YamlTestContracts.ValidateAsync(wrongVersion, options);
        Assert.Contains(versionResult.Diagnostics, diagnostic => diagnostic.Code == "ACT003");

        var missingPackage = Document(includeImport: false);
        var packageResult = await YamlTestContracts.ValidateAsync(missingPackage, options);
        Assert.Contains(packageResult.Diagnostics, diagnostic => diagnostic.Code == "ACT004");
    }

    [Fact]
    public async Task GeneratedYaml_UsesTheActivityPackageContract()
    {
        const string yaml = """
            schemaVersion: "1.0"
            workflow:
              name: EmailWorkflow
              version: 1
              dataType: EmailWorkflowData
            imports:
              catalogs:
                - ixiflow.tests@1.0.0
            definitions:
              - kind: activity
                id: send-email
                activity: example.email.send
                activityVersion: "1.0"
                output:
                  - source: Sent
                    to:
                      kind: path
                      path: workflow.Sent
            """;
        var services = new ServiceCollection();
        services.AddIxIFlow().AddActivity<SendTestEmailActivity>();
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IActivityRegistry>();
        var document = WorkflowYamlSerializer.Read(yaml);
        var rewritten = WorkflowYamlSerializer.Read(WorkflowYamlSerializer.Write(document));
        var activity = Assert.IsType<ActivityStepDocument>(Assert.Single(rewritten.Definitions));
        Assert.Equal("example.email.send", activity.Activity);
        Assert.Equal("1.0", activity.ActivityVersion);

        var definition = await IxIFlow.Authoring.WorkflowCompilation.BuildAsync(rewritten, await ResolvedContracts(registry));
        var result = await provider.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new EmailWorkflowData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(Assert.IsType<EmailWorkflowData>(result.WorkflowData).Sent);
    }

    private static WorkflowDocument Document(string activityVersion = "1.0", bool includeImport = true) => new()
    {
        Workflow = new WorkflowDefinitionDocument
        {
            Name = "EmailWorkflow", Version = 1, DataType = "EmailWorkflowData"
        },
        Imports = new WorkflowImportsDocument
        {
            Catalogs = includeImport ? ["ixiflow.tests@1.0.0"] : []
        },
        Definitions = [new ActivityStepDocument
        {
            Id = "send-email", Activity = "example.email.send", ActivityVersion = activityVersion,
            Output = [new OutputMappingDocument
            {
                Source = "Sent",
                To = new PathExpressionDocument { Path = "workflow.Sent" }
            }]
        }]
    };

    private static async Task<WorkflowSourceOptions> ResolvedContracts(IActivityRegistry registry)
    {
        var options = YamlTestContracts.Create(("EmailWorkflowData", typeof(EmailWorkflowData)));
        if (registry.Find("example.email.send") is { } activity)
            YamlTestContracts.AddActivity(options, activity);
        return options;
    }
}

public sealed class EmailWorkflowData
{
    public bool Sent { get; set; }
}

[WorkflowActivity("example.email.send")]
public sealed class SendTestEmailActivity : IAsyncActivity
{
    public bool Sent { get; private set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Sent = true;
        return Task.CompletedTask;
    }
}

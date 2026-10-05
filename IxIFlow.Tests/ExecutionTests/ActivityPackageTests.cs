using IxIFlow.ActivitySdk;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public class ActivityPackageTests
{
    private const string Manifest = """
        schemaVersion: "1.0"
        package:
          name: example.email
          version: "1.0.0"
        activities:
          - key: example.email.send
            version: "1.0"
            name: Send email
            category: Email
            fields:
              - key: subject
                label: Subject
                control: text
                required: true
            defaults:
              subject: Order received
        """;

    [Fact]
    public async Task RegisteredPackage_CompilesAndExecutesItsActivity()
    {
        var registry = new ActivityPackageRegistry();
        registry.AddPackage(Manifest, typeof(SendTestEmailActivity).Assembly);
        var descriptor = await registry.FindAsync("example.email.send");
        Assert.NotNull(descriptor);
        Assert.Equal(typeof(SendTestEmailActivity), descriptor.ActivityType);
        Assert.Equal("example.email@1.0.0", Assert.Single(registry.Packages).PackageReference);

        var document = Document();
        var support = new TestDocumentRegistry();
        var context = new WorkflowValidationContext(registry, support, support, support, support);
        var definition = await new WorkflowDocumentCompiler(new WorkflowDocumentValidator())
            .CompileAsync(document, context);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        registry.RegisterActivities(services);
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new EmailWorkflowData());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(Assert.IsType<EmailWorkflowData>(result.WorkflowData).Sent);
    }

    [Fact]
    public async Task MissingOrMismatchedActivity_FailsValidationBeforeExecution()
    {
        var registry = new ActivityPackageRegistry();
        var support = new TestDocumentRegistry();
        var context = new WorkflowValidationContext(registry, support, support, support, support);
        var missing = await new WorkflowDocumentValidator().ValidateAsync(Document(), context);
        Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Code == "STEP003");

        registry.AddPackage(Manifest, typeof(SendTestEmailActivity).Assembly);
        var wrongVersion = Document(activityVersion: "2.0");
        var versionResult = await new WorkflowDocumentValidator().ValidateAsync(wrongVersion, context);
        Assert.Contains(versionResult.Diagnostics, diagnostic => diagnostic.Code == "ACT003");

        var missingPackage = Document(includeImport: false);
        var packageResult = await new WorkflowDocumentValidator().ValidateAsync(missingPackage, context);
        Assert.Contains(packageResult.Diagnostics, diagnostic => diagnostic.Code == "ACT004");
    }

    [Fact]
    public void ManifestWithNoActivityImplementation_IsRejected()
    {
        var registry = new ActivityPackageRegistry();
        var manifest = Manifest.Replace("example.email.send", "example.email.missing", StringComparison.Ordinal);

        var error = Assert.Throws<InvalidOperationException>(() =>
            registry.AddPackage(manifest, typeof(SendTestEmailActivity).Assembly));
        Assert.Contains("no matching implementation", error.Message);
    }

    [Fact]
    public async Task YamlWorkflow_UsesTheSameActivityPackageContract()
    {
        const string yaml = """
            schemaVersion: "1.0"
            workflow:
              name: EmailWorkflow
              version: 1
              dataType: EmailWorkflowData
            imports:
              catalogs:
                - example.email@1.0.0
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
        var registry = new ActivityPackageRegistry();
        registry.AddPackage(Manifest, typeof(SendTestEmailActivity).Assembly);
        var document = WorkflowYamlSerializer.Read(yaml);
        var rewritten = WorkflowYamlSerializer.Read(WorkflowYamlSerializer.Write(document));
        var activity = Assert.IsType<ActivityStepDocument>(Assert.Single(rewritten.Definitions));
        Assert.Equal("example.email.send", activity.Activity);
        Assert.Equal("1.0", activity.ActivityVersion);

        var support = new TestDocumentRegistry();
        var definition = await new WorkflowDocumentCompiler(new WorkflowDocumentValidator())
            .CompileAsync(rewritten, new WorkflowValidationContext(registry, support, support, support, support));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        registry.RegisterActivities(services);
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new EmailWorkflowData());

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
            Catalogs = includeImport ? ["example.email@1.0.0"] : []
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

    private sealed class TestDocumentRegistry : IWorkflowCatalog, IEventRegistry, IDataTypeRegistry, IStepTemplateRegistry
    {
        public ValueTask<WorkflowDescriptor?> FindAsync(string workflowName, int? version, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorkflowDescriptor?>(null);

        ValueTask<EventDescriptor?> IEventRegistry.FindAsync(string eventKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult<EventDescriptor?>(null);

        ValueTask<DataTypeDescriptor?> IDataTypeRegistry.FindAsync(string typeKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult<DataTypeDescriptor?>(typeKey == "EmailWorkflowData"
                ? new DataTypeDescriptor { Key = typeKey, ClrType = typeof(EmailWorkflowData) }
                : null);

        ValueTask<StepTemplateDescriptor?> IStepTemplateRegistry.FindAsync(string templateKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult<StepTemplateDescriptor?>(null);
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

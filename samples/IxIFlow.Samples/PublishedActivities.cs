using IxIFlow.Authoring;
using IxIFlow.Core;

namespace IxIFlow.Samples;

/// <summary>Publishes a code activity and a workflow that imports its package.</summary>
public static class PublishedActivities
{
    public static string ActivityYaml
    {
        get
        {
            using var stream = typeof(PublishedActivities).Assembly.GetManifestResourceStream("Samples.double.activity.yaml")!;
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        }
    }

    // snippet: package-consumer-yaml
    public const string WorkflowYaml = """
                                       workflow: {name: PublishedDouble, dataType: IxIFlow.Samples.PublishedNumber}
                                       imports: {catalogs: [acme.double@1.0.0]}
                                       definitions:
                                         - kind: activity
                                           activity: acme.double
                                           activityVersion: "1"
                                           input: [{target: Value, from: {path: workflow.Value}}]
                                           output: [{source: Result, to: {path: workflow.Value}}]
                                       """;

    // end-snippet: package-consumer-yaml

    // After a PackageReference to acme.double version 1.0.0, this is ordinary
    // application C#. The sample test compiles it against the published nupkg DLL.
    // snippet: package-consumer-fluent
    public const string FluentConsumerSource = """
                                               using IxIFlow.Builders;
                                               using IxIFlow.Core;
                                               using IxIFlow.Samples;

                                               public static class DoubleConsumer
                                               {
                                                   public static WorkflowDefinition Build() => Workflow.Create<PublishedNumber>("FluentDouble")
                                                       .Step<IxIFlow.Activities.Acme.Double>(step => step
                                                           .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Value)
                                                           .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Value))
                                                       .Build();
                                               }
                                               """;

    // end-snippet: package-consumer-fluent

    public static async Task<(WorkflowPackage Package, WorkflowVersion Workflow)> PublishAsync(IWorkflowStateRepository store)
    {
        var publisher = new WorkflowPublisher(store, store);
        var activity = await store.SaveDefinitionAsync(new("acme.double", 1, "activity", ActivityYaml), null);
        var package = Require(await publisher.PublishAsync(activity.Id, "sample-publisher")).Package!;
        var draft = await store.SaveDefinitionAsync(new("PublishedDouble", 1, "workflow", WorkflowYaml), null);

        return (package, Require(await publisher.PublishAsync(draft.Id, "sample-publisher")).Workflow!);
    }

    internal static WorkflowPublishResult Require(WorkflowPublishResult result) => result.Success
        ? result
        : throw new InvalidOperationException(string.Join("; ", result.Diagnostics.Select(item => item.Code + ": " + item.Message)));
}

public sealed class PublishedNumber
{
    public int Value { get; set; }
}
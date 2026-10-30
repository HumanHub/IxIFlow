using IxIFlow.Authoring;
using IxIFlow.Core;

namespace IxIFlow.Samples;

/// <summary>Publishes the catalog invocation shown in the YAML authoring guide.</summary>
public static class PublishedCheckout
{
    // snippet: checkout-yaml
    public const string Yaml = """
                               workflow:
                                 name: Checkout
                                 version: 1
                                 dataType: CheckoutData
                                 input: [OrderId]
                                 output: [Receipt]
                               imports:
                                 workflows: [Orders]
                               definitions:
                                 - kind: invokeWorkflow
                                   workflow: {name: Orders}
                                   input:
                                     - target: OrderId
                                       from: {path: workflow.OrderId}
                                   output:
                                     - source: Receipt
                                       to: {path: workflow.Receipt}
                               """;

    // end-snippet: checkout-yaml

    public static async Task<WorkflowPublishResult> PublishAsync(IWorkflowStateRepository store)
    {
        var options = new WorkflowPublisherOptions();
        options.SourceOptions.Types["CheckoutData"] = "global::IxIFlow.Samples.CheckoutData";
        var publisher = new WorkflowPublisher(store, store, options);

        var orders = await store.SaveDefinitionAsync(new("Orders", 1, "workflow", """
                                                                                  workflow: {name: Orders, dataType: CheckoutData, input: [OrderId], output: [Receipt]}
                                                                                  definitions: []
                                                                                  """), null);

        var callee = await publisher.PublishAsync(orders.Id);

        if (!callee.Success)
            return callee;

        var checkout = await store.SaveDefinitionAsync(new("Checkout", 1, "workflow", Yaml), null);

        return await publisher.PublishAsync(checkout.Id);
    }
}

public sealed class CheckoutData
{
    public string OrderId { get; set; } = "";
    public string Receipt { get; set; } = "";
}
using IxIFlow.Core;
using IxIFlow.Samples;
using IxIFlow.Samples.Generated;
using Microsoft.Extensions.DependencyInjection;

var services = Hosting.Services();
await using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IWorkflowClient>();
var definition = args.Contains("--yaml") ? Workflow_quote_workflow.Build() : Quote.Definition();
var result = await client.RunAsync(definition, new QuoteData());
if (!result.IsSuccess)
    throw new InvalidOperationException(result.ErrorMessage);
var quote = (QuoteData)result.WorkflowData!;
Console.WriteLine($"{quote.Customer}: {quote.Amount} ({quote.Outcome})");

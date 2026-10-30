using IxIFlow.Core;

namespace IxIFlow.Samples.Tests;

[Trait("Feature", "host")]
public sealed class Slice4HostedDocumentationTests
{
    [Fact]
    public async Task HostedDocumentationProducesTheDocumentedQuote()
    {
        var result = await DocumentationExamples.RunHostedAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var quote = Assert.IsType<QuoteData>(result.WorkflowData);
        Assert.Equal(12.50m, quote.Amount);
        Assert.Equal("ready", quote.Outcome);
        Assert.Equal("Ada", quote.Customer);
    }
}
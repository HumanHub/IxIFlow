using IxIFlow.Core;
using IxIFlow.Samples;

namespace IxIFlow.Samples.Tests;

public sealed class PublishedCheckoutTests
{
    [Fact]
    public async Task GuideCatalogInvocationCompilesAndPinsItsCallee()
    {
        var store = new InMemoryWorkflowStateRepository();
        var result = await PublishedCheckout.PublishAsync(store);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(new WorkflowVersionDependency("Orders", 1, WorkflowDependencyKind.Child),
            Assert.Single(result.Version!.Dependencies!));
        var contract = WorkflowContract.Parse(result.Version.ContractJson);
        Assert.Equal("OrderId", Assert.Single(contract.Inputs).Name);
        Assert.Equal("Receipt", Assert.Single(contract.Outputs).Name);
    }
}

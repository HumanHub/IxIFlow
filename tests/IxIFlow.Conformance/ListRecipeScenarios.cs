using IxIFlow.Core;
using IxIFlow.Recipes;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// The list recipes of spec D-17 that run their items one after another (D-34), as the samples define
/// them, in every profile (J-02).
/// </summary>
[Trait("Spec", "D-17")]
[Trait("Spec", "E-11")]
public sealed class ListRecipeScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task EachOrderLineIsPricedWithTheTotalOfTheLinesBeforeIt(string profile)
    {
        await using var cluster = await ConformanceCluster.CreateAsync(profile, _ => { });
        var engine = cluster.AddHost();

        var result = await engine.RunAsync(PriceLinesRecipe.Definition(), new Basket
        {
            OrderId = "order-15",
            Lines =
            [
                new BasketLine { Sku = "pen", Quantity = 3, UnitPrice = 2m },
                new BasketLine { Sku = "pad", Quantity = 1, UnitPrice = 5m },
                new BasketLine { Sku = "ink", Quantity = 2, UnitPrice = 4m }
            ]
        });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var basket = Assert.IsType<Basket>(result.WorkflowData);
        Assert.Equal(19m, basket.Total);
        Assert.Equal(
            [
                new ItemResult<decimal>(0, ItemStatus.Completed, 6m),
                new ItemResult<decimal>(1, ItemStatus.Completed, 5m),
                new ItemResult<decimal>(2, ItemStatus.Completed, 8m)
            ],
            basket.LineTotals);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TheInnerBodyReadsBothTheCustomerAndTheLine(string profile)
    {
        await using var cluster = await ConformanceCluster.CreateAsync(profile, _ => { });
        var engine = cluster.AddHost();

        var result = await engine.RunAsync(InvoiceCustomersRecipe.Definition(), new Invoicing
        {
            Customers =
            [
                new Customer { Name = "ann", Lines = [new BasketLine { Sku = "pen" }, new BasketLine { Sku = "pad" }] },
                new Customer { Name = "bob", Lines = [] },
                new Customer { Name = "cid", Lines = [new BasketLine { Sku = "ink" }] }
            ]
        });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["ann #1: pen", "ann #2: pad", "cid #1: ink"],
            Assert.IsType<Invoicing>(result.WorkflowData).InvoiceLines);
    }
}

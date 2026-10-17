using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Recipes;

public sealed class BasketLine
{
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public sealed class Basket
{
    public string OrderId { get; set; } = "";
    public List<BasketLine> Lines { get; set; } = [];
    public decimal Total { get; set; }
    public List<ItemResult<decimal>> LineTotals { get; set; } = [];
}

/// <summary>Prices one line and adds it to the running total.</summary>
public sealed class PriceLine : IAsyncActivity
{
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalSoFar { get; set; }
    public decimal LineTotal { get; set; }
    public decimal Total { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        LineTotal = Quantity * UnitPrice;
        Total = TotalSoFar + LineTotal;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Recipe 15, a step per order line (spec D-17): a sequential <c>ForEach</c> over the order's lines prices
/// each one and keeps a running total; each line sees the total of the lines before it. Each line's total
/// is also kept, in line order, with <c>.Result(...)</c> and <c>Into</c>.
/// </summary>
public static class PriceLinesRecipe
{
    public static WorkflowDefinition Definition() =>
        Workflow.Create<Basket>("PriceLines")
            .Step<ValidateOrder>()
            .ForEach(ctx => ctx.WorkflowData.Lines,
                (body, line) => body
                    .Step<PriceLine>(step => step
                        .Input(activity => activity.Quantity).From(ctx => ctx.Item(line).Quantity)
                        .Input(activity => activity.UnitPrice).From(ctx => ctx.Item(line).UnitPrice)
                        .Input(activity => activity.TotalSoFar).From(ctx => ctx.WorkflowData.Total)
                        .Output(activity => activity.Total).To(ctx => ctx.WorkflowData.Total))
                    .Result(ctx => ctx.PreviousStep.LineTotal),
                setup => setup.As("line").Into(ctx => ctx.WorkflowData.LineTotals))
            .Build();
}

public sealed class Customer
{
    public string Name { get; set; } = "";
    public List<BasketLine> Lines { get; set; } = [];
}

public sealed class Invoicing
{
    public List<Customer> Customers { get; set; } = [];
    public List<string> InvoiceLines { get; set; } = [];
}

/// <summary>Writes one invoice line.</summary>
public sealed class WriteInvoiceLine : IAsyncActivity
{
    public string Customer { get; set; } = "";
    public int Position { get; set; }
    public string Sku { get; set; } = "";
    public List<string> InvoiceLines { get; set; } = [];

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        InvoiceLines = [.. InvoiceLines, $"{Customer} #{Position + 1}: {Sku}"];
        return Task.CompletedTask;
    }
}

/// <summary>
/// Recipe 17, nested lists (spec D-17): a <c>ForEach</c> over customers holds a <c>ForEach</c> over each
/// customer's lines, and the inner body reads both the customer and the line through their handles.
/// </summary>
public static class InvoiceCustomersRecipe
{
    public static WorkflowDefinition Definition() =>
        Workflow.Create<Invoicing>("InvoiceCustomers")
            .Step<ValidateInvoicing>()
            .ForEach(ctx => ctx.WorkflowData.Customers,
                (customers, customer) => customers
                    .ForEach(ctx => ctx.Item(customer).Lines,
                        (lines, line) => lines
                            .Step<WriteInvoiceLine>(step => step
                                .Input(activity => activity.Customer).From(ctx => ctx.Item(customer).Name)
                                .Input(activity => activity.Position).From(ctx => ctx.Index(line))
                                .Input(activity => activity.Sku).From(ctx => ctx.Item(line).Sku)
                                .Input(activity => activity.InvoiceLines).From(ctx => ctx.WorkflowData.InvoiceLines)
                                .Output(activity => activity.InvoiceLines).To(ctx => ctx.WorkflowData.InvoiceLines)),
                        setup => setup.As("line")),
                setup => setup.As("customer"))
            .Build();
}

/// <summary>Checks the customers before invoicing.</summary>
public sealed class ValidateInvoicing : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

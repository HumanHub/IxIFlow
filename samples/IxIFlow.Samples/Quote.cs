using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class QuoteData
{
    public string Customer { get; set; } = "Ada";
    public decimal Amount { get; set; } = 10m;
    public string Outcome { get; set; } = "";
    public DateTimeOffset PricedAt { get; set; }
}

/// <summary>Adds a handling fee to a quote. The engine supplies the typed default.</summary>
[WorkflowActivity("samples.add-fee", Version = "1.0")]
public sealed class AddFee : IAsyncActivity
{
    [ActivityInput, Required, Display(Name = "Customer", Description = "The customer receiving the quote"), UIHint("text")]
    public string Customer { get; set; } = "";

    [ActivityInput, DefaultValue(typeof(decimal), "2.50")]
    public decimal Fee { get; set; }

    [ActivityInputOutput, Display(Name = "Amount", Description = "The quote before and after the fee")]
    public decimal Amount { get; set; }

    [ActivityOutput, Display(Name = "Receipt")]
    public string Receipt { get; private set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Amount += Fee;
        Receipt = "Quote for " + Customer;
        return Task.CompletedTask;
    }
}

[WorkflowActivity("samples.label")]
public sealed class Label : IAsyncActivity
{
    [ActivityInputOutput]
    public string Value { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>Maps an in-out property both ways, then uses a sequence and a condition.</summary>
public static class Quote
{
    public static WorkflowDefinition Definition() =>
        Workflow.Create<QuoteData>("Quote")
            .Step<AddFee>(step => step
                .Input(activity => activity.Customer).From(ctx => ctx.WorkflowData.Customer)
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(activity => activity.Amount).To(ctx => ctx.WorkflowData.Amount))
            .Sequence(sequence => sequence
                .Step<Label>(step => step
                    .Input(activity => activity.Value).From(ctx => ctx.PreviousStep.Receipt)
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))
            .If(ctx => ctx.WorkflowData.Amount > 10,
                then => then.Step<Label>(step => step
                    .Input(activity => activity.Value).From(ctx => "ready")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)),
                @else => @else.Step<Label>(step => step
                    .Input(activity => activity.Value).From(ctx => "small order")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))
            .Build();
}

using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class DeclineData { public int Code { get; set; } = 402; public int RecordedCode { get; set; } }
public sealed class PaymentDeclinedException(string message) : Exception(message) { public int Code { get; init; } }
public sealed class DeclineFault { public int Code { get; set; } }

[WorkflowActivity("samples.record-decline")]
public sealed class RecordDecline : IAsyncActivity
{
    [ActivityInputOutput] public int Code { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Constructs a business error, propagates it from an inner Catch, and records its declared fault.</summary>
public static class ThrowAndRethrow
{
    public static WorkflowDefinition Definition() => Workflow.Create<DeclineData>("ThrowAndRethrow")
        .Step<Label>(s => s.Input(a => a.Value).From(ctx => "checkout"))
        .Try(outer => outer.Try(body => body.Throw<PaymentDeclinedException>(ctx => new("Payment declined") { Code = ctx.WorkflowData.Code }))
            .Catch<PaymentDeclinedException, DeclineFault>(caught => caught.Rethrow()))
        .Catch<PaymentDeclinedException, DeclineFault>(caught => caught.Step<RecordDecline>(s => s.Input(a => a.Code).From(ctx => ctx.Fault.Code)
            .Output(a => a.Code).To(ctx => ctx.WorkflowData.RecordedCode))).Build();
}

using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class FailureData
{
    public string Message { get; set; } = "";
    public bool Audited { get; set; }
}

public sealed record QuoteFault(string Message);

public sealed class RefuseQuote : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Quote expired");
}

public sealed class AuditQuote : IAsyncActivity
{
    public bool Audited { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Audited = true;
        return Task.CompletedTask;
    }
}

/// <summary>Copies a business failure into typed fault data and audits every exit through Finally.</summary>
public static class FailedQuote
{
    public static WorkflowDefinition Definition() =>
        new WorkflowBuilder<FailureData, object>([], "FailedQuote")
            .Try(body => body.Step<RefuseQuote>())
            .Catch<InvalidOperationException, QuoteFault>(handler => handler
                .Step<Label>(step => step
                    .Input(activity => activity.Value).From(ctx => ctx.Fault.Message)
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Message)))
            .Finally(body => body.Step<AuditQuote>(step => step
                .Output(activity => activity.Audited).To(ctx => ctx.WorkflowData.Audited)))
            .Build();
}

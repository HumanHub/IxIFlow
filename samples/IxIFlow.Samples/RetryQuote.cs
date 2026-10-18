using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class RetryData
{
    public int Attempt { get; set; }
    public string EarlierError { get; set; } = "";
    public string EarlierNote { get; set; } = "";
    public string EarlierAttemptId { get; set; } = "";
    public string InvocationId { get; set; } = "";
    public string AttemptId { get; set; } = "";
}

/// <summary>A failed attempt leaves a note so the retry can choose another endpoint.</summary>
public sealed class FetchQuote : IAsyncActivity
{
    public int Attempt { get; set; }
    public string EarlierError { get; set; } = "";
    public string EarlierNote { get; set; } = "";
    public string EarlierAttemptId { get; set; } = "";
    public string InvocationId { get; set; } = "";
    public string AttemptId { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Attempt = context.Attempt.Number;
        InvocationId = context.InvocationId;
        AttemptId = context.AttemptId;
        if (Attempt == 1)
        {
            context.Attempt.Note = "primary endpoint";
            throw new HttpRequestException("Primary unavailable");
        }
        var earlier = context.Attempt.Previous[^1];
        EarlierError = earlier.ErrorMessage!;
        EarlierNote = earlier.Note!.ToString()!;
        EarlierAttemptId = earlier.AttemptId;
        return Task.CompletedTask;
    }
}

public static class RetryQuote
{
    public static WorkflowDefinition Definition() =>
        new WorkflowBuilder<RetryData, object>([], "RetryQuote")
            .Saga(saga => saga.Step<FetchQuote>(step => step
                .Output(activity => activity.Attempt).To(ctx => ctx.WorkflowData.Attempt)
                .Output(activity => activity.EarlierError).To(ctx => ctx.WorkflowData.EarlierError)
                .Output(activity => activity.EarlierNote).To(ctx => ctx.WorkflowData.EarlierNote)
                .Output(activity => activity.EarlierAttemptId).To(ctx => ctx.WorkflowData.EarlierAttemptId)
                .Output(activity => activity.InvocationId).To(ctx => ctx.WorkflowData.InvocationId)
                .Output(activity => activity.AttemptId).To(ctx => ctx.WorkflowData.AttemptId)
                .OnError<HttpRequestException>(handler => handler.ThenRetry(new RetryPolicy(1)))))
            .Build();
}

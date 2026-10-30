using System.ComponentModel.DataAnnotations;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class LookupRequest
{
    public string? Query { get; set; }
    public string? Response { get; set; }
}

public sealed class LookupData
{
    [WorkflowInput, Required] public string Query { get; set; } = "";
    [WorkflowOutput] public string? Response { get; set; }
}

/// <summary>Register the child and an ILookup service before running either parent twin.</summary>
public static class InvocationRetries
{
    public static WorkflowDefinition Definition() =>
        new WorkflowBuilder<LookupRequest, object>([], "RetryLookup")

            // snippet: invocation-retries
            .Invoke<LookupData>("RetryLookupChild", 1, setup => setup
                .Input(child => child.Query).From(ctx => ctx.WorkflowData.Query!)
                .OnError<InvalidOperationException>(error => error.ThenRetry(new RetryPolicy(
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), 2, 2)))
                .Output(child => child.Response).To(ctx => ctx.WorkflowData.Response))

            // end-snippet: invocation-retries
            .WithId("lookup").Build();

    public static WorkflowDefinition Child() => Workflow.Create<LookupData>("RetryLookupChild")
        .Step<FetchLookup>(setup => setup.Input(activity => activity.Query).From(ctx => ctx.WorkflowData.Query)
            .Output(activity => activity.Response).To(ctx => ctx.WorkflowData.Response)).WithId("fetch").Build();
}

public interface ILookup
{
    Task<string> FetchAsync(string query, CancellationToken cancellationToken);
}

public sealed class FetchLookup(ILookup lookup) : IAsyncActivity
{
    public string Query { get; set; } = "";
    public string? Response { get; set; }

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        Response = await lookup.FetchAsync(Query, cancellationToken);
}
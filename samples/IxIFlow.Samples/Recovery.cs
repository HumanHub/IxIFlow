using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

/// <summary>The application checks its provider by invocation ID before repeating a reservation.</summary>
public interface IQuoteReservations
{
    Task<string> ReserveAsync(string invocationId, CancellationToken cancellationToken);
    Task<string?> FindAsync(string invocationId, CancellationToken cancellationToken);
}

public sealed class ReservationData
{
    public string Reference { get; set; } = "";
}

/// <summary>A crash after the provider accepts a reservation is resolved by looking up its idempotency key.</summary>
[WorkflowActivity("samples.reserve-quote")]
public sealed class ReserveQuote(IQuoteReservations reservations) : IRecoverableActivity<string>
{
    [ActivityOutput]
    public string Reference { get; set; } = "";

    public string CaptureRecoveryState(IActivityContext context) => context.InvocationId;

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        Reference = await reservations.ReserveAsync(context.GetRecoveryState<string>(), cancellationToken);

    public async Task<ActivityRecoveryResult<string>> RecoverAsync(string invocationId, IActivityContext context,
        CancellationToken cancellationToken = default)
    {
        var reference = await reservations.FindAsync(invocationId, cancellationToken);
        if (reference == null)
            return new ActivityRecoveryResult<string>.Unresolved("Check the reservation with the provider");
        Reference = reference;
        return new ActivityRecoveryResult<string>.Completed();
    }
}

public static class Recovery
{
    public static WorkflowDefinition Definition() =>
        Workflow.Create<ReservationData>("Reservation")
            .Step<ReserveQuote>(step => step
                .Output(activity => activity.Reference).To(ctx => ctx.WorkflowData.Reference))
            .Build();

    /// <summary>An operator supplies an observed result when the provider cannot resolve it automatically.</summary>
    public static async Task<WorkflowExecutionResult> ConfirmAsync(IWorkflowClient client, string instanceId,
        string observedReference)
    {
        var pending = (await client.GetPendingActivitiesAsync(instanceId)).Single();
        return await client.ResolveAsync(instanceId, pending.InvocationId,
            ActivityResolution.Completed(new Dictionary<string, object?> { [nameof(ReserveQuote.Reference)] = observedReference },
                note: "Confirmed with reservation provider", decidedBy: "operator"));
    }
}

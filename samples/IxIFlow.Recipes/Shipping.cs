using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Recipes;

/// <summary>Hands parcels to a carrier.</summary>
public interface IShipping
{
    Task<string> ShipAsync(string orderId, CancellationToken cancellationToken);
}

/// <summary>A warehouse clerk's confirmation, answered from a task list.</summary>
public sealed class ShipmentConfirmed
{
    public string TrackingNumber { get; set; } = "";
}

/// <summary>The carrier's notification, sent to the order by its key.</summary>
public sealed class ParcelDelivered
{
    public DateTime At { get; set; }
}

public sealed class Shipment
{
    public string OrderId { get; set; } = "";
    public string TrackingNumber { get; set; } = "";
    public string Parcel { get; set; } = "";
    public bool Delivered { get; set; }
}

public sealed class Ship(IShipping shipping) : IAsyncActivity
{
    public string OrderId { get; set; } = "";
    public string Parcel { get; set; } = "";

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        Parcel = await shipping.ShipAsync(OrderId, cancellationToken);
}

/// <summary>
/// Recipe 6, a human task inbox (spec D-17): a wait with no request. A task list finds it with
/// <c>IWorkflowClient.GetOpenWaitsAsync</c> and answers it with the token listed for it; a screen that
/// answers after the wait closed is told why.
/// </summary>
public static class ConfirmShipmentRecipe
{
    public static WorkflowDefinition Definition() =>
        Workflow.Create<Shipment>("ConfirmShipment")
            .Step<ValidateOrder>()
            .WaitFor<ShipmentConfirmed>("ship", configure: wait => wait
                .Output(confirmed => confirmed.TrackingNumber).To(ctx => ctx.WorkflowData.TrackingNumber))
            .Build();
}

/// <summary>
/// Recipe 7, a notification (spec D-17): after shipping, wait for the carrier's "delivered" event, sent
/// by key. It completes a wait that is open when it arrives; sent earlier, it is refused and not kept.
/// </summary>
public static class DeliveryRecipe
{
    public static WorkflowDefinition Definition() =>
        Workflow.Create<Shipment>("AwaitDelivery")
            .Step<Ship>(step => step
                .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Output(activity => activity.Parcel).To(ctx => ctx.WorkflowData.Parcel))
            .WaitFor<ParcelDelivered>("delivered")
            .Step<Delivered>(step => step
                .Output(activity => activity.Done).To(ctx => ctx.WorkflowData.Delivered))
            .Build();
}

public sealed class Delivered : IAsyncActivity
{
    public bool Done { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Done = true;
        return Task.CompletedTask;
    }
}

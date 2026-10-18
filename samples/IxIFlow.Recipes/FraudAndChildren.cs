using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Recipes;

public sealed class FraudCase
{
    public string OrderInstanceId { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>Cancels the workflow of a flagged order through the client, as any application code can.</summary>
public sealed class CancelFlaggedOrder(IWorkflowClient client) : IAsyncActivity
{
    public string OrderInstanceId { get; set; } = "";
    public string Reason { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        client.CancelAsync(OrderInstanceId, new CancellationReason
        {
            ReasonCode = "fraud", Description = Reason
        }, cancellationToken);
}

/// <summary>
/// Recipe 9, cancel another instance: a fraud workflow cancels the order workflow it
/// flagged. The order's open waits close as cancelled, so an approver's late click is refused as
/// cancelled.
/// </summary>
public static class FraudRecipe
{
    public static WorkflowDefinition Definition() =>
        Workflow.Create<FraudCase>("CancelFlaggedOrder")
            .Step<CancelFlaggedOrder>(step => step
                .Input(activity => activity.OrderInstanceId).From(ctx => ctx.WorkflowData.OrderInstanceId)
                .Input(activity => activity.Reason).From(ctx => ctx.WorkflowData.Reason))
            .Build();
}

public sealed class Purchase
{
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
    public bool Approved { get; set; }
}

/// <summary>
/// Recipe 10, a child asks: a child workflow sends the approval request. The answer goes to
/// the child by its token, and the parent continues with the child's result when the child finishes.
/// </summary>
public static class ChildApprovalRecipe
{
    public const string ChildName = "PurchaseApproval";

    public static WorkflowDefinition Child() =>
        Workflow.Create<Purchase>(ChildName)
            .Step<ValidateOrder>()
            .Request<RequestApproval>("approval", step => step
                .Input(activity => activity.Approver).From(_ => "purchasing")
                .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount))
            .WaitFor<ApprovalDecision>("approval", configure: wait => wait
                .Output(decision => decision.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();

    public static WorkflowDefinition Parent() =>
        Workflow.Create<Order>("OrderWithPurchaseApproval")
            .Step<ValidateOrder>()
            .Invoke<Purchase>(ChildName, 1, child => child
                .Input(purchase => purchase.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(purchase => purchase.Amount).From(ctx => ctx.WorkflowData.Amount)
                .Output(purchase => purchase.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build();
}

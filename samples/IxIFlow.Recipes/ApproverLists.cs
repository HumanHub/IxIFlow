using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Recipes;

/// <summary>An order and the approvers asked about it, with one decision per approver.</summary>
public sealed class ApprovalRound
{
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
    public List<string> Approvers { get; set; } = [];
    public List<ItemResult<ApprovalDecision>> Decisions { get; set; } = [];
    public string Outcome { get; set; } = "";
}

/// <summary>
/// Recipe 13, any one of N approvers from the data, and recipe 18, empty and all rejected (spec D-17):
/// a parallel <c>ForEach</c> asks every approver and continues on the first approval. A rejection does
/// not end it. The other approvers' late clicks are refused as cancelled, and the decisions show who
/// approved, who rejected and who was cancelled. An empty list, or one where everyone rejects, takes the
/// rejection path with every decision recorded.
/// </summary>
public static class AnyApproverFromListRecipe
{
    public static WorkflowDefinition Definition() =>
        Workflow.Create<ApprovalRound>("AnyApproverFromList")
            .Step<ValidateOrder>()
            .ForEach(ctx => ctx.WorkflowData.Approvers, AskEach,
                setup => setup.As("approver").Parallel()
                    .WaitConditionally<ApprovalDecision>((_, decisions) => decisions.Any(decision => decision.Value!.Approved))
                    .Into(ctx => ctx.WorkflowData.Decisions))
            .If(ctx => ctx.WorkflowData.Decisions.Any(decision => decision.Value != null && decision.Value.Approved),
                then => then.Step<Decide>(step => step
                    .Input(activity => activity.Value).From(_ => "approved")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)),
                @else => @else.Step<Decide>(step => step
                    .Input(activity => activity.Value).From(_ => "rejected")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))
            .Build();

    internal static void AskEach(ISequenceBuilder<ApprovalRound, ValidateOrder> body, ItemScope<string> approver) =>
        body
            .Request<RequestApproval>("approval", step => step
                .Input(activity => activity.Approver).From(ctx => ctx.Item(approver))
                .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount))
            .WaitFor<ApprovalDecision>("approval")
            .Result(ctx => ctx.PreviousStep);
}

/// <summary>
/// Recipe 14, every approver from the data, recipe 19, one item fails, and recipe 21, queued requests are
/// not sent (spec D-17): a parallel <c>ForEach</c> collects one decision per approver, in approver order,
/// whatever order they answer in. With a cap, only that many requests are out at once; the next is sent
/// when one is answered. If asking any approver fails, the others are cancelled, their waits close as
/// cancelled, and the round fails with no decisions stored.
/// </summary>
public static class EveryApproverFromListRecipe
{
    public static WorkflowDefinition Definition(int? maxConcurrency = null) =>
        Workflow.Create<ApprovalRound>(maxConcurrency == null ? "EveryApproverFromList" : "EveryApproverFromListCapped")
            .Step<ValidateOrder>()
            .Try(round => round.ForEach(ctx => ctx.WorkflowData.Approvers, AnyApproverFromListRecipe.AskEach,
                setup => setup.As("approver").Parallel(maxConcurrency).Into(ctx => ctx.WorkflowData.Decisions)))
            .Catch(failed => failed.Step<Decide>(step => step
                .Input(activity => activity.Value).From(_ => "failed")
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))
            .Build();
}

/// <summary>A customer's charge, run as a child workflow.</summary>
public sealed class CustomerCharge
{
    public string Customer { get; set; } = "";
    public decimal Amount { get; set; }
    public bool Paid { get; set; }
}

public sealed class Billing
{
    public List<string> Customers { get; set; } = [];
    public List<ItemResult<bool>> Paid { get; set; } = [];
}

/// <summary>
/// Recipe 16, a child per customer, capped (spec D-17): a parallel <c>ForEach</c> with
/// <c>maxConcurrency</c> invokes a child workflow per customer. No more than the cap run at once, a
/// queued customer's child has not started, and the results land in customer order.
/// </summary>
public static class ChargeCustomersRecipe
{
    public const string ChildName = "ChargeCustomer";

    public static WorkflowDefinition Child() =>
        Workflow.Create<CustomerCharge>(ChildName)
            .Step<ValidateOrder>()
            .Request<RequestApproval>("payment", step => step
                .Input(activity => activity.Approver).From(ctx => ctx.WorkflowData.Customer)
                .Input(activity => activity.OrderId).From(ctx => "charge-" + ctx.WorkflowData.Customer)
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount))
            .WaitFor<ApprovalDecision>("payment", configure: wait => wait
                .Output(decision => decision.Approved).To(ctx => ctx.WorkflowData.Paid))
            .Build();

    public static WorkflowDefinition Parent(int maxConcurrency) =>
        Workflow.Create<Billing>("ChargeCustomers")
            .Step<ValidateOrder>()
            .ForEach(ctx => ctx.WorkflowData.Customers,
                (body, customer) => body
                    .Invoke<CustomerCharge>(ChildName, 1, child => child
                        .Input(charge => charge.Customer).From(ctx => ctx.Item(customer))
                        .Input(charge => charge.Amount).From(_ => 10m))
                    .Result(ctx => ctx.PreviousStep.Paid),
                setup => setup.As("customer").Parallel(maxConcurrency).Into(ctx => ctx.WorkflowData.Paid))
            .Build();
}

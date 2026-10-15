using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Recipes;

/// <summary>Sends approval requests, for example as emails with an approve and a reject link.</summary>
public interface IApprovalMailer
{
    /// <summary>
    /// Sends one request. <see cref="ApprovalRequest.ReplyToken"/> goes into the links; the page they
    /// open answers with <c>IWorkflowClient.ReplyAsync(token, decision)</c>.
    /// </summary>
    Task SendAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

public sealed record ApprovalRequest(string Approver, string OrderId, decimal Amount, string ReplyToken);

/// <summary>An approver's answer. It carries only the answer, never what was asked.</summary>
public sealed class ApprovalDecision
{
    public bool Approved { get; set; }
    public string By { get; set; } = "";
}

/// <summary>Asks one approver; the answer can only come back with this request's token.</summary>
public sealed class RequestApproval(IApprovalMailer mailer) : IRequestActivity<ApprovalDecision>
{
    public string Approver { get; set; } = "";
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        mailer.SendAsync(new ApprovalRequest(Approver, OrderId, Amount, context.Reply!.Token), cancellationToken);
}

public sealed class Order
{
    public string OrderId { get; set; } = "";
    public decimal Amount { get; set; }
    public bool Approved { get; set; }
    public string ApprovedBy { get; set; } = "";
    public bool ManagerApproved { get; set; }
    public bool DirectorApproved { get; set; }
    public bool Escalated { get; set; }
    public string Outcome { get; set; } = "";
    public string TrackingNumber { get; set; } = "";
}

/// <summary>
/// Recipe 1, one approver (spec D-17): ask a manager and wait for the decision, with a timeout path.
/// The request names the order and amount as its subject, so an answer can only confirm what the
/// manager was shown. After approval the order waits to be dispatched.
/// </summary>
public static class OneApproverRecipe
{
    public static WorkflowDefinition Definition(TimeSpan approvalTimeout) =>
        Workflow.Create<Order>("OneApprover")
            .Step<ValidateOrder>()
            .Request<RequestApproval>("approval", step => step
                    .Input(activity => activity.Approver).From(_ => "manager")
                    .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount),
                subject: ctx => $"{ctx.WorkflowData.OrderId}:{ctx.WorkflowData.Amount}")
            .WaitFor<ApprovalDecision>("approval", configure: wait => wait
                .Output(decision => decision.Approved).To(ctx => ctx.WorkflowData.Approved)
                .Output(decision => decision.By).To(ctx => ctx.WorkflowData.ApprovedBy)
                .TimeoutAfter(approvalTimeout)
                .OnTimeout(path => path.Step<Escalate>(step => step
                    .Output(activity => activity.Escalated).To(ctx => ctx.WorkflowData.Escalated))))
            .If(ctx => ctx.WorkflowData.Approved,
                then => then.Step<Decide>(step => step
                    .Input(activity => activity.Value).From(_ => "approved")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)),
                @else => @else.Step<Decide>(step => step
                    .Input(activity => activity.Value).From(_ => "rejected")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))
            .WaitFor<OrderDispatched>("dispatched")
            .Build();
}

/// <summary>
/// Recipe 3, any one of several approvers (spec D-17): one request and wait per branch, and a join that
/// continues on the first approval. A rejection does not end it; once someone approves, the other
/// branch is cancelled and its approver's late click is refused as cancelled.
/// </summary>
public static class AnyApproverRecipe
{
    public static WorkflowDefinition Definition(TimeSpan approvalTimeout) =>
        Workflow.Create<Order>("AnyApprover")
            .Step<ValidateOrder>()
            .Parallel(parallel => parallel
                .Do(manager => manager
                    .Request<RequestApproval>("approval", step => step
                        .Input(activity => activity.Approver).From(_ => "manager")
                        .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount))
                    .WaitFor<ApprovalDecision>("approval", configure: wait => wait
                        .Output(decision => decision.Approved).To(ctx => ctx.WorkflowData.ManagerApproved)
                        .TimeoutAfter(approvalTimeout)
                        .OnTimeout(path => path.Step<ValidateOrder>())))
                .Do(director => director
                    .Request<RequestApproval>("approval", step => step
                        .Input(activity => activity.Approver).From(_ => "director")
                        .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount))
                    .WaitFor<ApprovalDecision>("approval", configure: wait => wait
                        .Output(decision => decision.Approved).To(ctx => ctx.WorkflowData.DirectorApproved)
                        .TimeoutAfter(approvalTimeout)
                        .OnTimeout(path => path.Step<ValidateOrder>())))
                .WaitConditionally(order => order.ManagerApproved || order.DirectorApproved))
            .If(ctx => ctx.WorkflowData.ManagerApproved || ctx.WorkflowData.DirectorApproved,
                then => then.Step<Decide>(step => step
                    .Input(activity => activity.Value).From(_ => "approved")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)),
                @else => @else.Step<Decide>(step => step
                    .Input(activity => activity.Value).From(_ => "rejected")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))
            .Build();
}

/// <summary>
/// Recipe 4, every approver (spec D-17): one request and wait per branch with the default join, so the
/// order continues only when both have answered, in whatever order they answer.
/// </summary>
public static class EveryApproverRecipe
{
    public static WorkflowDefinition Definition() =>
        Workflow.Create<Order>("EveryApprover")
            .Step<ValidateOrder>()
            .Parallel(parallel => parallel
                .Do(manager => manager
                    .Request<RequestApproval>("approval", step => step
                        .Input(activity => activity.Approver).From(_ => "manager")
                        .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                    .WaitFor<ApprovalDecision>("approval", configure: wait => wait
                        .Output(decision => decision.Approved).To(ctx => ctx.WorkflowData.ManagerApproved)))
                .Do(director => director
                    .Request<RequestApproval>("approval", step => step
                        .Input(activity => activity.Approver).From(_ => "director")
                        .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId))
                    .WaitFor<ApprovalDecision>("approval", configure: wait => wait
                        .Output(decision => decision.Approved).To(ctx => ctx.WorkflowData.DirectorApproved))))
            .Step<Decide>(step => step
                .Input(activity => activity.Value).From(_ => "both answered")
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))
            .Build();
}

public sealed class OrderLines
{
    public string OrderId { get; set; } = "";
    public List<decimal> Lines { get; set; } = [];
    public int Next { get; set; }
    public int Approved { get; set; }
}

/// <summary>
/// Recipe 5, an approval per loop iteration (spec D-17): each order line is approved on its own. Each
/// iteration sends its own request, so an earlier line's token, or its approval sent again, can never
/// approve a later line.
/// </summary>
public static class ApprovalPerLineRecipe
{
    public static WorkflowDefinition Definition() =>
        Workflow.Create<OrderLines>("ApprovalPerLine")
            .Step<ValidateOrder>()
            .WhileDo(ctx => ctx.WorkflowData.Next < ctx.WorkflowData.Lines.Count, line => line
                .Request<RequestApproval>("line", step => step
                    .Input(activity => activity.Approver).From(_ => "manager")
                    .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                    .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Lines[ctx.WorkflowData.Next]))
                .WaitFor<ApprovalDecision>("line")
                .Step<CountLine>(step => step
                    .Input(activity => activity.Next).From(ctx => ctx.WorkflowData.Next)
                    .Input(activity => activity.Approved).From(ctx => ctx.WorkflowData.Approved)
                    .Input(activity => activity.Decision).From(ctx => ctx.PreviousStep.Approved)
                    .Output(activity => activity.Next).To(ctx => ctx.WorkflowData.Next)
                    .Output(activity => activity.Approved).To(ctx => ctx.WorkflowData.Approved)))
            .Build();
}

public sealed class OrderDispatched
{
    public string Carrier { get; set; } = "";
}

public sealed class ValidateOrder : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class Escalate : IAsyncActivity
{
    public bool Escalated { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Escalated = true;
        return Task.CompletedTask;
    }
}

public sealed class Decide : IAsyncActivity
{
    public string Value { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class CountLine : IAsyncActivity
{
    public int Next { get; set; }
    public int Approved { get; set; }
    public bool Decision { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Next++;
        if (Decision)
            Approved++;
        return Task.CompletedTask;
    }
}

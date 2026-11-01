using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class OrderData
{
    public string OrderId { get; set; } = "A-100";
    public decimal Amount { get; set; } = 250m;
    public bool Approved { get; set; }
    public string Outcome { get; set; } = "";
}

public sealed class OrderDecision
{
    public bool Approved { get; set; }
    public string By { get; set; } = "";
}

/// <summary>Tells the approver about the order. A real activity would send a mail or a chat message.</summary>
[WorkflowActivity("samples.notify-approver")]
public sealed class NotifyApprover : IAsyncActivity
{
    [ActivityInput] public string OrderId { get; set; } = "";
    [ActivityInput] public decimal Amount { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>Waits up to two days for a manager's decision, escalating when nobody answers.</summary>
public static class OrderApproval
{
    // snippet: order-approval
    public static WorkflowDefinition Definition() =>
        Workflow.Create<OrderData>("OrderApproval")
            .Step<NotifyApprover>(step => step
                .Input(activity => activity.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                .Input(activity => activity.Amount).From(ctx => ctx.WorkflowData.Amount))
            .WaitFor<OrderDecision>("approval", configure: wait => wait
                .Output(decision => decision.Approved).To(ctx => ctx.WorkflowData.Approved)
                .TimeoutAfter(TimeSpan.FromDays(2))
                .OnTimeout(timeout => timeout.Step<Label>(step => step
                    .Input(activity => activity.Value).From(ctx => "escalated")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))))
            .If(ctx => ctx.WorkflowData.Approved,
                then => then.Step<Label>(step => step
                    .Input(activity => activity.Value).From(ctx => "approved")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))
            .Build();

    // end-snippet: order-approval
}
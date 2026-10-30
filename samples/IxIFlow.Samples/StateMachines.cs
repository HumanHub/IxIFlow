using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public static class StateMachines
{
    // snippet: approval-state-machine
    public static WorkflowDefinition Definition() =>
        Workflow.Create<Approval>("ApproveOrder")
            .StateMachine(m => m.Start("pending")
                .State("pending", s => s
                    .Entry(e => e.Request<SendApprovalMail>("decision"))
                    .Exit(e => e.Step<LogExit>())
                    .OnReply<Decision>("decision", t => t
                        .Output(d => d.Approved).To(ctx => ctx.WorkflowData.Approved)
                        .When(ctx => ctx.PreviousStep.Approved, to: "approved",
                            a => a.Step<RecordDecision>())
                        .Otherwise(to: "rejected"))
                    .After(TimeSpan.FromDays(3), to: "escalated")
                    .On<CancelRequested>("cancel", to: "cancelled"))
                .State("approved", s => s.Final().Entry(e => e.Step<Notify>()))
                .State("rejected", s => s.Final())
                .State("escalated", s => s.Final())
                .State("cancelled", s => s.Final()))
            .Build();

    // end-snippet: approval-state-machine

    public sealed class Approval
    {
        public bool Approved { get; set; }
        public bool ExitRecorded { get; set; }
        public bool DecisionRecorded { get; set; }
        public bool Notified { get; set; }
    }

    public sealed class Decision
    {
        public bool Approved { get; set; }
    }

    public sealed class CancelRequested;

    public sealed class Mailbox
    {
        public string ReplyToken { get; set; } = "";
    }

    public sealed class SendApprovalMail(Mailbox mailbox) : IRequestActivity<Decision>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            mailbox.ReplyToken = context.Reply!.Token;

            return Task.CompletedTask;
        }
    }

    public sealed class LogExit : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ((Approval)context.WorkflowData).ExitRecorded = true;

            return Task.CompletedTask;
        }
    }

    public sealed class RecordDecision : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ((Approval)context.WorkflowData).DecisionRecorded = true;

            return Task.CompletedTask;
        }
    }

    public sealed class Notify : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ((Approval)context.WorkflowData).Notified = true;

            return Task.CompletedTask;
        }
    }
}
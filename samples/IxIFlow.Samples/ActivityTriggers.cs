using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public static class ActivityTriggers
{
    // snippet: activity-trigger-state-machine
    public static WorkflowDefinition Definition() =>
        Workflow.Create<Review>("AutomatedReview")
            .StateMachine(m => m.Start("pending")
                .State("pending", s => s
                    .OnActivity<CheckRisk>(t => t
                        .Input(a => a.OrderId).From(ctx => ctx.WorkflowData.OrderId)
                        .Output(a => a.Approved).To(ctx => ctx.WorkflowData.Approved)
                        .When(ctx => ctx.PreviousStep.Approved, to: "approved")
                        .Otherwise(to: "manual"))
                    .On<Decision>("decision", t => t
                        .Output(d => d.Approved).To(ctx => ctx.WorkflowData.Approved)
                        .When(ctx => ctx.PreviousStep.Approved, to: "approved")
                        .Otherwise(to: "manual"))
                    .After(TimeSpan.FromDays(1), to: "manual"))
                .State("approved", s => s.Final())
                .State("manual", s => s.Final()))
            .Build();

    // end-snippet: activity-trigger-state-machine

    public sealed class Review
    {
        public string OrderId { get; set; } = "order-1";
        public bool Approved { get; set; }
    }

    public sealed class Decision
    {
        public bool Approved { get; set; }
    }

    public sealed class CheckRisk : IAsyncActivity
    {
        public string OrderId { get; set; } = "";
        public bool Approved { get; set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Approved = OrderId.Length > 0;

            return Task.CompletedTask;
        }
    }
}
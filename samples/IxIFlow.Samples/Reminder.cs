using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public sealed class ReminderData
{
    public DateTimeOffset SentAt { get; set; }
}

[WorkflowActivity("samples.stamp-time")]
public sealed class StampTime : IAsyncActivity
{
    [ActivityInputOutput]
    public DateTimeOffset At { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>A delay parks the instance. The next mapping reads the workflow clock of that run.</summary>
public static class Reminder
{
    public static WorkflowDefinition Definition() =>
        new WorkflowBuilder<ReminderData, object>([], "Reminder")
            .Delay(TimeSpan.FromHours(1))
            .Step<StampTime>(step => step
                .Input(activity => activity.At).From(ctx => ctx.UtcNow)
                .Output(activity => activity.At).To(ctx => ctx.WorkflowData.SentAt))
            .Build();
}

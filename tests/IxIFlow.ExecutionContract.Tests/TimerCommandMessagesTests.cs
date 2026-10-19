using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.ContractTests;

[Trait("Feature", "host")]
[Trait("Spec", "G-09")]
public sealed class TimerCommandMessagesTests
{
    [Theory]
    [InlineData(DebugCommandKind.FreezeTimers)]
    [InlineData(DebugCommandKind.UnfreezeTimers)]
    public async Task OrderedTimerCommandsAndStoreRepliesCrossTheMessageBoundary(DebugCommandKind kind)
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<MessageHarness.Data>("TimerMessages").Step<MessageHarness.Noop>().Build());
        var command = new DebugCommand("timer", 7, kind, 3, "timer-actor", 11);
        var state = new WorkflowDebugState(8, 3, [command]);
        var assignment = harness.Cross(assign with { DebugState = state, DebuggingEnabled = true });
        Assert.Equal(command, Assert.Single(assignment.DebugState!.Commands));
        var delivery = harness.Cross(new DebugCommands(assign.AssignmentId, state));
        Assert.Equal(command, Assert.Single(delivery.State.Commands));
        var now = DateTime.UtcNow;
        var timers = new TimerCommitResult(new(3, 7)
        {
            Timers = new(now, new Dictionary<string, DateTime> { ["wait"] = now.AddSeconds(1) },
                new Dictionary<string, FrozenRetry> { ["branch"] = new(now.AddMinutes(1), now.AddSeconds(2)) })
                { ExpiredWaits = ["expired"], ExpiredRetries = new Dictionary<string, DateTime> { ["old-retry"] = now.AddMinutes(-1) } }
        }, new Dictionary<string, DateTime> { ["wait"] = now.AddMinutes(2) },
            new Dictionary<string, DateTime> { ["branch"] = now.AddMinutes(3) });
        var reply = harness.Cross(new CheckpointReply(assign.AssignmentId, CheckpointReplyKind.Ack, 11, Timers: timers));
        Assert.Equal(now, reply.Timers!.Debug.Timers!.StartedAtUtc);
        Assert.Equal(timers.Debug.Timers!.WaitAnchors, reply.Timers.Debug.Timers.WaitAnchors);
        Assert.Equal(timers.Debug.Timers.RetryAnchors, reply.Timers.Debug.Timers.RetryAnchors);
        Assert.Equal(timers.Debug.Timers.ExpiredWaits, reply.Timers.Debug.Timers.ExpiredWaits);
        Assert.Equal(timers.Debug.Timers.ExpiredRetries, reply.Timers.Debug.Timers.ExpiredRetries);
        Assert.Equal(timers.Deadlines, reply.Timers.Deadlines);
        Assert.Equal(timers.RetryDeadlines, reply.Timers.RetryDeadlines);
        var page = harness.Cross(new ReadInboxReply(assign.AssignmentId, new([], 0, new Dictionary<string, long>(), timers.Deadlines, now.AddHours(1), true)));
        Assert.False(page.Page!.IsDue("wait"));
        Assert.True(page.Page.TimersFrozen);
    }
}

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The engine's OpenTelemetry signals, through <see cref="System.Diagnostics"/> only: an
/// <see cref="ActivitySource"/> and a <see cref="Meter"/> both named <c>IxIFlow</c>, which an application adds to its
/// OpenTelemetry setup. Spans carry the instance, step, invocation and attempt; metrics carry only the workflow's name
/// and version, since an ID would make a series per instance.
/// </summary>
internal static class Telemetry
{
    public const string Name = "IxIFlow";

    public const string WorkflowName = "ixiflow.workflow.name";
    public const string WorkflowVersion = "ixiflow.workflow.version";
    public const string InstanceId = "ixiflow.instance.id";
    public const string StepId = "ixiflow.step.id";
    public const string InvocationId = "ixiflow.invocation.id";
    public const string AttemptId = "ixiflow.attempt.id";
    public const string AttemptNumber = "ixiflow.attempt.number";
    public const string ActivityType = "ixiflow.activity.type";
    public const string PublisherName = "ixiflow.publisher.name";
    public const string Outcome = "ixiflow.outcome";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    public static readonly UpDownCounter<long> ActiveRuns = Meter.CreateUpDownCounter<long>(
        "ixiflow.run.active", "{run}", "Runs of instances in progress on this host");

    public static readonly Histogram<double> ActivityDuration = Meter.CreateHistogram<double>(
        "ixiflow.activity.duration", "s", "How long an activity attempt ran, by activity type and outcome");

    public static readonly Histogram<double> CommitDuration = Meter.CreateHistogram<double>(
        "ixiflow.commit.duration", "s", "How long a checkpoint commit took, resends included");

    public static readonly Histogram<long> CommitSize = Meter.CreateHistogram<long>(
        "ixiflow.commit.size", "By", "The size of a committed checkpoint as stored, as the checkpoint limit counts it");

    public static readonly Counter<long> LeasesLost = Meter.CreateCounter<long>(
        "ixiflow.lease.lost", "{lease}", "Execution leases this host lost while it ran an instance");

    public static readonly Histogram<double> BacklogAge = Meter.CreateHistogram<double>(
        "ixiflow.backlog.age", "s", "How long an instance was runnable before a host claimed it");

    public static readonly Gauge<long> Unschedulable = Meter.CreateGauge<long>(
        "ixiflow.backlog.unschedulable", "{instance}", "Due, unleased instances no live host can run, last complete scan");

    public static readonly Gauge<double> UnschedulableAge = Meter.CreateGauge<double>(
        "ixiflow.backlog.unschedulable.age", "s", "Age of the oldest unschedulable instance, last complete scan");

    public static readonly Counter<long> OutboxDeliveries = Meter.CreateCounter<long>(
        "ixiflow.outbox.delivery", "{delivery}", "Outbox attempts settled, by publisher and outcome");

    public static readonly Counter<long> OutboxSettleFailures = Meter.CreateCounter<long>(
        "ixiflow.outbox.settle_failure", "{delivery}", "Successful publishes whose settlement failed or was rejected");

    public static readonly Counter<long> RetentionSkippedUnattempted = Meter.CreateCounter<long>(
        "ixiflow.retention.skipped_unattempted", "{instance}",
        "Finished candidates skipped per pass because a publisher has never attempted their pending deliveries");

    public static readonly Histogram<double> OutboxAge = Meter.CreateHistogram<double>(
        "ixiflow.outbox.age", "s", "Age of an outbox event when delivery is attempted");

    public static TagList Workflow(WorkflowInstance instance) => new()
    {
        { WorkflowName, instance.WorkflowName },
        { WorkflowVersion, instance.WorkflowVersion }
    };

    /// <summary>Tags a span with the instance it works on.</summary>
    public static void Tag(Activity? span, WorkflowInstance instance)
    {
        if (span == null)
            return;
        span.SetTag(WorkflowName, instance.WorkflowName);
        span.SetTag(WorkflowVersion, instance.WorkflowVersion);
        span.SetTag(InstanceId, instance.InstanceId);
    }

    /// <summary>Counts a run as active until disposed.</summary>
    public static IDisposable Running(WorkflowInstance instance)
    {
        var tags = Workflow(instance);
        ActiveRuns.Add(1, tags);
        return new RunEnded(tags);
    }

    private sealed class RunEnded(TagList tags) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
                ActiveRuns.Add(-1, tags);
        }
    }

    /// <summary>Ends an activity attempt's span when its task does, and records how long it ran and how it ended.</summary>
    public static async Task<ActivityRunResult> AttemptEndedAsync(Task<ActivityRunResult> attempt, Activity? span,
        WorkflowInstance instance, string activityType, long startedAt)
    {
        var outcome = "Faulted";
        try
        {
            var result = await attempt;
            outcome = result.Kind.ToString();
            if (result.Error is { } error)
                Fail(span, error);
            return result;
        }
        catch (Exception error)
        {
            Fail(span, error);
            throw;
        }
        finally
        {
            span?.SetTag(Outcome, outcome);
            span?.Dispose();
            var tags = Workflow(instance);
            tags.Add(ActivityType, activityType);
            tags.Add(Outcome, outcome);
            ActivityDuration.Record(Stopwatch.GetElapsedTime(startedAt).TotalSeconds, tags);
        }
    }

    public static void Fail(Activity? span, Exception error)
    {
        span?.SetStatus(ActivityStatusCode.Error, error.Message);
        span?.AddException(error);
    }
}

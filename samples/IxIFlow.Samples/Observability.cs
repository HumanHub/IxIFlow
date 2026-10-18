using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using IxIFlow.Core;

namespace IxIFlow.Samples;

/// <summary>Applications can attach listeners or configure OpenTelemetry for the IxIFlow source and meter.</summary>
public sealed class Observability : IDisposable
{
    private readonly ActivityListener _activities;
    private readonly MeterListener _metrics = new();
    public ConcurrentQueue<string> Spans { get; } = new();
    public ConcurrentQueue<string> Measurements { get; } = new();

    public Observability()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "IxIFlow",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => Spans.Enqueue(activity.OperationName)
        };
        ActivitySource.AddActivityListener(_activities);
        _metrics.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "IxIFlow")
                listener.EnableMeasurementEvents(instrument);
        };
        _metrics.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
            Measurements.Enqueue(instrument.Name));
        _metrics.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
            Measurements.Enqueue(instrument.Name));
        _metrics.Start();
    }

    public static async Task<(IReadOnlyList<ExecutionTraceEntry> Trace, WorkflowExecutionSnapshot? Snapshot)>
        InspectAsync(IWorkflowClient client, string instanceId) =>
        (await client.GetTraceAsync(instanceId), await client.GetSnapshotAsync(instanceId));

    public void Dispose()
    {
        _metrics.Dispose();
        _activities.Dispose();
    }
}

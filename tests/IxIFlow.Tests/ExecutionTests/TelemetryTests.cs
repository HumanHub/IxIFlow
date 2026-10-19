using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The engine emits OpenTelemetry through <c>System.Diagnostics</c>: spans for each run pass, activity attempt and
/// commit, tagged with the instance, step, invocation and attempt; metrics tagged only with the workflow's name and
/// version (G-02, D-39).
/// </summary>
[Trait("Feature", "tracing")]
[Trait("Spec", "G-02")]
public sealed class TelemetryTests
{
    private static readonly ActivitySource Own = new("IxIFlow.Tests.Telemetry");

    [Fact]
    public async Task ARunHasASpanWithItsAttemptsAndCommitsInside()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "IxIFlow" or "IxIFlow.Tests.Telemetry",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };
        ActivitySource.AddActivityListener(listener);
        var name = "Traced" + Guid.NewGuid().ToString("N");

        var result = await Run(name);

        var mine = spans.Where(span => span.GetTagItem("ixiflow.instance.id") as string == result.InstanceId).ToList();
        var run = Assert.Single(mine, span => span.DisplayName == $"run {name}");
        var attempts = mine.Where(span => span.DisplayName == $"activity {nameof(Works)}").ToList();
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, attempt =>
        {
            Assert.Equal(run.SpanId, attempt.ParentSpanId);
            Assert.NotNull(attempt.GetTagItem("ixiflow.step.id"));
            Assert.NotNull(attempt.GetTagItem("ixiflow.invocation.id"));
            Assert.NotNull(attempt.GetTagItem("ixiflow.attempt.id"));
            Assert.Equal(1, attempt.GetTagItem("ixiflow.attempt.number"));
            Assert.Equal("Returned", attempt.GetTagItem("ixiflow.outcome"));
        });
        Assert.Contains(mine, span => span.DisplayName == "commit" && span.ParentSpanId == run.SpanId);
        // The activity's own spans nest in its attempt's.
        var own = spans.Where(span => span.Source.Name == "IxIFlow.Tests.Telemetry" &&
                                      span.GetTagItem("workflow") as string == name).ToList();
        Assert.Equal(2, own.Count);
        Assert.All(own, span => Assert.Contains(attempts, attempt => attempt.SpanId == span.ParentSpanId));
    }

    [Fact]
    public async Task MetricsCarryTheWorkflowButNoInstance()
    {
        var measurements = new ConcurrentBag<(string Instrument, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meter) =>
            {
                if (instrument.Meter.Name == "IxIFlow")
                    meter.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, Tags(tags))));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, Tags(tags))));
        listener.Start();
        var name = "Measured" + Guid.NewGuid().ToString("N");

        await Run(name);

        var mine = measurements.Where(item => item.Tags.TryGetValue("ixiflow.workflow.name", out var value) &&
                                              Equals(value, name)).ToList();
        Assert.Equal(2, mine.Count(item => item.Instrument == "ixiflow.activity.duration" &&
                                           Equals(item.Tags["ixiflow.outcome"], "Returned") &&
                                           Equals(item.Tags["ixiflow.activity.type"], nameof(Works))));
        Assert.Contains(mine, item => item.Instrument == "ixiflow.commit.duration");
        Assert.Contains(mine, item => item.Instrument == "ixiflow.commit.size" && item.Value > 0);
        Assert.Equal(0, mine.Where(item => item.Instrument == "ixiflow.run.active").Sum(item => item.Value));
        Assert.Contains(mine, item => item.Instrument == "ixiflow.run.active" && item.Value == 1);
        Assert.All(mine, item =>
        {
            Assert.Equal(1, item.Tags["ixiflow.workflow.version"]);
            Assert.DoesNotContain(item.Tags.Keys, key => key.EndsWith(".id", StringComparison.Ordinal));
        });
    }

    private static async Task<WorkflowExecutionResult> Run(string name)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(
            Workflow.Create<Data>(name)
                .Step<Works>(step => step.Input(activity => activity.Workflow).From(ctx => ctx.WorkflowData.Name))
                .Step<Works>(step => step.Input(activity => activity.Workflow).From(ctx => ctx.WorkflowData.Name))
                .Build(),
            new Data { Name = name });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        return result;
    }

    private static Dictionary<string, object?> Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var all = new Dictionary<string, object?>();
        foreach (var (key, value) in tags)
            all[key] = value;
        return all;
    }

    public sealed class Data
    {
        public string Name { get; set; } = "";
    }

    /// <summary>Starts a span of its own, as an activity calling an instrumented library would.</summary>
    public sealed class Works : IAsyncActivity
    {
        public string Workflow { get; set; } = "";

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            using var span = Own.StartActivity("work");
            span?.SetTag("workflow", Workflow);
        }
    }
}

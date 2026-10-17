using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The values a log scope carries (G-03). A run logs inside a scope with its instance, and an activity attempt inside
/// one with its step and invocation, so every entry written meanwhile, by the engine or by the activity's own logger,
/// carries them. Providers that keep structure read the pairs; others read the "Key:Value" text.
/// </summary>
internal sealed class LogScope : IReadOnlyList<KeyValuePair<string, object?>>
{
    public const string InstanceId = "InstanceId";
    public const string WorkflowName = "WorkflowName";
    public const string StepId = "StepId";
    public const string InvocationId = "InvocationId";

    private readonly KeyValuePair<string, object?>[] _values;

    private LogScope(params KeyValuePair<string, object?>[] values) => _values = values;

    public static LogScope ForInstance(WorkflowInstance instance) =>
        new(new(InstanceId, instance.InstanceId), new(WorkflowName, instance.WorkflowName));

    public static LogScope ForStep(string stepId, string? invocationId) => invocationId == null
        ? new(new KeyValuePair<string, object?>(StepId, stepId))
        : new(new(StepId, stepId), new(InvocationId, invocationId));

    /// <summary>The engine's logger, or none when the application registers no logging.</summary>
    public static ILogger Logger(IServiceProvider services) =>
        services.GetService<ILoggerFactory>()?.CreateLogger("IxIFlow.Execution") ?? NullLogger.Instance;

    public KeyValuePair<string, object?> this[int index] => _values[index];
    public int Count => _values.Length;

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, object?>>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => string.Join(", ", _values.Select(value => $"{value.Key}:{value.Value}"));
}

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The clock of the run evaluating a workflow's expressions. A run sets it for everything it evaluates, so
/// <see cref="WorkflowContext{TWorkflowData}.UtcNow"/> reads the engine's <see cref="TimeProvider"/>, fake time in
/// tests included, and never the machine's clock.
/// </summary>
internal static class RunClock
{
    private static readonly AsyncLocal<TimeProvider?> CurrentClock = new();

    public static TimeProvider Current => CurrentClock.Value
        ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
            "The run's clock is read only while a run evaluates the workflow's expressions");

    /// <summary>Makes <paramref name="clock"/> the run's clock until disposed.</summary>
    public static IDisposable Use(TimeProvider clock)
    {
        var previous = CurrentClock.Value;
        CurrentClock.Value = clock;
        return new Restore(previous);
    }

    private sealed class Restore(TimeProvider? previous) : IDisposable
    {
        public void Dispose() => CurrentClock.Value = previous;
    }
}

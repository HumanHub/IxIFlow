namespace IxIFlow.Core;

/// <summary>
/// An activity that can resolve an invocation whose result was not committed before execution stopped.
/// The activity receives the same invocation ID and mapped inputs during recovery.
/// </summary>
public interface IRecoverableActivity : IAsyncActivity
{
    object? CaptureRecoveryStateObject(IActivityContext context);

    Task<ActivityRecoveryResolution> RecoverObjectAsync(
        object? state,
        IActivityContext context,
        CancellationToken cancellationToken = default);
}

public enum ActivityRecoveryDisposition
{
    Completed,
    Execute,
    Unresolved,
    Faulted
}

public sealed record ActivityRecoveryResolution(
    ActivityRecoveryDisposition Disposition,
    string? Reason = null,
    Exception? Error = null);

public interface IRecoverableActivity<TRecoveryState> : IRecoverableActivity
{
    /// <summary>
    /// Captures the information needed for recovery before the activity is invoked.
    /// This method must not perform an external effect.
    /// </summary>
    TRecoveryState CaptureRecoveryState(IActivityContext context);

    /// <summary>
    /// Resolves a prior invocation. A completed result must restore this activity's output properties.
    /// An exception thrown by this method leaves the invocation unresolved.
    /// </summary>
    Task<ActivityRecoveryResult<TRecoveryState>> RecoverAsync(
        TRecoveryState state,
        IActivityContext context,
        CancellationToken cancellationToken = default);

    object? IRecoverableActivity.CaptureRecoveryStateObject(IActivityContext context) =>
        CaptureRecoveryState(context);

    async Task<ActivityRecoveryResolution> IRecoverableActivity.RecoverObjectAsync(
        object? state,
        IActivityContext context,
        CancellationToken cancellationToken)
    {
        if (state is not TRecoveryState typedState)
            throw new InvalidOperationException(
                $"Saved recovery state for '{GetType().Name}' has the wrong type");
        var result = await RecoverAsync(typedState, context, cancellationToken);
        return result switch
        {
            ActivityRecoveryResult<TRecoveryState>.Completed =>
                new(ActivityRecoveryDisposition.Completed),
            ActivityRecoveryResult<TRecoveryState>.Execute =>
                new(ActivityRecoveryDisposition.Execute),
            ActivityRecoveryResult<TRecoveryState>.Unresolved unresolved =>
                new(ActivityRecoveryDisposition.Unresolved, Reason: unresolved.Reason),
            ActivityRecoveryResult<TRecoveryState>.Faulted faulted =>
                new(ActivityRecoveryDisposition.Faulted, Error: faulted.Error),
            _ => throw new InvalidOperationException("Unknown activity recovery result")
        };
    }
}

/// <summary>
/// The activity's decision about an invocation with a committed Start and no committed End.
/// </summary>
public abstract record ActivityRecoveryResult<TRecoveryState>
{
    /// <summary>The activity has restored the original output properties.</summary>
    public sealed record Completed : ActivityRecoveryResult<TRecoveryState>;

    /// <summary>The activity author permits another ExecuteAsync attempt of the same invocation.</summary>
    public sealed record Execute : ActivityRecoveryResult<TRecoveryState>;

    /// <summary>The outcome cannot yet be determined safely.</summary>
    public sealed record Unresolved(string Reason) : ActivityRecoveryResult<TRecoveryState>;

    /// <summary>The activity recovered an exception to pass through workflow fault handling.</summary>
    public sealed record Faulted(Exception Error) : ActivityRecoveryResult<TRecoveryState>;
}

public static class ActivityRecoveryContextExtensions
{
    /// <summary>Gets the state supplied by CaptureRecoveryState or RecoverAsync for this attempt.</summary>
    public static TRecoveryState GetRecoveryState<TRecoveryState>(this IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RecoveryState is TRecoveryState state
            ? state
            : throw new InvalidOperationException(
                $"Recovery state of type '{typeof(TRecoveryState).Name}' is unavailable");
    }
}

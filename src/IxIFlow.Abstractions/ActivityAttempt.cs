namespace IxIFlow.Core;

/// <summary>
/// The attempt an activity runs in, and the attempts of its invocation before it. A step retry starts from the
/// inputs the invocation saved, so an attempt that must do something different learns why the earlier ones failed
/// from here: which host they ran on, the error they ended with, and the note they left.
/// </summary>
public sealed class ActivityAttempt
{
    public ActivityAttempt(int number, IReadOnlyList<PreviousActivityAttempt> previous)
    {
        if (number < 1)
            throw new ArgumentOutOfRangeException(nameof(number), "The first attempt is number 1");
        Number = number;
        Previous = previous ?? throw new ArgumentNullException(nameof(previous));
    }

    /// <summary>The first attempt of an invocation, with nothing before it.</summary>
    public static ActivityAttempt First => new(1, []);

    /// <summary>Which attempt of the invocation this is, the first being 1. A recovery after a crash is an attempt too.</summary>
    public int Number { get; }

    /// <summary>The invocation's earlier attempts, oldest first.</summary>
    public IReadOnlyList<PreviousActivityAttempt> Previous { get; }

    /// <summary>
    /// A value this attempt leaves for the next one, such as the endpoint it tried. It is saved with the attempt if the
    /// attempt fails, and the next attempt reads it in <see cref="Previous"/>. It must serialize to JSON.
    /// </summary>
    public object? Note { get; set; }
}

/// <summary>An earlier attempt of the same invocation.</summary>
/// <param name="AttemptId">The attempt's ID, as <see cref="IActivityContext.AttemptId"/> gave it.</param>
/// <param name="Host">The host the attempt ran on, as that host names itself.</param>
/// <param name="StartedAtUtc">When the attempt started.</param>
/// <param name="EndedAtUtc">When it ended; none when its host stopped before the attempt's end was saved.</param>
/// <param name="ErrorType">The type of the exception it ended with, if it threw.</param>
/// <param name="ErrorMessage">That exception's message.</param>
/// <param name="Note">The note it left, read back as the value it saved.</param>
public sealed record PreviousActivityAttempt(
    string AttemptId,
    string? Host,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    string? ErrorType,
    string? ErrorMessage,
    object? Note);

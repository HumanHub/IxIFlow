using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// A fake clock that records each time a timer is armed on it, by <see cref="CreateTimer"/> or by
/// <see cref="ITimer.Change"/> on a timer it created, with its due time; so a test can move the clock only once
/// the timer it means to drive is set. An arming is recorded after the fake clock has registered it: a test that
/// saw the record may advance at once, and the timer fires on that advance.
/// </summary>
/// <remarks>
/// An arming's <see cref="RecordedTimer.DueAt"/> is read from the clock as the timer is armed, so it is exact
/// while nothing advances the clock at the same moment; tests advance only after the waits below.
/// </remarks>
public sealed class TimerRecordingClock : FakeTimeProvider
{
    private static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly List<Arming> _armings = [];
    private int _timers;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TimerRecordingClock()
    {
    }

    public TimerRecordingClock(DateTimeOffset startDateTime) : base(startDateTime)
    {
    }

    /// <summary>How many times timers were armed so far; pass it as <c>after</c> to wait for a later arming.</summary>
    public int Armed
    {
        get
        {
            lock (_lock)
                return _armings.Count;
        }
    }

    /// <summary>Every arming so far, in order, each as it is now.</summary>
    public IReadOnlyList<RecordedTimer> Armings
    {
        get
        {
            lock (_lock)
                return _armings.Select(item => item.Snapshot()).ToArray();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        int id;
        lock (_lock)
            id = ++_timers;
        var timer = new RecordingTimer(this, id);
        var arming = timer.Arm(dueTime, period);
        // A timer due at once fires inside the base call, before its arming is published; it is then published
        // as fired.
        timer.Inner = base.CreateTimer(target =>
        {
            timer.Fire();
            callback(target);
        }, state, dueTime, period);
        Publish(arming);
        return timer;
    }

    /// <summary>
    /// Waits, for at most <paramref name="patience"/> of real time (ten seconds by default), until an arming after
    /// the first <paramref name="after"/> ones matches, and returns it as it then was.
    /// </summary>
    public async Task<RecordedTimer> WaitForTimerAsync(Func<RecordedTimer, bool> match, int after = 0,
        TimeSpan? patience = null, CancellationToken cancellationToken = default)
    {
        RecordedTimer? found = null;
        await WaitUntilAsync(armings => (found = armings.Skip(after).FirstOrDefault(match)) != null,
            $"a matching timer armed after the first {after}", patience, cancellationToken);
        return found!;
    }

    /// <summary>
    /// Waits, for at most <paramref name="patience"/> of real time (ten seconds by default), until a timer armed
    /// after the first <paramref name="after"/> armings is still to fire at <paramref name="dueAt"/> on this clock.
    /// An arming that already fired, or was stopped, replaced or disposed, does not count: something waits on it.
    /// </summary>
    public Task<RecordedTimer> WaitForTimerDueAtAsync(DateTimeOffset dueAt, int after = 0, TimeSpan? patience = null) =>
        WaitForTimerAsync(timer => timer.Pending && timer.DueAt == dueAt, after, patience);

    /// <summary>
    /// Waits, for at most <paramref name="patience"/> of real time (ten seconds by default), until the armings
    /// meet <paramref name="condition"/>. The condition is checked again each time a timer is armed, fires or is
    /// disposed. A wait that runs out throws a <see cref="TimeoutException"/> that lists the armings.
    /// </summary>
    public async Task WaitUntilAsync(Func<IReadOnlyList<RecordedTimer>, bool> condition, string description,
        TimeSpan? patience = null, CancellationToken cancellationToken = default)
    {
        var limit = patience ?? DefaultPatience;
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            Task changed;
            IReadOnlyList<RecordedTimer> armings;
            lock (_lock)
            {
                changed = _changed.Task;
                armings = _armings.Select(item => item.Snapshot()).ToArray();
            }
            if (condition(armings))
                return;
            var remaining = limit - elapsed.Elapsed;
            try
            {
                if (remaining <= TimeSpan.Zero)
                    throw new TimeoutException();
                await changed.WaitAsync(remaining, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Waited {limit} for {description}; the clock reads {GetUtcNow():O} and has these armings:" +
                    Environment.NewLine + string.Join(Environment.NewLine, Armings));
            }
        }
    }

    private void Publish(Arming arming)
    {
        lock (_lock)
        {
            arming.Number = _armings.Count + 1;
            _armings.Add(arming);
        }
        Signal();
    }

    private void Signal()
    {
        TaskCompletionSource changed;
        lock (_lock)
        {
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    /// <summary>One arming of a timer, as it was when read.</summary>
    /// <param name="Number">The order of this arming among all armings on the clock, from one.</param>
    /// <param name="Timer">Which timer was armed, numbered in the order the timers were created, from one.</param>
    /// <param name="DueTime">The delay it was armed with.</param>
    /// <param name="Period">Its period; <see cref="Timeout.InfiniteTimeSpan"/> for a timer that fires once.</param>
    /// <param name="SetAt">The clock's time when it was armed.</param>
    /// <param name="Firings">How many times it fired on this arming.</param>
    /// <param name="Replaced">Whether the timer was armed again since.</param>
    /// <param name="Disposed">Whether the timer was disposed.</param>
    public sealed record RecordedTimer(int Number, int Timer, TimeSpan DueTime, TimeSpan Period,
        DateTimeOffset SetAt, int Firings, bool Replaced, bool Disposed)
    {
        /// <summary>When it first fires on the clock; null for an arming that stops the timer.</summary>
        public DateTimeOffset? DueAt => DueTime == Timeout.InfiniteTimeSpan ? null : SetAt + DueTime;

        /// <summary>Whether it is still to fire: running, not replaced or disposed, and periodic or not yet fired.</summary>
        public bool Pending => DueAt != null && !Replaced && !Disposed &&
                               (Firings == 0 || Period != Timeout.InfiniteTimeSpan);

        public override string ToString() =>
            $"arming {Number} of timer {Timer}: due in {DueTime} at {DueAt:O}, set at {SetAt:O}, period {Period}, " +
            $"fired {Firings}, replaced {Replaced}, disposed {Disposed}";
    }

    // The state of one arming; guarded by the clock's lock.
    private sealed class Arming(int timer, TimeSpan dueTime, TimeSpan period, DateTimeOffset setAt)
    {
        public int Number;
        public int Firings;
        public bool Replaced;
        public bool Disposed;

        public RecordedTimer Snapshot() =>
            new(Number, timer, dueTime, period, setAt, Firings, Replaced, Disposed);
    }

    // The timer handed to the caller: it forwards to the fake clock's timer and records each arming.
    private sealed class RecordingTimer(TimerRecordingClock clock, int id) : ITimer
    {
        private Arming? _current;

        public ITimer Inner { get; set; } = null!;

        public Arming Arm(TimeSpan dueTime, TimeSpan period)
        {
            var arming = new Arming(id, dueTime, period, clock.GetUtcNow());
            lock (clock._lock)
            {
                if (_current != null)
                    _current.Replaced = true;
                _current = arming;
            }
            return arming;
        }

        public void Fire()
        {
            lock (clock._lock)
                _current!.Firings++;
            clock.Signal();
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var arming = Arm(dueTime, period);
            var changed = Inner.Change(dueTime, period);
            clock.Publish(arming);
            return changed;
        }

        public void Dispose()
        {
            Inner.Dispose();
            MarkDisposed();
        }

        public async ValueTask DisposeAsync()
        {
            await Inner.DisposeAsync();
            MarkDisposed();
        }

        private void MarkDisposed()
        {
            lock (clock._lock)
                _current!.Disposed = true;
            clock.Signal();
        }
    }
}

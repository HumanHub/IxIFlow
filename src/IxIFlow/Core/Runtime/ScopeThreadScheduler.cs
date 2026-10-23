using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace IxIFlow.Core.Runtime;

internal sealed class ScopeThreadLimitExceededException(int limit) : ResourceLimitExceededException($"The host's scope thread limit of {limit} is exhausted");

internal sealed class ScopeThreadRegistry(int maximum)
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public ScopeThreadScheduler Create()
    {
        while (true)
        {
            var count = Count;
            if (count >= maximum) throw new ScopeThreadLimitExceededException(maximum);
            if (Interlocked.CompareExchange(ref _count, count + 1, count) == count) break;
        }
        try { return new ScopeThreadScheduler(() => Interlocked.Decrement(ref _count)); }
        catch { Interlocked.Decrement(ref _count); throw; }
    }
}

/// <summary>A pass-local cooperative thread; both managed continuations and Windows messages are pumped.</summary>
internal sealed class ScopeThreadScheduler : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State, ExecutionContext? Context)> _posts = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly Action _released;
    private volatile bool _stop;
    private int _stopping;
    public bool IsCurrent => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;
    public bool Sealed { get; set; }

    public ScopeThreadScheduler(Action released)
    {
        _released = released;
        _thread = new Thread(Pump) { IsBackground = true, Name = "IxIFlow scope" };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public override void Post(SendOrPostCallback callback, object? state)
    {
        var operation = ScopeOperation.Current.Value;
        operation?.AddPost();
        _posts.Enqueue((value =>
        {
            try { callback(value); }
            finally { operation?.CompletePost(); }
        }, state, ExecutionContext.Capture()));
        _wake.Set();
    }

    public override void OperationStarted() => ScopeOperation.Current.Value?.AddPost();
    public override void OperationCompleted() => ScopeOperation.Current.Value?.CompletePost();

    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (!IsCurrent) throw new InvalidOperationException("Cross-thread synchronous scope dispatch is unsupported");
        callback(state);
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> callback, bool cleanup = false)
    {
        await _ready.Task.ConfigureAwait(false);
        if (Sealed && !cleanup) throw new InvalidOperationException("The scope thread is quarantined");
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try { completion.TrySetResult(await callback()); }
            catch (Exception error) { completion.TrySetException(error); }
        }, null);
        return await completion.Task.ConfigureAwait(false);
    }

    public Task StopAsync()
    {
        if (_ended.Task.IsCompleted) return _ended.Task;
        if (Interlocked.Exchange(ref _stopping, 1) == 0) { _stop = true; _wake.Set(); }
        return _ended.Task;
    }

    private void Pump()
    {
        var initialized = false;
        try
        {
            SetSynchronizationContext(this);
            if (OperatingSystem.IsWindows())
            {
                var result = OleInitialize(IntPtr.Zero);
                if (result < 0) Marshal.ThrowExceptionForHR(result);
                initialized = true;
            }
            _ready.TrySetResult();
            while (!_stop || !_posts.IsEmpty)
            {
                while (_posts.TryDequeue(out var post))
                {
                    if (post.Context == null) Invoke(post.Callback, post.State);
                    else ExecutionContext.Run(post.Context, _ => Invoke(post.Callback, post.State), null);
                }
                if (_stop) break;
                if (OperatingSystem.IsWindows())
                {
                    while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
                    {
                        TranslateMessage(ref message);
                        DispatchMessage(ref message);
                    }
                    MsgWaitForMultipleObjectsEx(1, [_wake.SafeWaitHandle.DangerousGetHandle()], 100, 0x04FF, 0x0004);
                }
                else _wake.WaitOne();
            }
        }
        catch (Exception error) { _ready.TrySetException(error); }
        finally
        {
            if (initialized) OleUninitialize();
            _released();
            _ended.TrySetResult();
            _wake.Dispose();
        }
    }

    private void Invoke(SendOrPostCallback callback, object? state)
    {
        SetSynchronizationContext(this);
        callback(state);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[] handles, uint milliseconds, uint wakeMask, uint flags);
}

internal sealed class ScopeOperation
{
    public static readonly AsyncLocal<ScopeOperation?> Current = new();
    public bool Active { get; set; } = true;
    private readonly object _gate = new();
    private int _posts;
    private TaskCompletionSource? _drained;
    public void AddPost() { lock (_gate) { if (_posts++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously); } }
    public void CompletePost() { lock (_gate) { if (--_posts == 0) _drained?.TrySetResult(); } }
    public Task DrainAsync() { lock (_gate) return _posts == 0 ? Task.CompletedTask : _drained!.Task; }

    public static void RequestCancellation(CancellationTokenSource cancellation)
    {
        var notification = cancellation.CancelAsync();
        _ = notification.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

internal sealed class ScopeValues(IReadOnlyList<ScopeFrameState> entries, ScopeOperation? operation = null)
{
    public static ScopeValues None { get; } = new([]);
    public bool HasScopes => entries.Count != 0;
    public ScopeOperation Operation { get; } = operation ?? new ScopeOperation();
    public ScopeValues Fresh() => new(entries);
    public ScopeThreadScheduler? Scheduler => entries.Select(entry => entry.Scheduler).FirstOrDefault(item => item != null);
    public T? Find<T>() where T : class
    {
        foreach (var entry in entries.Reverse())
            if (entry.Value is T value)
            {
                if (entry.Scheduler is { } scheduler && (!scheduler.IsCurrent || ScopeOperation.Current.Value is not { Active: true } current || !ReferenceEquals(current, Operation)))
                    throw new InvalidOperationException("An affine scope value is only available on its owning thread during its author operation");
                return value;
            }
        return null;
    }

    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> callback, CancellationToken cancellation, bool cleanup = false) =>
        RunAsync(async () =>
        {
            cancellation.ThrowIfCancellationRequested();
            var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Registered before author callbacks, so cancellation reaches this marker
            // after their synchronous notifications have posted resource cleanup.
            using var registration = cancellation.UnsafeRegister(_ => notified.TrySetResult(), null);
            try { return await callback(cancellation); }
            finally
            {
                if (cancellation.IsCancellationRequested) await notified.Task;
                await registration.DisposeAsync();
                await Operation.DrainAsync();
            }
        }, cleanup);

    public async Task<T> RunAsync<T>(Func<Task<T>> callback, bool cleanup = false)
    {
        async Task<T> Run()
        {
            var previous = ScopeOperation.Current.Value;
            Operation.Active = true;
            if (Scheduler != null) ScopeOperation.Current.Value = Operation;
            try { return await callback(); }
            finally { Operation.Active = false; ScopeOperation.Current.Value = previous; }
        }
        return Scheduler is { } scheduler ? await scheduler.RunAsync(Run, cleanup).ConfigureAwait(false)
            : HasScopes ? await Task.Run(Run).ConfigureAwait(false) : await Run().ConfigureAwait(false);
    }
}

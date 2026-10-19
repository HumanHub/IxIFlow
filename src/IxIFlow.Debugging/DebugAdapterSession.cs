using System.Security.Cryptography;
using System.Text;
using IxIFlow.Core;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;

namespace IxIFlow.Debugging;

/// <summary>One DAP conversation. Each connection owns its authentication, handles and replay cursor.</summary>
public sealed partial class DebugAdapterSession : DebugAdapterBase, IDisposable
{
    private readonly IWorkflowOperations _operations;
    private readonly DebugEndpoint _endpoint;
    private readonly bool _trustedPipe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _commands = [];
    private IDisposable? _subscription;
    private string? _instanceId;
    private bool _authenticated, _initialized, _configured, _disconnected, _continueOnDisconnect, _terminated;
    private int _lineBase = 1, _columnBase = 1;
    private long _traceSequence, _reportedStop = -1;
    private InstanceInspection? _inspection;
    private TraceReplay? _replay;
    private IReadOnlyList<StepTableEntry> _steps = [];
    private readonly Dictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _threadIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observedThreads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _breakpointIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<Core.Breakpoint>> _breakpointGroups = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId = RandomNumberGenerator.GetInt32(1, 1000000);
    private int _started;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Stream hosting requires the configured TCP token. Only the hosted pipe listener grants OS authentication.</summary>
    public DebugAdapterSession(IWorkflowOperations operations, DebuggingOptions options)
        : this(operations, options.Snapshot(), false) { }

    internal DebugAdapterSession(IWorkflowOperations operations, DebugEndpoint endpoint, bool trustedPipe)
        => (_operations, _endpoint, _trustedPipe) = (operations, endpoint, trustedPipe);

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("A DAP session accepts one connection");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        InitializeProtocolClient(input, output);
        Protocol.Run();
        var reader = Task.Run(() => Protocol.WaitForReader());
        var monitor = MonitorAsync(linked.Token);
        try { await Task.WhenAny(reader, _closed.Task).WaitAsync(linked.Token); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            _lifetime.Cancel();
            _subscription?.Dispose();
            // Drain the protocol library's queued disconnect response before closing the transport.
            Protocol.Stop(1000);
            // Closing the streams also interrupts a peer that never finishes its header.
            input.Dispose();
            if (!ReferenceEquals(input, output)) output.Dispose();
            await reader;
            try { await monitor; } catch (OperationCanceledException) { }
            Task[] commands;
            lock (_commands) commands = _commands.ToArray();
            await Task.WhenAll(commands);
        }
    }

    private void Wake()
    {
        try { _changed.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await _changed.WaitAsync(TimeSpan.FromMilliseconds(250), token);
            await _gate.WaitAsync(token);
            try
            {
                if (_authenticated && _instanceId != null && _configured && !_disconnected && _replay == null)
                    await RefreshAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                // Do not serialize engine exception text, operator notes or failed requests.
                if (!token.IsCancellationRequested) Emit<OutputEvent>(new { category = "stderr", output = "Workflow inspection is unavailable.\n" });
            }
            finally { _gate.Release(); }
        }
    }

    private async Task AuthorizeAsync(DebugOperation operation, CancellationToken token)
    {
        if (!_authenticated || _instanceId == null || _disconnected) throw new ProtocolException("Session is not authenticated and attached");
        if (!await _endpoint.Authorize(new(_endpoint.Actor, _instanceId, operation), token))
            throw new ProtocolException("Operation is not authorized");
    }

    private void Authenticate(JObject args)
    {
        var supplied = args.Value<string>("sessionToken") ?? "";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        if (!_trustedPipe && (_endpoint.TokenHash == null || !CryptographicOperations.FixedTimeEquals(_endpoint.TokenHash, digest)))
            throw new ProtocolException("Session authentication failed");
        _authenticated = true;
    }

    private async Task<InstanceInspection> InspectAsync(CancellationToken token)
    {
        await AuthorizeAsync(DebugOperation.Inspect, token);
        return _inspection = await _operations.InspectAsync(_instanceId!, token)
            ?? throw new ProtocolException("Instance is unavailable");
    }

    private async Task AttachAsync(JObject args, bool launch, CancellationToken token)
    {
        if (!_initialized || _instanceId != null) throw new ProtocolException("Initialize once before opening a session");
        Authenticate(args);
        _instanceId = launch ? Guid.NewGuid().ToString("N") : Required(args, "instanceId");
        try
        {
            await AuthorizeAsync(DebugOperation.Inspect, token);
            if (launch)
            {
                await AuthorizeAsync(DebugOperation.Launch, token);
                if (args.Value<bool?>("stopOnEntry") == true) await AuthorizeAsync(DebugOperation.PauseAndStep, token);
                await _operations.LaunchAsync(_instanceId, Required(args, "workflow"), args.Value<int?>("version") ?? 1,
                    System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>((args["input"] ?? new JObject()).ToString()),
                    args.Value<bool?>("stopOnEntry") == true, _endpoint.Actor, token);
            }
            await InspectAsync(token);
            await AuthorizeAsync(DebugOperation.Inspect, token);
            _steps = await _operations.GetStepTableAsync(_instanceId, token);
            foreach (var group in _inspection!.Breakpoints.GroupBy(b => b.Source?.Path ?? "functions", StringComparer.OrdinalIgnoreCase))
                _breakpointGroups[group.Key] = group.ToArray();
            _continueOnDisconnect = args.Value<bool?>("continueOnDisconnect") == true;
            if (args["sourceRoots"] is JObject roots)
            {
                if (roots.Count > 32) throw new ProtocolException("Too many source roots");
                foreach (var pair in roots.Properties())
                {
                    var value = pair.Value.Value<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(pair.Name) || string.IsNullOrWhiteSpace(value)) throw new ProtocolException("Invalid source root");
                    _roots.Add(Normalize(pair.Name).TrimEnd('/'), Normalize(value).TrimEnd('/'));
                }
            }
            if (args.Value<bool?>("replay") == true) await EnterReplayAsync(token);
            _subscription = _operations.WatchChanges(_instanceId, Wake);
        }
        catch { _instanceId = null; _authenticated = false; throw; }
        Emit<InitializedEvent>(new { });
    }

    private async Task EnterReplayAsync(CancellationToken token)
    {
        var inspection = await InspectAsync(token);
        if (inspection.Summary.Status != WorkflowStatus.Paused && !IsTerminal(inspection.Summary.Status))
            throw new ProtocolException("Replay requires a paused or finished instance");
        await AuthorizeAsync(DebugOperation.Inspect, token);
        _replay = await _operations.OpenReplayAsync(_instanceId!, 10000, token);
        while (_replay.StepForward()) { }
        _replay.SetBreakpoints(_breakpointGroups.Values.SelectMany(b => b).Select(b => b.StepId));
        ClearHandles();
    }

    private static bool IsTerminal(WorkflowStatus status) => status is WorkflowStatus.Completed or WorkflowStatus.Failed or
        WorkflowStatus.Cancelled or WorkflowStatus.Terminated or WorkflowStatus.TimedOut;

    private async Task RefreshAsync(CancellationToken token)
    {
        var inspection = await InspectAsync(token);
        await AuthorizeAsync(DebugOperation.Inspect, token);
        var page = await _operations.GetTraceAsync(_instanceId!, _traceSequence, 1000, token);
        foreach (var entry in page)
        {
            _traceSequence = entry.Sequence;
            if (entry.Entry.Payload is ThreadPositionTrace position) ObserveThread(position.ContinuationId, position.Thread != null);
            Emit<OutputEvent>(new { category = "console", output = $"{entry.Sequence}: {entry.Entry.EntryType}\n" });
        }
        if (page.Count < 1000)
        {
            var live = inspection.Threads.Select(t => t.ContinuationId).ToHashSet(StringComparer.Ordinal);
            foreach (var id in live) ObserveThread(id, true);
            foreach (var id in _observedThreads.Except(live).ToArray()) ObserveThread(id, false);
            if (inspection.Summary.Status == WorkflowStatus.Paused && _reportedStop != inspection.DebugState.PauseGeneration)
            {
                ClearHandles();
                _reportedStop = inspection.DebugState.PauseGeneration;
                var stop = inspection.DebugState.Stop;
                var reason = stop?.Reason switch { "Step" => "step", "Blocked" => "blocked", "Pause" => "pause", _ =>
                    string.IsNullOrEmpty(stop?.BreakpointId) ? "pause" : "breakpoint" };
                Emit<StoppedEvent>(new { reason, allThreadsStopped = true,
                    threadId = stop == null ? (int?)null : ThreadId(stop.BranchId),
                    hitBreakpointIds = string.IsNullOrEmpty(stop?.BreakpointId) ? Array.Empty<int>() : new[] { BreakpointId(stop.BreakpointId) } });
            }
            if (IsTerminal(inspection.Summary.Status) && !_terminated)
            {
                _terminated = true;
                ClearHandles();
                Emit<TerminatedEvent>(new { });
            }
        }
        else Wake();
    }

    private void ObserveThread(string id, bool live)
    {
        if (live ? _observedThreads.Add(id) : _observedThreads.Remove(id))
            Emit<ThreadEvent>(new { reason = live ? "started" : "exited", threadId = ThreadId(id) });
    }

    private int ThreadId(string id) => Id(_threadIds, id);
    private int BreakpointId(string id) => Id(_breakpointIds, id);
    private int Id(Dictionary<string, int> map, string id)
    {
        if (!map.TryGetValue(id, out var value)) map[id] = value = checked(++_nextId);
        return value;
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken token)
    {
        await AuthorizeAsync(DebugOperation.PauseAndStep, token);
        if (_replay != null) throw new ProtocolException("This operation is unavailable in replay");
        ClearHandles();
        var task = operation(_lifetime.Token);
        if (task.IsCompleted) await task;
        else lock (_commands) { _commands.RemoveAll(t => t.IsCompleted); _commands.Add(ObserveCommandAsync(task)); }
        Wake();
    }

    private async Task ObserveCommandAsync(Task operation)
    {
        try { await operation; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { if (!_lifetime.IsCancellationRequested) Emit<OutputEvent>(new { category = "stderr", output = "Workflow debug command was refused.\n" }); }
        finally { Wake(); }
    }

    private JObject SafeBody(object body)
    {
        var json = JObject.FromObject(body);
        if (_endpoint.SessionToken is { } secret) Scrub(json);
        return json;
        void Scrub(JToken node)
        {
            foreach (var child in node.Children().ToArray())
            {
                if (child is JProperty property)
                {
                    Scrub(property.Value);
                    if (property.Name.Contains(secret, StringComparison.Ordinal)) property.Replace(new JProperty(property.Name.Replace(secret, "[session capability]", StringComparison.Ordinal), property.Value));
                }
                else if (child.Type == JTokenType.String) child.Replace(child.Value<string>()!.Replace(secret, "[session capability]", StringComparison.Ordinal));
                else Scrub(child);
            }
            if (node is JValue { Type: JTokenType.String } value) value.Value = value.Value<string>()!.Replace(secret, "[session capability]", StringComparison.Ordinal);
        }
    }
    private void Emit<T>(object body) where T : DebugEvent => Protocol.SendEvent(SafeBody(body).ToObject<T>()!);
    private static string Required(JObject args, string name) => args.Value<string>(name) is { Length: > 0 } value
        ? value : throw new ProtocolException("Missing required argument");
    private static string Normalize(string path) => path.Replace('\\', '/');

    public void Dispose()
    {
        _subscription?.Dispose();
        _lifetime.Cancel();
        _closed.TrySetResult();
    }
}

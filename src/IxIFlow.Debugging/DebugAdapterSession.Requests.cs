using IxIFlow.Core;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Newtonsoft.Json.Linq;

namespace IxIFlow.Debugging;

public sealed partial class DebugAdapterSession
{
    protected override void HandleInitializeRequestAsync(IRequestResponder<InitializeArguments, InitializeResponse> r) => Dispatch(r);
    protected override void HandleAttachRequestAsync(IRequestResponder<AttachArguments> r) => Dispatch(r);
    protected override void HandleLaunchRequestAsync(IRequestResponder<LaunchArguments> r) => Dispatch(r);
    protected override void HandleConfigurationDoneRequestAsync(IRequestResponder<ConfigurationDoneArguments> r) => Dispatch(r);
    protected override void HandleDisconnectRequestAsync(IRequestResponder<DisconnectArguments> r) => Dispatch(r);
    protected override void HandleThreadsRequestAsync(IRequestResponder<ThreadsArguments, ThreadsResponse> r) => Dispatch(r);
    protected override void HandleStackTraceRequestAsync(IRequestResponder<StackTraceArguments, StackTraceResponse> r) => Dispatch(r);
    protected override void HandleScopesRequestAsync(IRequestResponder<ScopesArguments, ScopesResponse> r) => Dispatch(r);
    protected override void HandleVariablesRequestAsync(IRequestResponder<VariablesArguments, VariablesResponse> r) => Dispatch(r);
    protected override void HandleEvaluateRequestAsync(IRequestResponder<EvaluateArguments, EvaluateResponse> r) => Dispatch(r);
    protected override void HandleSetVariableRequestAsync(IRequestResponder<SetVariableArguments, SetVariableResponse> r) => Dispatch(r);
    protected override void HandleSetExpressionRequestAsync(IRequestResponder<SetExpressionArguments, SetExpressionResponse> r) => Dispatch(r);
    protected override void HandleSetBreakpointsRequestAsync(IRequestResponder<SetBreakpointsArguments, SetBreakpointsResponse> r) => Dispatch(r);
    protected override void HandleSetFunctionBreakpointsRequestAsync(IRequestResponder<SetFunctionBreakpointsArguments, SetFunctionBreakpointsResponse> r) => Dispatch(r);
    protected override void HandleBreakpointLocationsRequestAsync(IRequestResponder<BreakpointLocationsArguments, BreakpointLocationsResponse> r) => Dispatch(r);
    protected override void HandleLoadedSourcesRequestAsync(IRequestResponder<LoadedSourcesArguments, LoadedSourcesResponse> r) => Dispatch(r);
    protected override void HandleSourceRequestAsync(IRequestResponder<SourceArguments, SourceResponse> r) => Dispatch(r);
    protected override void HandleExceptionInfoRequestAsync(IRequestResponder<ExceptionInfoArguments, ExceptionInfoResponse> r) => Dispatch(r);
    protected override void HandlePauseRequestAsync(IRequestResponder<PauseArguments> r) => Dispatch(r);
    protected override void HandleContinueRequestAsync(IRequestResponder<ContinueArguments, ContinueResponse> r) => Dispatch(r);
    protected override void HandleNextRequestAsync(IRequestResponder<NextArguments> r) => Dispatch(r);
    protected override void HandleStepInRequestAsync(IRequestResponder<StepInArguments> r) => Dispatch(r);
    protected override void HandleStepOutRequestAsync(IRequestResponder<StepOutArguments> r) => Dispatch(r);
    protected override void HandleStepBackRequestAsync(IRequestResponder<StepBackArguments> r) => Dispatch(r);
    protected override void HandleReverseContinueRequestAsync(IRequestResponder<ReverseContinueArguments> r) => Dispatch(r);

    private void Dispatch<T>(IRequestResponder<T> responder) where T : class, new() =>
        Dispatch(responder, _ => responder.SetResponse(null!));
    private void Dispatch<TArgs, TResponse>(IRequestResponder<TArgs, TResponse> responder)
        where TArgs : class, new() where TResponse : ResponseBody =>
        Dispatch(responder, body => responder.SetResponse(body.ToObject<TResponse>()!));

    private async void Dispatch(IRequestResponder responder, Action<JObject> respond)
    {
        var acquired = false;
        try
        {
            await _gate.WaitAsync(_lifetime.Token); acquired = true;
            var args = responder.Arguments == null ? new JObject() : JObject.FromObject(responder.Arguments);
            if (responder.Arguments is AttachArguments attach)
                args.Merge(JObject.FromObject(attach.ConfigurationProperties));
            if (responder.Arguments is LaunchArguments launch)
                args.Merge(JObject.FromObject(launch.ConfigurationProperties));
            var body = await RequestAsync(responder.Command, args, _lifetime.Token);
            respond(body == null ? new JObject() : SafeBody(body));
            if (responder.Command == "disconnect") _closed.TrySetResult();
            else Wake();
        }
        catch (ProtocolException error) { RespondError(responder, error); }
        catch (Exception) { RespondError(responder, new ProtocolException("Workflow debug request was refused")); }
        finally { if (acquired) _gate.Release(); }
    }

    private static void RespondError(IRequestResponder responder, ProtocolException error)
    {
        try { responder.SetError(error); }
        catch (Exception) { /* A queued request may finish after its peer closed the connection. */ }
    }

    private async Task<object?> RequestAsync(string command, JObject args, CancellationToken token)
    {
        if (command == "initialize")
        {
            if (_initialized) throw new ProtocolException("Session is already initialized");
            _initialized = true;
            _lineBase = args.Value<bool?>("linesStartAt1") == false ? 0 : 1;
            _columnBase = args.Value<bool?>("columnsStartAt1") == false ? 0 : 1;
            return new { supportsConfigurationDoneRequest = true, supportsConditionalBreakpoints = true,
                supportsHitConditionalBreakpoints = true, supportsFunctionBreakpoints = true,
                supportsSetVariable = true, supportsSetExpression = true, supportsBreakpointLocationsRequest = true,
                supportsLoadedSourcesRequest = true, supportsExceptionInfoRequest = true,
                supportsSingleThreadExecutionRequests = true, supportsStepBack = true,
                supportTerminateDebuggee = false, supportSuspendDebuggee = true };
        }
        if (command is "attach" or "launch") { await AttachAsync(args, command == "launch", token); return null; }
        await AuthorizeAsync(DebugOperation.Inspect, token);
        switch (command)
        {
            case "configurationDone":
                _configured = true;
                if (_replay != null) ReplayStopped();
                return null;
            case "threads":
                await InspectAsync(token);
                return new { threads = CurrentThreads().Select(t => new { id = ThreadId(t.ContinuationId), name = ThreadName(t) }) };
            case "stackTrace": return await StackTraceAsync(args, token);
            case "scopes": return await ScopesAsync(args, token);
            case "variables": return await VariablesAsync(args, token);
            case "evaluate": return await EvaluateAsync(args, false, token);
            case "setVariable": return await SetVariableAsync(args, token);
            case "setExpression": return await EvaluateAsync(args, true, token);
            case "setBreakpoints": return await SetBreakpointsAsync(args, false, token);
            case "setFunctionBreakpoints": return await SetBreakpointsAsync(args, true, token);
            case "breakpointLocations":
                var path = ToBuildPath(Required((JObject)args["source"]!, "path"));
                var first = (args.Value<int?>("line") ?? _lineBase) + 1 - _lineBase;
                var last = (args.Value<int?>("endLine") ?? int.MaxValue - 1) + 1 - _lineBase;
                return new { breakpoints = _steps.Select(s => Location(s.SourceLocation)).OfType<BreakpointSource>()
                    .Where(s => SamePath(s.Path, path) && s.Line >= first && s.Line <= last)
                    .Distinct().Select(s => new { line = s.Line - 1 + _lineBase }) };
            case "loadedSources": return new { sources = SourcePaths().Select(Source) };
            case "source":
                var requestedPath = args["source"] is JObject sourceArgs && sourceArgs.Value<string>("path") is { } clientPath
                    ? ToBuildPath(clientPath) : null;
                var sourcePath = args.Value<int?>("sourceReference") is > 0 and var sourceId
                    ? SourcePaths().ElementAtOrDefault(sourceId - 1) : SourcePaths().FirstOrDefault(p => requestedPath != null && SamePath(p, requestedPath));
                if (sourcePath == null || !SourcePaths().Contains(sourcePath)) throw new ProtocolException("Unknown source");
                await AuthorizeAsync(DebugOperation.Inspect, token);
                var content = await _operations.GetSourceAsync(_instanceId!, sourcePath, token)
                    ?? throw new ProtocolException("Source is unavailable in this application's published bundle");
                return new { content, mimeType = sourcePath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ? "application/yaml" : "text/plain" };
            case "exceptionInfo":
                if (_replay != null) throw new ProtocolException("Historical failure values are unavailable");
                var state = await InspectAsync(token);
                var thread = SelectedThread(args);
                var failure = state.PendingAttempts.FirstOrDefault(p => p.ContinuationId == thread.ContinuationId && p.Reason != null);
                if (failure == null) throw new ProtocolException("The selected thread is not waiting for failure resolution");
                return new { exceptionId = failure.Kind ?? "WorkflowFailure", description = failure.Reason, breakMode = "always" };
            case "pause":
                await ExecuteAsync(ct => _operations.PauseAsync(_instanceId!, _endpoint.Actor, ct, _incarnation), token); return null;
            case "continue":
                if (_replay != null) { _replay.Continue(); ClearHandles(); ReplayStopped(true); return new { allThreadsContinued = true }; }
                if (args.Value<bool?>("singleThread") == true)
                {
                    var selected = SelectedThread(args).ContinuationId;
                    await ExecuteAsync(ct => _operations.StepAsync(_instanceId!, selected, DebugStepKind.Run, _endpoint.Actor, ct, _incarnation), token);
                }
                else
                {
                    await AuthorizeAsync(DebugOperation.PauseAndStep, token);
                    await _operations.RequestContinueAsync(_instanceId!, _endpoint.Actor, token, _incarnation);
                    ClearHandles();
                }
                var allThreadsContinued = args.Value<bool?>("singleThread") != true;
                Emit<ContinuedEvent>(new { threadId = args.Value<int?>("threadId") ?? 0, allThreadsContinued });
                return new { allThreadsContinued };
            case "next": case "stepIn": case "stepOut":
                if (_replay != null) { _replay.StepForward(); ClearHandles(); ReplayStopped(); return null; }
                await InspectAsync(token);
                var branch = SelectedThread(args).ContinuationId;
                var kind = command == "next" ? DebugStepKind.Over : command == "stepIn" ? DebugStepKind.Into : DebugStepKind.Out;
                await ExecuteAsync(ct => _operations.StepAsync(_instanceId!, branch, kind, _endpoint.Actor, ct, _incarnation), token);
                Emit<ContinuedEvent>(new { threadId = ThreadId(branch), allThreadsContinued = false });
                return null;
            case "stepBack": case "reverseContinue":
                if (_replay == null) await EnterReplayAsync(token);
                if (command == "stepBack") _replay!.StepBackward(); else _replay!.Continue(backwards: true);
                ClearHandles(); ReplayStopped(command == "reverseContinue"); return null;
            case "disconnect":
                if (_continueOnDisconnect && _replay == null)
                {
                    await AuthorizeAsync(DebugOperation.PauseAndStep, token);
                    var currentState = await InspectAsync(token);
                    if (currentState.Summary.Status == WorkflowStatus.Paused)
                        await _operations.RequestContinueAsync(_instanceId!, _endpoint.Actor, token, _incarnation);
                }
                _disconnected = true; _subscription?.Dispose(); ClearHandles(); return null;
            default: throw new ProtocolException("Unsupported debug request");
        }
    }

    private IReadOnlyList<InspectionThread> CurrentThreads() => _replay?.Current.Threads ?? _inspection?.Threads ?? [];
    private InspectionThread SelectedThread(JObject args) => CurrentThreads().SingleOrDefault(t =>
        _threadIds.TryGetValue(t.ContinuationId, out var id) && id == args.Value<int?>("threadId"))
        ?? throw new ProtocolException("Unknown thread");
    private static string ThreadName(InspectionThread t) => t.Name + " — " + string.Join(" / ", t.Frames.Select(f => f.Name));
    private void ReplayStopped(bool considerBreakpoints = false)
    {
        var position = _replay!.Current;
        var hitIds = considerBreakpoints ? _breakpointGroups.Values.SelectMany(b => b)
            .Where(b => b.Enabled && b.StepId == _replay.CurrentStepId).Select(b => BreakpointId(b.Id)).Distinct().ToArray() : [];
        foreach (var thread in position.Threads) ObserveThread(thread.ContinuationId, true);
        foreach (var id in _observedThreads.Except(position.Threads.Select(t => t.ContinuationId)).ToArray()) ObserveThread(id, false);
        Emit<StoppedEvent>(new { reason = hitIds.Length == 0 ? "step" : "breakpoint", allThreadsStopped = true,
            threadId = position.Threads.FirstOrDefault() is { } first ? (int?)ThreadId(first.ContinuationId) : null,
            description = position.IsComplete ? "Replay" : "Replay history is incomplete", hitBreakpointIds = hitIds });
    }
}

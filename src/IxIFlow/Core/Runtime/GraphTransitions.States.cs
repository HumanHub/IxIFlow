namespace IxIFlow.Core.Runtime;

internal static partial class GraphTransitions
{
    public static void PreparePick(WorkflowScopeCatalog scopes, ContinuationState continuation)
    {
        if (continuation.Stack.LastOrDefault() is not { Kind: FrameKind.State } state ||
            scopes.Steps(state.ScopeId).ElementAtOrDefault(state.NextStepIndex)?.StepType != WorkflowStepType.Arm)
            return;
        Restore(continuation, state);
        if (state.ArmOccurrence == 0) state.ArmOccurrence = 1;
    }

    private static void Restore(ContinuationState continuation, ScopePosition state)
    {
        continuation.Previous = state.EntryPrevious;
        continuation.RuntimePrevious = state.RuntimeEntryPrevious;
    }

    public static void TraceEntry(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep step, TraceEntryType? kind = null)
    {
        var owner = continuation.Stack.Last(frame => frame.Graph != null);
        var graph = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        var state = owner.Graph!;
        var frame = continuation.Stack[^1];
        checkpoint.Trace.Graph(kind ?? (frame.Kind == FrameKind.Node ? TraceEntryType.NodeEntered :
                frame.Kind == FrameKind.StateExit ? TraceEntryType.ExitEntered : TraceEntryType.StateEntered),
            step, continuation, scopes.Id(graph), state.Node, state.Visits, state.Via);
    }

    public static bool Accepts(WorkflowScopeCatalog scopes, ContinuationState continuation, string triggerId)
    {
        var frame = continuation.Stack.LastOrDefault();
        return frame is { Kind: FrameKind.State } &&
            scopes.Steps(frame.ScopeId).ElementAtOrDefault(frame.NextStepIndex)?.StepType == WorkflowStepType.Arm &&
            triggerId.StartsWith(frame.ScopeId + "/on/", StringComparison.Ordinal) &&
            scopes.Step(triggerId).StepType == WorkflowStepType.Trigger;
    }

    public static void TriggerFired(ExecutionCheckpoint checkpoint, ContinuationState continuation)
    {
        var state = continuation.Stack[^1];
        checkpoint.RemoveWaits(wait => wait.ContinuationId == continuation.Id && !wait.IsReply &&
            wait.StepId.StartsWith(state.ScopeId + "/on/", StringComparison.Ordinal), WaitCloseReason.Cancelled);
        state.ArmOccurrence++;
        checkpoint.Touch(continuation);
    }

    public static void CloseState(ExecutionCheckpoint checkpoint, ContinuationState continuation, ScopePosition state)
    {
        checkpoint.RemoveWaits(wait => wait.ScopeActivationId == state.ActivationId ||
            wait.ContinuationId == continuation.Id && wait.StepId.StartsWith(state.ScopeId + "/on/", StringComparison.Ordinal),
            WaitCloseReason.Cancelled);
        checkpoint.BoundReplies.RemoveAll(reply =>
        {
            if (reply.ScopeActivationId != state.ActivationId) return false;
            checkpoint.TouchOwner(reply.ContinuationId);
            return true;
        });
    }

    public static void Arm(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, ContinuationState continuation,
        WorkflowStep arm, IServiceProvider services, DateTime now, DateTime storeNow)
    {
        PreparePick(scopes, continuation);
        checkpoint.Trace.Record(TraceEntryType.StepEntered, arm, continuation);
        var state = continuation.Stack[^1];
        var owner = continuation.Stack[^2];
        var graph = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        var definition = graph.GraphStates.Single(s => s.Key == state.Node);
        if (continuation.AcceptedWait is { } accepted)
        {
            continuation.AcceptedWait = null;
            var trigger = scopes.Step(accepted.StepId);
            var value = accepted.Event?.Read(services);
            if (trigger.TriggerKind is TriggerKind.Event or TriggerKind.Reply)
            {
                if (value == null)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "An event trigger requires its accepted payload");
                WorkflowValueBinding.ApplyEventOutputs(trigger, value, checkpoint.DataOf(continuation),
                    ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
                continuation.SetPrevious(value);
            }
            var transition = definition.Transitions.Single(t => t.Steps[0] == trigger);
            if (!Select(scopes, checkpoint, continuation, transition, services, accepted.Event))
            {
                Restore(continuation, state);
                state.ArmOccurrence++;
                return;
            }
        }
        else if (owner.Graph!.Pending == null)
        {
            foreach (var transition in definition.Transitions)
            {
                var trigger = transition.Steps[0];
                if (trigger.TriggerKind == TriggerKind.Immediate &&
                    Select(scopes, checkpoint, continuation, transition, services, null)) break;
                if (trigger.TriggerKind == TriggerKind.Reply && checkpoint.BoundReplies.FirstOrDefault(reply =>
                        reply.StepId == scopes.Id(trigger) && reply.ScopeActivationId == state.ActivationId) is { } answer)
                {
                    checkpoint.BoundReplies.Remove(answer);
                    checkpoint.TouchOwner(answer.ContinuationId);
                    continuation.AcceptedWait = new AcceptedWaitState
                    {
                        StepId = answer.StepId, Event = answer.Event, EventId = answer.EventId, WaitId = answer.WaitId
                    };
                    TriggerFired(checkpoint, continuation);
                    return;
                }
            }
            if (owner.Graph.Pending == null)
            {
                TryScopeTransitions.EnsureCanWait(checkpoint, continuation);
                foreach (var transition in definition.Transitions)
                {
                    var trigger = transition.Steps[0];
                    if (trigger.TriggerKind == TriggerKind.Immediate) continue;
                    if (trigger.TriggerKind == TriggerKind.Reply)
                    {
                        var reply = checkpoint.Waits.Single(wait => wait.StepId == scopes.Id(trigger) &&
                            wait.ScopeActivationId == state.ActivationId);
                        if (reply.DefaultPayloadJson == null)
                        {
                            reply.DefaultPayloadJson = WaitSuggestions.Create(trigger, checkpoint.DataOf(continuation),
                                continuation.GetPrevious(services), ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
                            checkpoint.TouchOwner(reply.ContinuationId);
                        }
                        continue;
                    }
                    var suggestion = trigger.TriggerKind == TriggerKind.Event
                        ? WaitSuggestions.Create(trigger, checkpoint.DataOf(continuation), continuation.GetPrevious(services),
                            ForEachTransitions.ItemsOf(scopes, checkpoint, continuation)) : null;
                    TimeSpan? duration = null;
                    if (trigger.TriggerKind == TriggerKind.Timer)
                    {
                        duration = trigger.DelayDuration;
                        if (duration == null)
                        {
                            var context = WorkflowValueBinding.EvaluationContext(trigger, checkpoint.DataOf(continuation),
                                continuation.GetPrevious(services), ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
                            try
                            {
                                duration = trigger.After!(context);
                            }
                            catch (Exception error)
                            {
                                if (error is WorkflowEngineException engineError) engineError.At(null, scopes.Id(trigger));
                                WorkflowEngineException.FromWorkflowCode(error);
                                throw;
                            }
                        }
                        if (duration <= TimeSpan.Zero)
                            throw new InvalidOperationException("After must evaluate to a positive duration");
                    }
                    checkpoint.AddWait(new WaitState
                    {
                        RegisteredAtUtc = now, ContinuationId = continuation.Id, StepId = scopes.Id(trigger),
                        Key = trigger.TriggerKind == TriggerKind.Timer ? "timer" : (string)trigger.StepMetadata["WaitKey"],
                        EventType = trigger.ResumeEventType == null ? "" : WorkflowTypeIdentity.SavedName(trigger.ResumeEventType),
                        DefaultPayloadJson = suggestion, Timeout = duration, DeadlineUtc = storeNow + duration
                    });
                }
                continuation.Status = ContinuationStatus.Waiting;
                checkpoint.Trace.Graph(TraceEntryType.StateArmed, arm, continuation, scopes.Id(graph), state.Node!,
                    owner.Graph.Visits, count: checkpoint.Waits.Count(wait => wait.ContinuationId == continuation.Id));
                return;
            }
        }
        Restore(continuation, state);
        if (definition.ExitSteps.Count > 0)
            PushScope(scopes, checkpoint, continuation, graph, scopes.StateExitScope(graph, state.Node!), FrameKind.StateExit);
        else Transition(scopes, checkpoint, continuation, state);
    }

    private static bool Select(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, ContinuationState continuation,
        StateMachineTransition transition, IServiceProvider services, SerializedValue? value)
    {
        var trigger = transition.Steps[0];
        foreach (var alternative in transition.Cases)
        {
            var edge = alternative[0];
            var context = WorkflowValueBinding.EvaluationContext(edge, checkpoint.DataOf(continuation),
                continuation.GetPrevious(services), ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
            try
            {
                if (edge.CompiledCondition != null && !edge.CompiledCondition(context)) continue;
            }
            catch (Exception error)
            {
                if (error is WorkflowEngineException engineError) engineError.At(null, scopes.Id(trigger));
                WorkflowEngineException.FromWorkflowCode(error);
                throw;
            }
            var cause = trigger.TriggerKind switch
            {
                TriggerKind.Event => TransitionCause.Event, TriggerKind.Reply => TransitionCause.Reply,
                TriggerKind.Timer => TransitionCause.Timer, _ => TransitionCause.Immediate
            };
            Choose(scopes, checkpoint, continuation, edge, edge.Target, cause, trigger, value);
            return true;
        }
        return false;
    }

    private static void Transition(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, ScopePosition state)
    {
        var owner = continuation.Stack[^2];
        var graph = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        var pending = owner.Graph!.Pending!;
        if (pending.Trigger == null)
        {
            state.NextStepIndex = scopes.Steps(state.ScopeId).Count;
            return;
        }
        if (pending.Event != null)
        {
            continuation.Previous = pending.Event;
            continuation.RuntimePrevious = null;
        }
        PushScope(scopes, checkpoint, continuation, graph,
            pending.Trigger[..pending.Trigger.LastIndexOf('/')], FrameKind.Transition);
    }

    public static void Trigger(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep trigger)
    {
        checkpoint.Trace.Record(TraceEntryType.StepEntered, trigger, continuation);
        var owner = continuation.Stack.Last(frame => frame.Graph != null);
        var graph = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
        var edge = owner.Graph!.Pending!.Via;
        PushScope(scopes, checkpoint, continuation, graph, edge[..edge.LastIndexOf('/')], FrameKind.Case);
    }

    private static void PushScope(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, ContinuationState continuation,
        WorkflowStep graph, string scope, FrameKind kind)
    {
        var frame = new ScopePosition
        {
            ScopeId = scope, Kind = kind, EntryPrevious = continuation.Previous,
            RuntimeEntryPrevious = continuation.RuntimePrevious, RestorePreviousOnExit = true
        };
        continuation.Stack.Add(frame);
        SteppingPolicy.Entered(checkpoint, continuation, scopes.Id(graph), frame);
    }
}

using IxIFlow.Core.Runtime;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
/// Pairs requests with their answer waits in the same body, or with a reply trigger of the
/// state whose entry directly owns the request. Both keep the request's scope activation.
/// </summary>
internal static class RequestPairing
{
    /// <summary>The answer type an activity declares with <see cref="IRequestActivity{TReply}"/>.</summary>
    public static Type ReplyTypeOf(Type activityType)
    {
        var replies = activityType.GetInterfaces()
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IRequestActivity<>))
            .Select(type => type.GetGenericArguments()[0])
            .ToArray();
        return replies.Length == 1
            ? replies[0]
            : throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"'{activityType.Name}' cannot run as a request: it must implement IRequestActivity<TReply> once");
    }

    /// <summary>The steps of a definition being built, with its requests paired.</summary>
    public static List<WorkflowStep> Paired(IReadOnlyList<WorkflowStep> steps)
    {
        Pair(steps);
        return [..steps];
    }

    /// <summary>Pairs the requests in a definition with their waits, or fails.</summary>
    public static void Pair(IReadOnlyList<WorkflowStep> root)
    {
        var bodies = new List<IReadOnlyList<WorkflowStep>>();
        Collect(root, bodies);
        var stateEntries = ParallelRules.Flatten(root, crossInline: false).SelectMany(step => step.GraphStates)
            .ToDictionary(state => (IReadOnlyList<WorkflowStep>)state.Steps, state => state);
        var pairedTriggers = new HashSet<WorkflowStep>();
        var requestNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var body in bodies)
        {
            for (var index = 0; index < body.Count; index++)
            {
                var request = body[index];
                if (request.RequestName is not { } name)
                    continue;
                if (body.Take(index).Any(earlier => earlier.RequestName == name))
                    throw WorkflowBuildError.Invalid(request, $"Two requests named '{name}' are in the same body");
                requestNames.Add(name);
                var waits = body.Skip(index + 1).Where(step => WaitKey(step) == name)
                    .Concat(stateEntries.TryGetValue(body, out var state)
                        ? state.Transitions.Select(t => t.Steps[0]).Where(t =>
                            t.TriggerKind == TriggerKind.Reply && t.ReplyToRequest == name)
                        : []).ToArray();
                if (waits.Length != 1)
                    throw WorkflowBuildError.Invalid(request, $"Request '{name}' needs exactly one WaitFor<{request.ReplyType?.Name}>(\"{name}\") " +
                        "after it in the same body, or one reply trigger of its state");
                if (waits[0].ResumeEventType != request.ReplyType)
                    throw WorkflowBuildError.Invalid(waits[0], $"WaitFor(\"{name}\") waits for {waits[0].ResumeEventType?.Name}, but request " +
                        $"'{name}' is answered with {request.ReplyType?.Name}");
                waits[0].ReplyToRequest = name;
                request.ReplyWait = waits[0];
                pairedTriggers.Add(waits[0]);
            }
        }
        foreach (var trigger in bodies.SelectMany(body => body).Where(step =>
                     step.StepType == WorkflowStepType.Trigger && step.TriggerKind == TriggerKind.Reply))
            if (!pairedTriggers.Contains(trigger))
                throw WorkflowBuildError.Invalid(trigger, "A reply trigger needs exactly one request directly in its state's entry body");
        // A wait with a request's name anywhere else would take the answer to another occurrence.
        foreach (var step in bodies.SelectMany(body => body))
            if (WaitKey(step) is { } key && requestNames.Contains(key) && step.ReplyToRequest == null)
                throw WorkflowBuildError.Invalid(step, $"WaitFor(\"{key}\") is not in the same body as request '{key}', after it");
    }

    private static string? WaitKey(WorkflowStep step) =>
        step.StepType == WorkflowStepType.SuspendResume && step.StepMetadata.TryGetValue("WaitKey", out var key)
            ? key as string
            : null;

    private static void Collect(IReadOnlyList<WorkflowStep> body, List<IReadOnlyList<WorkflowStep>> bodies)
    {
        bodies.Add(body);
        foreach (var step in body)
        {
            foreach (var scope in step.ChildScopes().Where(scope => scope.Kind != "inline").OrderBy(scope => scope.Kind switch
                     {
                         "foreach" => 1, "branch" or "case" or "default" => 2, _ => 0
                     }))
                Collect(scope.Steps, bodies);
        }
    }

}

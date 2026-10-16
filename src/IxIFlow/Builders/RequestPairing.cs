using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
/// Pairs each request step with the one <c>WaitFor</c> that takes its answer (D-33): it has the
/// request's name and answer type and follows the request in the same body, so both belong to the same
/// loop iteration or branch occurrence. Anything else fails when the workflow is built.
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
        var requestNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var body in bodies)
        {
            for (var index = 0; index < body.Count; index++)
            {
                var request = body[index];
                if (request.RequestName is not { } name)
                    continue;
                if (body.Take(index).Any(earlier => earlier.RequestName == name))
                    throw Invalid($"Two requests named '{name}' are in the same body");
                requestNames.Add(name);
                var waits = body.Skip(index + 1).Where(step => WaitKey(step) == name).ToArray();
                if (waits.Length != 1)
                    throw Invalid($"Request '{name}' needs exactly one WaitFor<{request.ReplyType?.Name}>(\"{name}\") " +
                        "after it in the same body");
                if (waits[0].ResumeEventType != request.ReplyType)
                    throw Invalid($"WaitFor(\"{name}\") waits for {waits[0].ResumeEventType?.Name}, but request " +
                        $"'{name}' is answered with {request.ReplyType?.Name}");
                waits[0].ReplyToRequest = name;
            }
        }
        // A wait with a request's name anywhere else would take the answer to another occurrence.
        foreach (var step in bodies.SelectMany(body => body))
            if (WaitKey(step) is { } key && requestNames.Contains(key) && step.ReplyToRequest == null)
                throw Invalid($"WaitFor(\"{key}\") is not in the same body as request '{key}', after it");
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
            foreach (var nested in new[]
                     {
                         step.SequenceSteps, step.ThenSteps, step.ElseSteps, step.LoopBodySteps, step.FinallySteps,
                         step.TimeoutSteps, step.CatchBlocks
                     })
                Collect(nested, bodies);
            foreach (var branch in step.ParallelBranches)
                Collect(branch, bodies);
            foreach (var outcome in step.OutcomeBranches)
                Collect(outcome.Steps, bodies);
        }
    }

    private static WorkflowEngineException Invalid(string message) =>
        new(WorkflowErrorKind.InvalidDefinition, message);
}

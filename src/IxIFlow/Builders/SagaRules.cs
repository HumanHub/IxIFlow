using IxIFlow.Core.Runtime;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
/// Checks each saga's error handlers against its steps when the workflow is built: the step a
/// <c>CompensateUpTo</c> stops at is in the saga exactly once, and a handler that ends the saga keeps no
/// request whose answer the saga has not read yet. Runs after <see cref="RequestPairing"/>, which pairs each
/// request with its <c>WaitFor</c>.
/// </summary>
internal static class SagaRules
{
    public static List<WorkflowStep> Checked(List<WorkflowStep> steps)
    {
        foreach (var saga in Sagas(steps))
            Check(saga);
        return steps;
    }

    private static void Check(WorkflowStep saga)
    {
        // A saga body has no loops or forks, so the order of its steps, outcome branches in place, is the
        // order they run in; of two branches only one runs.
        var order = new List<WorkflowStep>();
        Flatten(saga.SequenceSteps, order);
        // A compensation that reads an answer belongs to a request answered with that type.
        foreach (var step in order)
            if (step.StepMetadata.TryGetValue("CompensationActivities", out var declared) &&
                declared is IEnumerable<CompensationActivityInfo> compensations)
                foreach (var compensation in compensations.Where(item => item.ReplyType != null))
                    if (step.RequestName == null)
                        throw WorkflowBuildError.Invalid(step, $"The compensation {compensation.ActivityType.Name} of {step.ActivityType?.Name} " +
                            $"reads an answer of type {compensation.ReplyType!.Name}, but {step.ActivityType?.Name} " +
                            "is not a request");
                    else if (compensation.ReplyType != step.ReplyType)
                        throw WorkflowBuildError.Invalid(step, $"The compensation {compensation.ActivityType.Name} of request '{step.RequestName}' " +
                            $"reads an answer of type {compensation.ReplyType!.Name}, but the request is answered " +
                            $"with {step.ReplyType?.Name}");
        foreach (var handler in saga.CatchBlocks)
        {
            var configuration = handler.StepMetadata.GetValueOrDefault("SagaErrorConfig") as SagaErrorConfiguration;
            if (configuration?.ContinuationAction == null &&
                handler.SequenceSteps.FirstOrDefault(step => step.StepMetadata.ContainsKey("WaitKey")) is { } handlerWait)
                throw WorkflowBuildError.Invalid(handlerWait,
                    "A saga error handler with WaitFor must end with ThenContinue, ThenRetry, or ThenTerminate");
            if (!handler.StepMetadata.TryGetValue("SagaErrorConfig", out var value) ||
                value is not SagaErrorConfiguration policy)
                continue;
            int stop;
            string boundary;
            switch (policy.CompensationStrategy)
            {
                case CompensationStrategy.CompensateUpTo:
                    var target = policy.CompensationTargetType!;
                    var matches = order.Select((step, index) => (step, index))
                        .Where(item => item.step.StepType == WorkflowStepType.Activity && item.step.ActivityType == target)
                        .ToArray();
                    if (matches.Length != 1)
                        throw WorkflowBuildError.Invalid(handler, $"{handler.Name} compensates up to {target.Name}, which " +
                            (matches.Length == 0 ? "is not a step of the saga" : "is a step of the saga more than once"));
                    stop = matches[0].index;
                    boundary = $"{target.Name}, where the undo stops";
                    break;
                case CompensationStrategy.None:
                    stop = -1;
                    boundary = "a step that may fail, and nothing is undone";
                    break;
                default:
                    continue;
            }
            // A retry runs on past the boundary and reads the answer; anything else ends the saga.
            if (policy.ContinuationAction == SagaContinuationAction.Retry)
                continue;
            foreach (var (body, request, inBody, wait, waitInBody) in Requests(saga.SequenceSteps))
            {
                var index = order.IndexOf(request);
                var name = request.RequestName!;
                var lost = policy.CompensationStrategy == CompensationStrategy.None
                    ? waitInBody > inBody + 1
                    : index < stop && order.IndexOf(wait) > stop;
                if (lost)
                    throw WorkflowBuildError.Invalid(handler, $"{handler.Name} keeps request '{name}' ({request.ActivityType?.Name}), but its " +
                        $"WaitFor(\"{name}\") comes after {boundary}, so the saga would end without its answer. " +
                        "Compensate the request too, read its answer earlier, or retry the saga");
            }
        }
    }

    // Each request of a saga with the WaitFor paired with it in the same body, and both positions there.
    private static IEnumerable<(List<WorkflowStep> Body, WorkflowStep Request, int InBody, WorkflowStep Wait, int WaitInBody)>
        Requests(List<WorkflowStep> body)
    {
        for (var index = 0; index < body.Count; index++)
        {
            var step = body[index];
            if (step.RequestName is { } name &&
                body.FindIndex(index + 1, candidate => candidate.ReplyToRequest == name) is var wait and >= 0)
                yield return (body, step, index, body[wait], wait);
            foreach (var branch in step.OutcomeBranches)
            foreach (var nested in Requests(branch.Steps))
                yield return nested;
        }
    }

    private static void Flatten(IEnumerable<WorkflowStep> body, List<WorkflowStep> order)
    {
        foreach (var step in body)
        {
            order.Add(step);
            foreach (var branch in step.OutcomeBranches)
                Flatten(branch.Steps, order);
        }
    }

    private static IEnumerable<WorkflowStep> Sagas(IEnumerable<WorkflowStep> body)
    {
        foreach (var step in body)
        {
            if (step.StepType == WorkflowStepType.Saga)
                yield return step;
            var nested = new[]
                {
                    step.SequenceSteps, step.ThenSteps, step.ElseSteps, step.LoopBodySteps, step.FinallySteps,
                    step.TimeoutSteps, step.CatchBlocks, step.ForEachBodySteps
                }
                .Concat(step.ParallelBranches)
                .Concat(step.OutcomeBranches.Select(branch => branch.Steps));
            foreach (var steps in nested)
            foreach (var saga in Sagas(steps))
                yield return saga;
        }
    }

}

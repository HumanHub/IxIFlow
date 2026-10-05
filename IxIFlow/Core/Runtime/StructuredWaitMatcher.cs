namespace IxIFlow.Core.Runtime;

/// <summary>
/// Finds the active wait whose type, key, and predicate accept an event.
/// </summary>
internal static class StructuredWaitMatcher
{
    public static List<WaitState> FindMatches(
        ExecutionCheckpoint checkpoint,
        WorkflowScopeCatalog scopes,
        object workflowData,
        object @event,
        IServiceProvider services,
        string? key = null)
    {
        var eventType = @event.GetType();
        return checkpoint.Waits.Where(wait =>
            (key == null || wait.Key == key) &&
            WorkflowTypeIdentity.Resolve(wait.EventType)?.IsAssignableFrom(eventType) == true &&
            MatchesPredicate(wait)).ToList();

        bool MatchesPredicate(WaitState wait)
        {
            var step = scopes.Step(wait.StepId);
            var continuation = checkpoint.Continuations.Single(item => item.Id == wait.ContinuationId);
            return WorkflowValueBinding.Matches(step, @event, workflowData,
                continuation.GetPrevious(services));
        }
    }
}

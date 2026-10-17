using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// What follows a saga handler's undo (D-36). With none of these, the original error is raised again once the
/// undo has run.
/// </summary>
public interface ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> where TPreviousStepData : class
{
    /// <summary>
    /// Ends the saga after the undo; the workflow goes on with the step after it. A handler that keeps a request
    /// whose answer the saga has not read is refused when the workflow is built.
    /// </summary>
    /// <param name="ignoreCompensationErrors">Whether to ignore errors during compensation</param>
    void ThenContinue(bool ignoreCompensationErrors = false);

    /// <summary>
    /// Ends the saga after the undo by raising <see cref="SagaTerminatedException"/>, which a surrounding
    /// <c>Catch</c> may handle. A handler that keeps a request whose answer the saga has not read is refused
    /// when the workflow is built.
    /// </summary>
    void ThenTerminate();

    /// <summary>
    /// Runs the saga again from the first step the undo reached, or from the step that failed when nothing was
    /// undone; kept steps do not run again, and a kept request keeps its wait and answer. When the saga cannot
    /// retry after all (no attempts left, an undo failed, the handler's steps failed, or the instance is
    /// cancelled) it undoes the kept steps too, then ends (D-36).
    /// </summary>
    /// <param name="maxAttempts">Maximum number of retry attempts</param>
    void ThenRetry(int maxAttempts);

    /// <inheritdoc cref="ThenRetry(int)"/>
    /// <param name="retryPolicy">The retry policy configuration</param>
    void ThenRetry(RetryPolicy retryPolicy);
}

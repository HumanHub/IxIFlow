using System.Reflection;
using System.Runtime.ExceptionServices;

namespace IxIFlow.Core.Runtime;

internal static class FaultCapture
{
    // A control-flow transition can raise a new error or explicitly propagate a saved one.
    // Exception identity alone cannot distinguish those occurrences.
    internal sealed class Boundary : IDisposable
    {
        private readonly ExecutionCheckpoint _checkpoint;
        private readonly Boundary? _outer;
        private Exception? _propagating;

        internal Boundary(ExecutionCheckpoint checkpoint)
        {
            _checkpoint = checkpoint;
            _outer = checkpoint.FailureBoundary;
            checkpoint.FailureBoundary = this;
        }

        internal void Propagating(Exception error) => _propagating = error;
        internal void Clear() => _propagating = null;
        internal bool IsPropagation(Exception error) => ReferenceEquals(_propagating, error);
        public void Dispose() => _checkpoint.FailureBoundary = _outer;
    }

    internal static Exception ProjectionFailure(Exception error, Exception projection)
    {
        while (projection is TargetInvocationException { InnerException: { } inner }) projection = inner;
        if (WorkflowEngineException.IsEngineFailure(projection) || projection is ResourceLimitExceededException)
            ExceptionDispatchInfo.Capture(projection).Throw();
        return new InvalidOperationException($"Fault projection for '{SerializedException.CatchType(error).Name}' failed: " +
            projection.Message, projection);
    }
}

namespace IxIFlow.Core;

internal static class ExecutionContextFlow
{
    internal static AsyncFlowControl? SuppressIfNeeded() =>
        ExecutionContext.IsFlowSuppressed() ? null : ExecutionContext.SuppressFlow();
}

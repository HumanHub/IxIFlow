namespace IxIFlow.Core;

/// <summary>
/// Determines when a parallel step may continue past its branches.
/// </summary>
public enum ParallelJoinMode
{
    WaitAll,
    WaitAny,
    WaitConditionally
}

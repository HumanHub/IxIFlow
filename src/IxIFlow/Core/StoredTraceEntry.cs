namespace IxIFlow.Core;

/// <summary>A trace entry as a store keeps it, numbered from 1 in the order its commits appended it (G-01).</summary>
public sealed record StoredTraceEntry(long Sequence, ExecutionTraceEntry Entry);

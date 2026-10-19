namespace IxIFlow.Core;

/// <summary>A trace entry as a store keeps it, numbered from 1 in the order its commits appended it.</summary>
public sealed record StoredTraceEntry(long Sequence, ExecutionTraceEntry Entry)
{
    /// <summary>The writing commit's revision; standalone inbox refusals carry the observed revision.</summary>
    public long Revision { get; init; }
    /// <summary>The writing commit's final sequence, even when a page ends earlier. Null for standalone inbox appends.</summary>
    public long? CommitEndSequence { get; init; }
    /// <summary>The status saved by the writing commit. Null for standalone inbox appends.</summary>
    public WorkflowStatus? CommitStatus { get; init; }
}

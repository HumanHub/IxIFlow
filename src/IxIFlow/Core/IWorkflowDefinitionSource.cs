namespace IxIFlow.Core;

/// <summary>Read-only definition access on the executor side of the boundary.</summary>
public interface IWorkflowDefinitionSource
{
    /// <summary>Acquires the exact saved bundle, or the local code definition when the digest is null.</summary>
    Task<WorkflowDefinitionLease> AcquireWorkflowDefinitionAsync(string name, int version, ArtifactDigest? bundleDigest, CancellationToken cancellationToken = default);
    /// <summary>Resolves a new child/start against the current immutable catalog version.</summary>
    Task<WorkflowDefinitionLease> ResolveWorkflowDefinitionAsync(string name, int version, CancellationToken cancellationToken = default);
}


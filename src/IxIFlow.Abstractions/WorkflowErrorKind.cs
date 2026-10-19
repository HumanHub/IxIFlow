namespace IxIFlow.Core;

/// <summary>
/// Classifies an engine failure by what the caller should do about it.
/// </summary>
public enum WorkflowErrorKind
{
    /// <summary>The workflow definition is invalid. Fix the definition.</summary>
    InvalidDefinition,

    /// <summary>The request is not valid for this engine or workflow. Fix the call.</summary>
    InvalidRequest,

    /// <summary>The instance's state does not allow the operation, or the saved state is inconsistent.</summary>
    InvalidState,

    /// <summary>
    /// A workflow version, type or converter the instance needs is not available in this host.
    /// The instance stays recoverable until the artifact is deployed.
    /// </summary>
    ArtifactUnavailable,

    /// <summary>The state store or another dependency failed. The instance stays recoverable.</summary>
    Infrastructure,

    /// <summary>A configured limit was reached.</summary>
    ResourceExhausted,

    /// <summary>Retention removed the instance; its ID is still reserved by a tombstone.</summary>
    InstanceRemoved,

    /// <summary>No live host advertises all demands at start admission.</summary>
    Unschedulable
}

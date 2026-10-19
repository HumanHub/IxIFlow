namespace IxIFlow.Core;

/// <summary>The host and executor must use the same message contract.</summary>
public static class ExecutionContract
{
    public const int Version = 10;

    public static void RequireVersion(int version)
    {
        if (version != Version)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                $"Execution contract version {version} is incompatible with version {Version}");
    }
}

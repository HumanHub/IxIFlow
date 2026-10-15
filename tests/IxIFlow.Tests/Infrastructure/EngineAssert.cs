using IxIFlow.Core;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>Asserts how the engine classifies its own failures.</summary>
public static class EngineAssert
{
    public static WorkflowEngineException Fails(WorkflowErrorKind kind, Action call)
    {
        var error = Assert.Throws<WorkflowEngineException>(call);
        Assert.Equal(kind, error.Kind);
        return error;
    }

    public static async Task<WorkflowEngineException> FailsAsync(WorkflowErrorKind kind, Func<Task> call)
    {
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(call);
        Assert.Equal(kind, error.Kind);
        return error;
    }

    /// <summary>A store failure interrupts the instance and keeps the store's own error.</summary>
    public static async Task<WorkflowEngineException> StoreFailureAsync(Func<Task> call)
    {
        var error = await FailsAsync(WorkflowErrorKind.Infrastructure, call);
        Assert.IsType<IOException>(error.InnerException);
        return error;
    }
}

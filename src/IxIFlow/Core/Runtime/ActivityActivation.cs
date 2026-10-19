using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Core.Runtime;

/// <summary>Creates each activity object with its constructor dependencies from the run's scope.</summary>
internal static class ActivityActivation
{
    public static IAsyncActivity Create(Type activityType, IServiceProvider services)
    {
        try
        {
            return (IAsyncActivity)ActivatorUtilities.CreateInstance(services, activityType);
        }
        catch (Exception error)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Activity '{activityType.Name}' cannot be created in this host: {error.Message}", error);
        }
    }
}

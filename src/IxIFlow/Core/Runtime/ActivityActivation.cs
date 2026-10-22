using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Core.Runtime;

/// <summary>Creates each activity object with its constructor dependencies from the run's scope.</summary>
internal static class ActivityActivation
{
    private static readonly ConditionalWeakTable<object, object> Used = new();
    private static readonly object Sentinel = new();

    public static IAsyncActivity Create(Type activityType, IServiceProvider services, IActivityRegistry registry)
    {
        IAsyncActivity activity;
        try
        {
            if (!registry.IsRegistered(activityType))
                return (IAsyncActivity)ActivatorUtilities.CreateInstance(services, activityType);
            activity = (IAsyncActivity)services.GetRequiredService(activityType);
        }
        catch (Exception error)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Activity '{activityType.Name}' cannot be created in this host: {error.Message}", error);
        }
        if (!Used.TryAdd(activity, Sentinel))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Activity factory for '{activityType.Name}' returned a previously used instance");
        return activity;
    }
}

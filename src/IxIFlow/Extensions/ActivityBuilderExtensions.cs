using System.Reflection;
using IxIFlow.Core;
using IxIFlow.ActivitySdk;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Extensions;

/// <summary>Registers installed activity code and its declaration contracts.</summary>
public static class ActivityBuilderExtensions
{
    /// <summary>Registers every attributed activity in a default-context assembly, refusing invalid declarations.</summary>
    public static IIxIFlowBuilder AddActivities(this IIxIFlowBuilder builder, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ActivityRegistrations.CheckAssembly(assembly);
        Type[] types;
        try { types = assembly.GetTypes().Where(type => type.IsDefined(typeof(WorkflowActivityAttribute))).ToArray(); }
        catch (ReflectionTypeLoadException error)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Activity assembly '{assembly.GetName().Name}' could not load its types: {error.LoaderExceptions.FirstOrDefault()?.Message}", error);
        }
        if (types.Length == 0)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Assembly '{assembly.GetName().Name}' has no [WorkflowActivity] types");
        Registrations(builder.Services).Add(types);
        return builder;
    }

    /// <summary>Registers one attributed activity. Disposable transient instances live until the run scope ends.</summary>
    public static IIxIFlowBuilder AddActivity<TActivity>(this IIxIFlowBuilder builder) where TActivity : class, IAsyncActivity
    {
        Registrations(builder.Services).Add([typeof(TActivity)]);
        return builder;
    }

    internal static ActivityRegistrations Registrations(IServiceCollection services)
    {
        var registrations = services.FirstOrDefault(service => service.ServiceType == typeof(ActivityRegistrations))?
            .ImplementationInstance as ActivityRegistrations;
        if (registrations != null) return registrations;
        registrations = new(services);
        services.AddSingleton(registrations);
        services.AddSingleton<IActivityRegistry>(_ => registrations.Build());
        return registrations;
    }
}

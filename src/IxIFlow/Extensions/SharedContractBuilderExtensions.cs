using System.Reflection;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Extensions;

public static class SharedContractBuilderExtensions
{
    /// <summary>Declares default-context application contracts before the host first uses its frozen shared profile.</summary>
    public static IIxIFlowBuilder ShareContracts(this IIxIFlowBuilder builder, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(assemblies);

        var registration =
            builder.Services.LastOrDefault(item => item.ServiceType == typeof(SharedContractRegistration))?.ImplementationInstance as
                SharedContractRegistration;

        if (registration == null)
            builder.Services.AddSingleton(registration = new SharedContractRegistration());

        registration.Add(assemblies);

        return builder;
    }
}
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Samples;

public static class Coordinators
{
    // snippet: service
    public static IHost CreateCoordinator(string connectionString)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddIxIFlow()
            .UsePostgreSql(connectionString)
            .AddCoordinator(options =>
            {
                options.ListenEndpoint = new Uri("http://127.0.0.1:5088");
                options.AllowInsecure = true;
            });
        return builder.Build();
    }
    // end-snippet: service

    // snippet: links
    public static void LinkHost(IServiceCollection services, string connectionString)
    {
        services.AddIxIFlow(options => options.Slots = 8)
            .UsePostgreSql(connectionString)
            .UseCoordinators(options =>
            {
                options.AllowInsecure = true;
                // Optional: registry discovery already connects to every live coordinator.
                options.Coordinators.Add(new Uri("http://127.0.0.1:5088"));
            });
    }
    // end-snippet: links
}

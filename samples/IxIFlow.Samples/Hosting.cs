using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Samples;

public static class Hosting
{
    /// <summary>Choose one store on the builder returned by AddIxIFlow. Memory is the default.</summary>
    public static IServiceCollection Services(string store = "memory", string? connectionString = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var flow = services.AddIxIFlow(options => options.Slots = 8);
        switch (store)
        {
            case "memory": break;
            case "sqlite": flow.UseSqlite(connectionString!); break;
            case "sqlserver": flow.UseSqlServer(connectionString!); break;
            case "postgresql": flow.UsePostgreSql(connectionString!); break;
            default: throw new ArgumentException("Unknown store", nameof(store));
        }
        return services;
    }

    /// <summary>The generic host starts recovery scanning and drains running work when it stops.</summary>
    public static IHost CreateHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddIxIFlow(options => options.Slots = 8);
        return builder.Build();
    }

    public static async Task<WorkflowExecutionResult> RunHostedAsync()
    {
        using var host = CreateHost();
        await host.StartAsync();
        try
        {
            return await host.Services.GetRequiredService<IWorkflowClient>()
                .RunAsync(Quote.Definition(), new QuoteData());
        }
        finally
        {
            await host.StopAsync();
        }
    }
}

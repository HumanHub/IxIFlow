using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Samples;

public static class ReplyTokenExamples
{
    public static void Configure(IServiceCollection services, IConfiguration configuration)
    {
        // snippet: reply-token-keys
        var current = Convert.FromBase64String(configuration["Workflow:ReplyKeys:Current"]!);
        var previous = configuration.GetSection("Workflow:ReplyKeys:Previous").GetChildren()
            .Select(key => Convert.FromBase64String(key.Value!)).ToArray();
        services.AddIxIFlow(options => options.ReplyTokenKeys = new ReplyTokenKeyRing(current, previous));
        // end-snippet: reply-token-keys
    }
}

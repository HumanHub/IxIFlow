using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using IxIFlow.Core;

namespace IxIFlow.Samples;

public static class Contracts
{
    /// <summary>Tools and applications can validate a definition before starting it.</summary>
    public static WorkflowDefinition ValidateQuote()
    {
        var definition = Quote.Definition();
        WorkflowDefinitionValidator.Validate(definition);
        return definition;
    }

    /// <summary>The catalog exposes the same input, output and designer metadata as the activity contract.</summary>
    public static IActivityRegistry Catalog()
    {
        var services = new ServiceCollection();
        // snippet: register-activities
        services.AddIxIFlow()
            .AddActivities(typeof(AddFee).Assembly);
        // To select one activity instead:
        // services.AddIxIFlow().AddActivity<AddFee>();
        // end-snippet: register-activities
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IActivityRegistry>();
    }
}
